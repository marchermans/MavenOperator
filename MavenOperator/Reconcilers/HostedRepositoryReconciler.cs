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
/// Full Phase 1 implementation of the Hosted repository reconciler.
/// Steps (all idempotent):
///   1. EnsurePvc
///   2. EnsureHtpasswdSecrets  (download + upload, separate files)
///   3. EnsureNginxConfigMap
///   4. EnsureDeployment
///   5. EnsureService
///   6. EnsureIngress (when enabled)
/// </summary>
public sealed class HostedRepositoryReconciler(
    IKubernetesClient k8s,
    IKubernetesResourceManager resources,
    IHtpasswdService htpasswd,
    IRoleBasedHtpasswdService roleBasedHtpasswd,
    IAuthProxyConfigRenderer authProxyConfig,
    INginxConfigRenderer nginx,
    IKubernetesEventService events,
    ILogger<HostedRepositoryReconciler> logger)
    : IHostedRepositoryReconciler
{
    private static string AuthProxyImage =>
        Environment.GetEnvironmentVariable("AUTH_PROXY_IMAGE") ?? "maven-auth-proxy:dev";
    private const string NginxImage     = "nginx:1.27-alpine";
    private const string AuthProxyMountPath = "/etc/maven-auth";
    private const int AuthProxyPort = 8080;
    private const string RepositoryPath = "/var/maven/repository";
    private const string AuthPath       = "/etc/nginx/auth";
    private const string ConfPath       = "/etc/nginx/conf.d";

    public async Task ReconcileAsync(MavenRepositoryV1Alpha1 entity, CancellationToken ct)
    {
        var name = entity.Metadata.Name!;
        var ns   = entity.Metadata.NamespaceProperty!;
        var spec = entity.Spec;
        var repositoryPathPrefix = RepositoryPathHelper.ResolvePathPrefix(spec, name);

        logger.LogInformation("[Hosted] Reconciling {Namespace}/{Name}", ns, name);
        await events.PublishAsync(entity, "Provisioning", $"Reconciling Hosted repository '{name}'", ct: ct);

        // 1 ── PVC ─────────────────────────────────────────────────────────────
        var storage  = spec.Storage ?? new StorageSpec();
        var pvcName  = $"{name}-pvc";
        // DeletionPolicy=Retain → no owner reference (PVC survives CRD deletion)
        var setOwner = storage.DeletionPolicy == DeletionPolicy.Delete;
        var accessMode = storage.AccessMode;

        await resources.EnsurePvcAsync(entity, pvcName, storage.Size, accessMode,
            storage.StorageClassName, setOwner, ct);

        entity.Status.SetCondition("StorageBound", isTrue: true,
            reason: "PVCEnsured", message: $"PVC {pvcName} ensured");

        // 2 ── htpasswd Secrets ────────────────────────────────────────────────
        var downloadUsesAuthProxy = spec.Auth.Download.CiTrust.Count > 0
                                    || spec.Auth.Download.Acls.Count > 0;
        var uploadUsesAuthProxy = spec.Auth.Upload.CiTrust.Count > 0
                                  || spec.Auth.Upload.Acls.Count > 0;

        var downloadHtpasswd = await BuildHtpasswdAsync(spec.Auth.Download, ns, ct);
        var uploadHtpasswd = await BuildHtpasswdAsync(spec.Auth.Upload, ns, ct);

        await resources.EnsureSecretAsync(entity, $"{name}-download-htpasswd",
            new Dictionary<string, string> { ["download.htpasswd"] = downloadHtpasswd }, ct);

        await resources.EnsureSecretAsync(entity, $"{name}-upload-htpasswd",
            new Dictionary<string, string> { ["upload.htpasswd"] = uploadHtpasswd }, ct);

        entity.Status.SetCondition("AuthReady", isTrue: true,
            reason: "HtpasswdGenerated",
            message: $"{spec.Auth.Download.Users.Count} download user(s), {spec.Auth.Upload.Users.Count} upload user(s) configured");
        await events.PublishAsync(entity, "AuthUpdated",
            $"htpasswd rebuilt: {spec.Auth.Download.Users.Count} download, {spec.Auth.Upload.Users.Count} upload user(s)",
            ct: ct);

        // 3 ── NGINX ConfigMap ─────────────────────────────────────────────────
        var nginxConfig  = nginx.RenderHosted(name, spec.Auth.Download.Policy, spec.Auth.Upload.Policy, 
            spec.Metrics, downloadUsesAuthProxy, uploadUsesAuthProxy, repositoryPathPrefix);
        var configMapName = $"{name}-nginx-cm";

        await resources.EnsureConfigMapAsync(entity, configMapName,
            new Dictionary<string, string> { ["default.conf"] = nginxConfig }, ct);

        string authProxyConfigJson = string.Empty;
        var useAuthProxy = downloadUsesAuthProxy || uploadUsesAuthProxy;
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
        var configHash    = ComputeHash(nginxConfig + downloadHtpasswd + uploadHtpasswd + authProxyConfigJson);
        var deployName    = $"{name}-nginx";
        var podSpec       = BuildPodSpec(name, spec, useAuthProxy);

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
            reason: "DeploymentEnsured", message: "NGINX deployment ensured");

        // 6 ── Ingress or Gateway API (optional) ──────────────────────────────
        if (spec.Ingress.Enabled)
        {
            await resources.EnsureIngressAsync(entity, $"{name}-ingress", $"{name}-svc", spec.Ingress, name, ct);

            if (spec.Ingress.CertManager is not null)
            {
                await resources.EnsureCertificateAsync(
                    entity, $"{name}-ingress-cert", spec.Ingress.Host ?? name, spec.Ingress.CertManager, ct);
            }

            entity.Status.SetCondition("IngressReady", isTrue: true,
                reason: "IngressEnsured", message: $"Ingress for host '{spec.Ingress.Host}' ensured");
            // Set URL from Ingress
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

            if (httpRouteCreated && spec.Gateway.CertManager is not null)
            {
                await resources.EnsureCertificateAsync(
                    entity, $"{name}-cert", spec.Gateway.Hostname ?? name, spec.Gateway.CertManager, ct);

                entity.Status.SetCondition("GatewayReady", isTrue: true,
                    reason: "HTTPRouteEnsured", message: $"HTTPRoute for hostname '{spec.Gateway.Hostname}' ensured");
            }

            // Set URL from Gateway
            var gatewayPath = spec.Gateway.Path ?? repositoryPathPrefix;
            var tls = !string.IsNullOrWhiteSpace(spec.Gateway.TlsSecretRef) || spec.Gateway.CertManager?.AutoCreate == true;
            var scheme = tls ? "https" : "http";
            entity.Status.Url = spec.Gateway.Hostname is not null
                ? $"{scheme}://{spec.Gateway.Hostname}{gatewayPath}"
                : gatewayPath;
        }
        else
        {
            // Use cluster-internal URL
            entity.Status.Url = RepositoryPathHelper.BuildInternalRepositoryUrl($"{name}-svc", repositoryPathPrefix);
        }

        // 7 ── Cleanup resources no longer required by spec ────────────────────
        await CleanupObsoleteResourcesAsync(name, ns, spec, useAuthProxy, ct);

        logger.LogInformation("[Hosted] {Namespace}/{Name} reconciled successfully", ns, name);
        await events.PublishAsync(entity, "Ready", $"Hosted repository '{name}' is ready at {entity.Status.Url}", ct: ct);
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
        bool useAuthProxy,
        CancellationToken ct)
    {
        // ── Ingress: delete if no longer enabled ──────────────────────────────
        if (!spec.Ingress.Enabled)
        {
            await resources.DeleteResourceIfExistsAsync<V1Ingress>($"{name}-ingress", ns, ct);
            // Also remove the CertManager Certificate that may have been created for the Ingress
            await resources.DeleteCustomResourceIfExistsAsync(
                "cert-manager.io", "v1", "certificates", $"{name}-ingress-cert", ns, ct);
        }

        // ── Gateway / HTTPRoute: delete if no longer enabled ──────────────────
        if (!spec.Gateway.Enabled)
        {
            await resources.DeleteCustomResourceIfExistsAsync(
                "gateway.networking.k8s.io", "v1", "httproutes", $"{name}-route", ns, ct);
            // Also remove the CertManager Certificate that may have been created for the Gateway
            await resources.DeleteCustomResourceIfExistsAsync(
                "cert-manager.io", "v1", "certificates", $"{name}-cert", ns, ct);
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
    }

    /// <summary>
    /// Reads each credential Secret from the cluster, extracts username+password,
    /// and returns a combined htpasswd file content.
    /// If the policy is Anonymous (no secretRefs), returns an empty string.
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
            var secret = await k8s.GetAsync<k8s.Models.V1Secret>(secretRef, ns, ct)
                ?? throw new InvalidOperationException(
                    $"Credential Secret '{secretRef}' not found in namespace '{ns}'. " +
                    $"Create it with 'username' and 'password' keys.");

            var username = GetSecretKey(secret, "username", secretRef);
            var password = GetSecretKey(secret, "password", secretRef);
            credentials.Add((username, password));
        }

        return htpasswd.BuildHtpasswd(credentials.DistinctBy(c => c.Item1));
    }

    private static string GetSecretKey(k8s.Models.V1Secret secret, string key, string secretName)
    {
        if (secret.Data?.TryGetValue(key, out var bytes) == true)
            return Encoding.UTF8.GetString(bytes);

        throw new InvalidOperationException(
            $"Credential Secret '{secretName}' is missing the required key '{key}'. " +
            $"Each credential Secret must have 'username' and 'password' keys.");
    }

    private static V1PodSpec BuildPodSpec(string name, MavenRepositorySpec spec, bool useAuthProxy)
    {
        var res = spec.Resources is not null
            ? new V1ResourceRequirements
            {
                Requests = spec.Resources.Requests,
                Limits   = spec.Resources.Limits,
            }
            : new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new("100m"), ["memory"] = new("128Mi"),
                },
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new("500m"), ["memory"] = new("512Mi"),
                },
            };

        var nginxVolumeMounts = new List<V1VolumeMount>
        {
            new() { Name = "repository",  MountPath = RepositoryPath },
            new() { Name = "nginx-conf",  MountPath = ConfPath, ReadOnlyProperty = true },
            new() { Name = "download-auth", MountPath = AuthPath, ReadOnlyProperty = true },
            new() { Name = "upload-auth",   MountPath = "/etc/nginx/upload-auth", ReadOnlyProperty = true },
        };

        var volumes = new List<V1Volume>
        {
            new() { Name = "repository", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = $"{name}-pvc" } },
            new() { Name = "nginx-conf",    ConfigMap = new V1ConfigMapVolumeSource { Name = $"{name}-nginx-cm" } },
            new() { Name = "download-auth", Secret    = new V1SecretVolumeSource { SecretName = $"{name}-download-htpasswd" } },
            new() { Name = "upload-auth",   Secret    = new V1SecretVolumeSource { SecretName = $"{name}-upload-htpasswd" } },
        };

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
                LivenessProbe   = new V1Probe { HttpGet = new V1HTTPGetAction { Path = "/healthz", Port = 80 }, InitialDelaySeconds = 10, PeriodSeconds = 15, FailureThreshold = 6 },
                ReadinessProbe  = new V1Probe { HttpGet = new V1HTTPGetAction { Path = "/healthz", Port = 80 }, InitialDelaySeconds = 5,  PeriodSeconds = 5,  FailureThreshold = 6 },
            },
        };

        if (spec.Metrics.Enabled)
        {
            // nginx-logs emptyDir shared between NGINX and mtail
            volumes.Add(new V1Volume { Name = "nginx-logs", EmptyDir = new V1EmptyDirVolumeSource() });
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
                    new V1VolumeMount { Name = "nginx-logs",   MountPath = "/var/log/nginx",  ReadOnlyProperty = true },
                    new V1VolumeMount { Name = "mtail-config", MountPath = "/etc/mtail",       ReadOnlyProperty = true },
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



