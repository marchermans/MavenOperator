using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;
using MavenOperator.Services;
using System.Security.Cryptography;
using System.Text;

namespace MavenOperator.Reconcilers;

/// <summary>
/// Full Phase 2 implementation of the Proxy repository reconciler.
///
/// Steps (all idempotent):
///   1. EnsureDownloadHtpasswdSecret
///   2. EnsureNginxConfigMap          (proxy_pass template with upstream credentials)
///   3. EnsureDeployment              (NGINX with emptyDir cache volume)
///   4. EnsureService
///   5. EnsureIngress (when enabled)
///
/// No PVC is created — the proxy cache uses an emptyDir. An optional PVC cache
/// is deferred to Phase 4 hardening.
/// </summary>
public sealed class ProxyRepositoryReconciler(
    IKubernetesClient k8s,
    IKubernetesResourceManager resources,
    IHtpasswdService htpasswd,
    IRoleBasedHtpasswdService roleBasedHtpasswd,
    IAuthProxyConfigRenderer authProxyConfig,
    INginxConfigRenderer nginx,
    IKubernetesEventService events,
    ILogger<ProxyRepositoryReconciler> logger)
    : IProxyRepositoryReconciler
{
    private static string AuthProxyImage =>
        Environment.GetEnvironmentVariable("AUTH_PROXY_IMAGE") ?? "maven-auth-proxy:dev";
    private const string NginxImage = "nginx:1.27-alpine";
    private const string AuthProxyMountPath = "/etc/maven-auth";
    private const int AuthProxyPort = 8080;
    private const string AuthPath   = "/etc/nginx/auth";
    private const string ConfPath   = "/etc/nginx/conf.d";
    private const string CachePath  = "/var/cache/nginx";

    public async Task ReconcileAsync(MavenRepositoryV1Alpha1 entity, CancellationToken ct)
    {
        var name = entity.Metadata.Name!;
        var ns   = entity.Metadata.NamespaceProperty!;
        var spec = entity.Spec;
        var repositoryPathPrefix = RepositoryPathHelper.ResolvePathPrefix(spec, name);

        if (spec.Upstream is null)
            throw new InvalidOperationException(
                $"MavenRepository '{name}' has type Proxy but spec.upstream is not set.");

        logger.LogInformation("[Proxy] Reconciling {Namespace}/{Name} → {Upstream}",
            ns, name, spec.Upstream.Url);
        await events.PublishAsync(entity, "Provisioning", $"Reconciling Proxy repository '{name}' → {spec.Upstream.Url}", ct: ct);

        // 1 ── Download htpasswd Secret ────────────────────────────────────────
        var downloadUsesAuthProxy = spec.Auth.Download.CiTrust.Count > 0
                                    || spec.Auth.Download.Acls.Count > 0;
        var useAuthProxy = downloadUsesAuthProxy;
        var downloadHtpasswd = await BuildHtpasswdAsync(spec.Auth.Download, ns, ct);

        await resources.EnsureSecretAsync(entity, $"{name}-download-htpasswd",
            new Dictionary<string, string> { ["download.htpasswd"] = downloadHtpasswd }, ct);

        // 1a ── Upload htpasswd Secret (for Passthrough mode) ──────────────────
        var uploadNeedsHtpasswd = spec.Upstream.Upload.Enabled
                                  && spec.Upstream.Upload.Mode == ProxyUploadMode.Passthrough
                                  && spec.Auth.Upload.Policy == AuthPolicy.Authenticated;
        if (uploadNeedsHtpasswd)
        {
            var uploadHtpasswd = await BuildHtpasswdAsync(spec.Auth.Upload, ns, ct);
            await resources.EnsureSecretAsync(entity, $"{name}-upload-htpasswd",
                new Dictionary<string, string> { ["upload.htpasswd"] = uploadHtpasswd }, ct);
        }

        entity.Status.SetCondition("AuthReady", isTrue: true,
            reason: "HtpasswdGenerated",
            message: $"{spec.Auth.Download.Users.Count} download user(s) configured");
        await events.PublishAsync(entity, "AuthUpdated",
            $"htpasswd rebuilt: {spec.Auth.Download.Users.Count} download user(s)", ct: ct);

        // 1b ── Persistent proxy cache PVC (optional) ──────────────────────────
        var usePvcCache = !string.IsNullOrWhiteSpace(spec.Upstream.CachePvcSize);
        if (usePvcCache)
        {
            var cachePvcName = $"{name}-cache-pvc";
            //Hard code the access mode to ReadWriteMany to support HA mode running.
            await resources.EnsurePvcAsync(entity, cachePvcName, spec.Upstream.CachePvcSize!, "ReadWriteMany",
                storageClassName: null, setOwnerReference: true, ct: ct);
            entity.Status.SetCondition("CacheReady", isTrue: true,
                reason: "PvcCacheEnsured", message: $"PVC cache {cachePvcName} ({spec.Upstream.CachePvcSize}) ensured");
        }
        else
        {
            entity.Status.SetCondition("CacheReady", isTrue: true,
                reason: "EmptyDirCache", message: "Using ephemeral emptyDir proxy cache");
        }

        // 2 ── Upstream auth header (if upstream credentials are configured) ───
        var upstreamAuthHeader = await BuildUpstreamAuthHeaderAsync(entity, spec.Upstream, ns, ct);

        // 2b ── Upload forwarding configuration ────────────────────────────────
        var uploadSpec = spec.Upstream.Upload;
        bool uploadEnabled = false;
        ProxyUploadMode uploadMode = ProxyUploadMode.Passthrough;
        AuthPolicy uploadPolicy = spec.Auth.Upload.Policy;
        string upstreamUploadAuthHeader = string.Empty;
        bool uploadCredentialsConfigured = true;
        string? uploadSyncError = null;

        if (uploadSpec.Enabled)
        {
            // Check external exposure before allowing uploads
            var isExternallyExposed = await IsProxyExternallyExposedAsync(entity, spec, ns, ct);
            if (isExternallyExposed && !uploadSpec.ForceAllowOnExternal)
            {
                entity.Status.SetCondition("UploadForbidden", isTrue: true,
                    reason: "ExternallyExposedProxy",
                    message: "Upload forwarding blocked — proxy is externally exposed. Set forceAllowOnExternal: true to override.");
                // KubeOps PublishAsync(entity, reason, message, type) — keep order intact so
                // .reason/.type land in the right fields (verified against live events).
                await events.PublishAsync(entity, "UploadBlockedExternallyExposed",
                    $"Upload forwarding disabled for '{name}' because the proxy is externally exposed without forceAllowOnExternal.",
                    type: "Warning", ct: ct);

                // Still render NGINX config with uploads disabled
            }
            else
            {
                uploadEnabled = true;
                uploadMode = uploadSpec.Mode;

                // Load upstream credentials for upload forwarding
                if (uploadSpec.UpstreamCredentialsRef is not null && !string.IsNullOrWhiteSpace(uploadSpec.UpstreamCredentialsRef.Name))
                {
                    try
                    {
                        var credsNs = string.IsNullOrWhiteSpace(uploadSpec.UpstreamCredentialsRef.Namespace) ? ns : uploadSpec.UpstreamCredentialsRef.Namespace;
                        upstreamUploadAuthHeader = await BuildUpstreamAuthHeaderFromSecretAsync(
                            entity, uploadSpec.UpstreamCredentialsRef.Name, credsNs, "upload", ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        uploadSyncError = $"Failed to load upload credentials Secret '{uploadSpec.UpstreamCredentialsRef.Name}': {ex.Message}";
                        uploadCredentialsConfigured = false;
                        entity.Status.SetCondition("UploadConfigurationError", isTrue: true,
                            reason: "MissingCredentialsSecret",
                            message: uploadSyncError);
                        await events.PublishAsync(entity, "UpstreamCredentialsMissing",
                            $"Upload credentials Secret '{uploadSpec.UpstreamCredentialsRef.Name}' not found or unreadable.",
                            type: "Warning", ct: ct);

                        // Disable uploads if credentials can't be loaded
                        uploadEnabled = false;
                    }
                }
                else
                {
                    uploadSyncError = "upstreamCredentialsRef.name is required when upload.enabled is true";
                    uploadCredentialsConfigured = false;
                    entity.Status.SetCondition("UploadConfigurationError", isTrue: true,
                        reason: "MissingCredentialsSecret",
                        message: uploadSyncError);
                    await events.PublishAsync(entity, "UpstreamCredentialsMissing",
                        $"upload.upstreamCredentialsRef.name not set for '{name}'.",
                        type: "Warning", ct: ct);

                    uploadEnabled = false;
                }
            }
        }

        // Set upload status (only when spec has uploads configured)
        if (spec.Upstream.Upload.Enabled || uploadSyncError != null)
        {
            entity.Status.Upload ??= new UploadStatus();
            entity.Status.Upload.Proxy = new ProxyUploadStatus
            {
                Enabled = uploadEnabled,
                Mode = uploadMode,
                UpstreamCredentialsConfigured = uploadCredentialsConfigured,
                LastSyncError = uploadSyncError,
            };

            if (uploadEnabled && string.IsNullOrEmpty(uploadSyncError))
            {
                entity.Status.SetCondition("UploadReady", isTrue: true,
                    reason: "UploadForwardingEnabled",
                    message: $"Upload forwarding enabled with mode={uploadMode}");
                await events.PublishAsync(entity, "UploadEnabled",
                    $"Upload forwarding enabled for '{name}' (mode={uploadMode})",
                    type: "Normal", ct: ct);
            }
        }

        // 3 ── NGINX ConfigMap ─────────────────────────────────────────────────
        var nginxConfig   = nginx.RenderProxy(
            name,
            spec.Auth.Download.Policy,
            spec.Upstream.Url,
            spec.Upstream.CacheTtl,
            upstreamAuthHeader,
            spec.Metrics,
            downloadUsesAuthProxy,
            repositoryPathPrefix,
            uploadEnabled: uploadEnabled,
            uploadMode: uploadMode,
            uploadPolicy: uploadPolicy,
            upstreamUploadAuthHeader: upstreamUploadAuthHeader);

        var configMapName = $"{name}-nginx-cm";
        await resources.EnsureConfigMapAsync(entity, configMapName,
            new Dictionary<string, string> { ["default.conf"] = nginxConfig }, ct);

        string authProxyConfigJson = string.Empty;
        if (useAuthProxy)
        {
            authProxyConfigJson = authProxyConfig.Render(spec.Auth);
            await resources.EnsureConfigMapAsync(entity, $"{name}-auth-proxy-cm",
                new Dictionary<string, string> { ["config.json"] = authProxyConfigJson }, ct);
            entity.Status.SetCondition("AuthProxyReady", isTrue: true,
                reason: "ConfigRendered", message: "Auth proxy config rendered from auth.download/auth.upload directional rules");
        }

        // 3b ── mtail ConfigMap (when metrics enabled) ─────────────────────────
        if (spec.Metrics.Enabled)
        {
            var mtailConfig = nginx.RenderMtailConfig();
            await resources.EnsureConfigMapAsync(entity, $"{name}-mtail-cm",
                new Dictionary<string, string> { ["maven.mtail"] = mtailConfig }, ct);
        }

        // 4 ── Deployment ──────────────────────────────────────────────────────
        var configHash = ComputeHash(nginxConfig + downloadHtpasswd + authProxyConfigJson);
        var deployName = $"{name}-nginx";
        var podSpec    = BuildPodSpec(name, spec, usePvcCache, useAuthProxy, uploadNeedsHtpasswd);

        await resources.EnsureDeploymentAsync(entity, deployName, configHash, podSpec, replicas: 1, ct);

        // 5 ── Service ─────────────────────────────────────────────────────────
        var servicePorts = BuildServicePorts(spec.Metrics);
        await resources.EnsureServiceWithPortsAsync(entity, $"{name}-svc", deployName, servicePorts, ct);

        if (spec.Metrics.Enabled)
        {
            var podMonitorEnsured = await resources.EnsurePodMonitorAsync(
                entity,
                $"{name}-metrics",
                deployName,
                spec.Metrics,
                ct);

            if (podMonitorEnsured)
            {
                entity.Status.SetCondition(
                    "MetricsScrapeReady",
                    isTrue: true,
                    reason: "PodMonitorEnsured",
                    message: $"PodMonitor '{name}-metrics' ensured.");
            }
        }

        entity.Status.SetCondition("Available", isTrue: true,
            reason: "DeploymentEnsured", message: "NGINX proxy deployment ensured");

        // 6 ── Ingress or Gateway API (optional) ──────────────────────────────
        if (spec.Ingress.Enabled)
        {
            await resources.EnsureIngressAsync(entity, $"{name}-ingress", $"{name}-svc", spec.Ingress, name, ct);

            entity.Status.SetCondition("IngressReady", isTrue: true,
                reason: "IngressEnsured", message: $"Ingress for host '{spec.Ingress.Host}' ensured");
            var ingressPath = spec.Ingress.Path ?? repositoryPathPrefix;
            var hasTls = spec.Ingress.TlsSecretRef is not null || spec.Ingress.CertManager?.AutoCreate == true;
            var scheme = hasTls ? "https" : "http";
            entity.Status.Url = spec.Ingress.Host is not null
                ? $"{scheme}://{spec.Ingress.Host}{ingressPath}"
                : ingressPath;
        }
        else if (spec.Gateway.Enabled)
        {
            var httpRouteCreated = await resources.EnsureHttpRouteAsync(
                entity, $"{name}-route", $"{name}-svc", 80, spec.Gateway, name, ct);

            if (httpRouteCreated)
            {
                entity.Status.SetCondition("GatewayReady", isTrue: true,
                    reason: "HTTPRouteEnsured", message: $"HTTPRoute for hostname '{spec.Gateway.Hostname}' ensured");
            }

            var gatewayPath = spec.Gateway.Path ?? repositoryPathPrefix;
            var tls = !string.IsNullOrWhiteSpace(spec.Gateway.TlsSecretRef) || spec.Gateway.CertManager?.AutoCreate == true;
            var scheme = tls ? "https" : "http";
            entity.Status.Url = spec.Gateway.Hostname is not null
                ? $"{scheme}://{spec.Gateway.Hostname}{gatewayPath}"
                : gatewayPath;
        }
        else
        {
            entity.Status.Url = RepositoryPathHelper.BuildInternalRepositoryUrl($"{name}-svc", repositoryPathPrefix);
        }

        // 7 ── Cleanup resources no longer required by spec ────────────────────
        await CleanupObsoleteResourcesAsync(name, ns, spec, usePvcCache, useAuthProxy, ct);

        logger.LogInformation("[Proxy] {Namespace}/{Name} reconciled successfully", ns, name);
        await events.PublishAsync(entity, "Ready", $"Proxy repository '{name}' is ready at {entity.Status.Url}", ct: ct);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Cleans up optional resources that are no longer required by the current spec.
    /// Called at the end of every reconcile so stale resources from previous specs are removed.
    /// </summary>
    private async Task CleanupObsoleteResourcesAsync(
        string name,
        string ns,
        MavenRepositorySpec spec,
        bool usePvcCache,
        bool useAuthProxy,
        CancellationToken ct)
    {
        // ── Ingress: delete if no longer enabled ──────────────────────────────
        if (!spec.Ingress.Enabled)
        {
            await resources.DeleteResourceIfExistsAsync<V1Ingress>($"{name}-ingress", ns, ct);
        }

        // ── Gateway / HTTPRoute: delete if no longer enabled ──────────────────
        if (!spec.Gateway.Enabled)
        {
            await resources.DeleteCustomResourceIfExistsAsync(
                "gateway.networking.k8s.io", "v1", "httproutes", $"{name}-route", ns, ct);
        }

        // ── Metrics: delete PodMonitor + mtail ConfigMap if no longer enabled ─
        if (!spec.Metrics.Enabled)
        {
            await resources.DeleteCustomResourceIfExistsAsync(
                "monitoring.coreos.com", "v1", "podmonitors", $"{name}-metrics", ns, ct);
            await resources.DeleteResourceIfExistsAsync<V1ConfigMap>($"{name}-mtail-cm", ns, ct);
        }

        // ── Auth proxy: delete its ConfigMap if no longer needed ──────────────
        if (!useAuthProxy)
        {
            await resources.DeleteResourceIfExistsAsync<V1ConfigMap>($"{name}-auth-proxy-cm", ns, ct);
        }

        // ── Proxy cache PVC: delete if persistent cache was removed from spec ─
        // The cache PVC was created with an owner reference, so it would survive
        // only until the CRD is deleted, but if cachePvcSize is removed from the spec
        // we clean it up eagerly because the pod no longer mounts it.
        if (!usePvcCache)
        {
            await resources.DeletePvcIfExistsAsync($"{name}-cache-pvc", ns, ct);
        }
    }

    /// <summary>
    /// Reads each credential Secret referenced by the policy and returns
    /// a combined htpasswd file content. Returns empty string for Anonymous policy.
    /// </summary>
    private async Task<string> BuildHtpasswdAsync(
        AuthPolicySpec policy,
        string ns,
        CancellationToken ct)
    {
        if (policy.Policy == AuthPolicy.Anonymous || policy.Users.Count == 0)
            return string.Empty;

        var credentials = new List<(string, string)>();

        foreach (var user in policy.Users.Where(u => !string.IsNullOrWhiteSpace(u.SecretRef)))
        {
            var secretRef = user.SecretRef;
            var secret = await k8s.GetAsync<V1Secret>(secretRef, ns, ct)
                ?? throw new InvalidOperationException(
                    $"Credential Secret '{secretRef}' not found in namespace '{ns}'.");

            var username = GetSecretKey(secret, "username", secretRef);
            var password = GetSecretKey(secret, "password", secretRef);
            credentials.Add((username, password));
        }

        return htpasswd.BuildHtpasswd(credentials.DistinctBy(c => c.Item1));
    }

    /// <summary>
    /// Reads the upstream credential Secret and returns a base64-encoded
    /// "Basic &lt;b64(user:pass)&gt;" header value, or empty string if no upstream auth.
    /// </summary>
    private async Task<string> BuildUpstreamAuthHeaderAsync(
        MavenRepositoryV1Alpha1 entity,
        UpstreamSpec upstream,
        string ns,
        CancellationToken ct)
    {
        if (upstream.Auth is null || string.IsNullOrWhiteSpace(upstream.Auth.SecretRef))
            return string.Empty;

        var secret = await k8s.GetAsync<V1Secret>(upstream.Auth.SecretRef, ns, ct)
            ?? throw new InvalidOperationException(
                $"Upstream credential Secret '{upstream.Auth.SecretRef}' not found in namespace '{ns}'.");

        var username = GetSecretKey(secret, "username", upstream.Auth.SecretRef);
        var password = GetSecretKey(secret, "password", upstream.Auth.SecretRef);
        var encoded  = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));

        return $"Basic {encoded}";
    }

    private static string GetSecretKey(V1Secret secret, string key, string secretName)
    {
        if (secret.Data?.TryGetValue(key, out var bytes) == true)
            return Encoding.UTF8.GetString(bytes);

        throw new InvalidOperationException(
            $"Credential Secret '{secretName}' is missing the required key '{key}'.");
    }

    /// <summary>
    /// Reads a credential Secret and returns a base64-encoded "Basic &lt;b64(user:pass)&gt;" header value.
    /// </summary>
    private async Task<string> BuildUpstreamAuthHeaderFromSecretAsync(
        MavenRepositoryV1Alpha1 entity,
        string secretName,
        string ns,
        string purpose,
        CancellationToken ct)
    {
        var secret = await k8s.GetAsync<V1Secret>(secretName, ns, ct)
            ?? throw new InvalidOperationException(
                $"Upload credential Secret '{secretName}' not found in namespace '{ns}'.");

        var username = GetSecretKey(secret, "username", secretName);
        var password = GetSecretKey(secret, "password", secretName);
        var encoded  = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));

        return $"Basic {encoded}";
    }

    /// <summary>
    /// Checks whether this proxy is externally exposed via Service type or Ingress/Gateway.
    /// </summary>
    private async Task<bool> IsProxyExternallyExposedAsync(
        MavenRepositoryV1Alpha1 entity,
        MavenRepositorySpec spec,
        string ns,
        CancellationToken ct)
    {
        // Explicit operator annotation marks the proxy as externally exposed
        // (e.g. fronted by an out-of-band Ingress/LoadBalancer we don't manage).
        if (entity.Metadata.Annotations is { } annotations &&
            annotations.TryGetValue("maven.operator.io/externally-exposed", out var exposed) &&
            string.Equals(exposed, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check if Ingress is enabled (routes external traffic to this proxy's Service)
        if (spec.Ingress.Enabled)
            return true;

        // Check if Gateway is enabled (creates HTTPRoute for external traffic)
        if (spec.Gateway.Enabled)
            return true;

        // Check the Service type — LoadBalancer and NodePort are externally exposed
        var serviceName = $"{entity.Metadata.Name}-svc";
        try
        {
            var service = await k8s.GetAsync<V1Service>(serviceName, ns, ct);
            if (service is not null)
            {
                var svcType = service.Spec?.Type;
                if (svcType == "LoadBalancer" || svcType == "NodePort")
                    return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[Proxy] Failed to check Service type for '{Name}' — assuming not externally exposed.", entity.Metadata.Name);
        }

        return false;
    }

    private static V1PodSpec BuildPodSpec(string name, MavenRepositorySpec spec, bool usePvcCache = false, bool useAuthProxy = false, bool uploadNeedsHtpasswd = false)
    {
        var res = spec.Resources is not null
            ? new V1ResourceRequirements { Requests = spec.Resources.Requests, Limits = spec.Resources.Limits }
            : new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new("128Mi") },
                Limits   = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("500m"), ["memory"] = new("512Mi") },
            };

        var nginxVolumeMounts = new List<V1VolumeMount>
        {
            new() { Name = "nginx-cache",    MountPath = CachePath },
            new() { Name = "nginx-conf",     MountPath = ConfPath,  ReadOnlyProperty = true },
            new() { Name = "download-auth",  MountPath = AuthPath,  ReadOnlyProperty = true },
        };

        var volumes = new List<V1Volume>
        {
            usePvcCache
                ? new V1Volume { Name = "nginx-cache", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = $"{name}-cache-pvc" } }
                : new V1Volume { Name = "nginx-cache", EmptyDir = new V1EmptyDirVolumeSource() },
            new() { Name = "nginx-conf",    ConfigMap = new V1ConfigMapVolumeSource { Name = $"{name}-nginx-cm" } },
            new() { Name = "download-auth", Secret    = new V1SecretVolumeSource { SecretName = $"{name}-download-htpasswd", Optional = true } },
        };

        // The upload htpasswd secret is consumed by nginx's per-method basic-auth gate in
        // Passthrough+Authenticated mode and, when an auth-proxy sidecar runs, by that
        // sidecar. Optional so a missing secret degrades to 403 on writes instead of
        // blocking Pod scheduling.
        if (uploadNeedsHtpasswd || useAuthProxy)
        {
            volumes.Add(new V1Volume { Name = "upload-auth", Secret = new V1SecretVolumeSource { SecretName = $"{name}-upload-htpasswd", Optional = true } });
        }

        if (uploadNeedsHtpasswd)
        {
            // nginx's limit_except gate reads /etc/nginx/upload-auth/upload.htpasswd.
            // Mounted as a directory (secret key == filename), NOT as a file subPath inside
            // the download-auth mount — binding a file into another volume-mounted dir fails
            // with ENOTDIR on some containerd/runc stacks.
            nginxVolumeMounts.Add(new V1VolumeMount
            {
                Name             = "upload-auth",
                MountPath        = "/etc/nginx/upload-auth",
                ReadOnlyProperty = true,
            });
        }

        if (useAuthProxy)
        {
            volumes.Add(new V1Volume { Name = "auth-proxy-config", ConfigMap = new V1ConfigMapVolumeSource { Name = $"{name}-auth-proxy-cm" } });
        }

        var containers = new List<V1Container>
        {
            new()
            {
                Name            = "nginx",
                Image           = NginxImage,
                ImagePullPolicy = "IfNotPresent",
                Ports           = [new V1ContainerPort { ContainerPort = 80, Name = "http" }],
                Resources       = res,
                VolumeMounts    = nginxVolumeMounts,
                LivenessProbe   = new V1Probe { HttpGet = new V1HTTPGetAction { Path = "/healthz", Port = 80 }, InitialDelaySeconds = 5,  PeriodSeconds = 15 },
                ReadinessProbe  = new V1Probe { HttpGet = new V1HTTPGetAction { Path = "/healthz", Port = 80 }, InitialDelaySeconds = 3,  PeriodSeconds = 10 },
            },
        };

        if (spec.Metrics.Enabled)
        {
            volumes.Add(new V1Volume { Name = "nginx-logs",   EmptyDir  = new V1EmptyDirVolumeSource() });
            volumes.Add(new V1Volume { Name = "mtail-config", ConfigMap = new V1ConfigMapVolumeSource { Name = $"{name}-mtail-cm" } });
            nginxVolumeMounts.Add(new V1VolumeMount { Name = "nginx-logs", MountPath = "/var/log/nginx" });

            var noPrivEscReadOnly = new V1SecurityContext
            {
                AllowPrivilegeEscalation = false,
                ReadOnlyRootFilesystem   = true,
                Capabilities             = new V1Capabilities { Drop = ["ALL"] },
            };

            // mtail writes runtime state under /tmp; keep least privilege but allow writable root.
            var noPrivEscWritableRoot = new V1SecurityContext
            {
                AllowPrivilegeEscalation = false,
                ReadOnlyRootFilesystem   = false,
                Capabilities             = new V1Capabilities { Drop = ["ALL"] },
            };

            containers.Add(new V1Container
            {
                Name            = "nginx-exporter",
                Image           = spec.Metrics.NginxExporterImage,
                ImagePullPolicy = "IfNotPresent",
                Args            = [$"--nginx.scrape-uri=http://127.0.0.1:{spec.Metrics.StubStatusPort}/stub_status"],
                Ports           = [new V1ContainerPort { ContainerPort = spec.Metrics.ExporterPort, Name = "nginx-metrics" }],
                Resources       = new V1ResourceRequirements
                {
                    Limits   = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("50m"),  ["memory"] = new("32Mi") },
                    Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"),  ["memory"] = new("16Mi") },
                },
                SecurityContext = noPrivEscReadOnly,
            });

            containers.Add(new V1Container
            {
                Name            = "mtail",
                Image           = spec.Metrics.MtailImage,
                ImagePullPolicy = "IfNotPresent",
                Args            =
                [
                    "--progs=/etc/mtail/maven.mtail",
                    "--logs=/var/log/nginx/access.json",
                    $"--port={spec.Metrics.MtailPort}",
                    "--expired_metrics_gc_interval=168h",
                    "--logtostderr",
                ],
                Ports        = [new V1ContainerPort { ContainerPort = spec.Metrics.MtailPort, Name = "mtail-metrics" }],
                VolumeMounts =
                [
                    new V1VolumeMount { Name = "nginx-logs",   MountPath = "/var/log/nginx", ReadOnlyProperty = true },
                    new V1VolumeMount { Name = "mtail-config", MountPath = "/etc/mtail",      ReadOnlyProperty = true },
                ],
                Resources = new V1ResourceRequirements
                {
                    Limits   = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new("64Mi") },
                    Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("20m"),  ["memory"] = new("32Mi") },
                },
                SecurityContext = noPrivEscWritableRoot,
            });
        }

        if (useAuthProxy)
        {
            containers.Add(new V1Container
            {
                Name = "maven-auth-proxy",
                Image = AuthProxyImage,
                ImagePullPolicy = "IfNotPresent",
                Ports = [new V1ContainerPort { ContainerPort = AuthProxyPort, Name = "auth-proxy" }],
                VolumeMounts =
                [
                    new V1VolumeMount { Name = "auth-proxy-config", MountPath = $"{AuthProxyMountPath}/config.json", SubPath = "config.json", ReadOnlyProperty = true },
                    new V1VolumeMount { Name = "download-auth", MountPath = $"{AuthProxyMountPath}/download.htpasswd", SubPath = "download.htpasswd", ReadOnlyProperty = true },
                    new V1VolumeMount { Name = "upload-auth", MountPath = $"{AuthProxyMountPath}/upload.htpasswd", SubPath = "upload.htpasswd", ReadOnlyProperty = true },
                ],
                LivenessProbe = new V1Probe { HttpGet = new V1HTTPGetAction { Path = "/healthz", Port = AuthProxyPort }, InitialDelaySeconds = 5, PeriodSeconds = 15 },
                ReadinessProbe = new V1Probe { HttpGet = new V1HTTPGetAction { Path = "/healthz", Port = AuthProxyPort }, InitialDelaySeconds = 3, PeriodSeconds = 10 },
                Resources = new V1ResourceRequirements
                {
                    Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("200m"), ["memory"] = new("256Mi") },
                    Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("25m"), ["memory"] = new("64Mi") },
                },
            });
        }

        return new V1PodSpec { Containers = containers, Volumes = volumes };
    }

    private static List<V1ServicePort> BuildServicePorts(MetricsSpec metrics)
    {
        var ports = new List<V1ServicePort>
        {
            new() { Name = "http", Port = 80, TargetPort = 80 },
        };
        if (metrics.Enabled)
        {
            ports.Add(new V1ServicePort { Name = "nginx-metrics", Port = metrics.ExporterPort, TargetPort = metrics.ExporterPort });
            ports.Add(new V1ServicePort { Name = "mtail-metrics",  Port = metrics.MtailPort,    TargetPort = metrics.MtailPort });
        }
        return ports;
    }

    /// <summary>SHA-256 of the combined config content — used as a pod restart trigger.</summary>
    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}

