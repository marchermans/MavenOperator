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
    INginxConfigRenderer nginx,
    IKubernetesEventService events,
    ILogger<ProxyRepositoryReconciler> logger)
    : IProxyRepositoryReconciler
{
    private const string NginxImage = "nginx:1.27-alpine";
    private const string AuthPath   = "/etc/nginx/auth";
    private const string ConfPath   = "/etc/nginx/conf.d";
    private const string CachePath  = "/var/cache/nginx";

    public async Task ReconcileAsync(MavenRepositoryV1Alpha1 entity, CancellationToken ct)
    {
        var name = entity.Metadata.Name!;
        var ns   = entity.Metadata.NamespaceProperty!;
        var spec = entity.Spec;

        if (spec.Upstream is null)
            throw new InvalidOperationException(
                $"MavenRepository '{name}' has type Proxy but spec.upstream is not set.");

        logger.LogInformation("[Proxy] Reconciling {Namespace}/{Name} → {Upstream}",
            ns, name, spec.Upstream.Url);
        await events.PublishAsync(entity, "Provisioning", $"Reconciling Proxy repository '{name}' → {spec.Upstream.Url}", ct: ct);

        // 1 ── Download htpasswd Secret ────────────────────────────────────────
        var downloadHtpasswd = await BuildHtpasswdAsync(entity, spec.Auth.Download, ns, ct);

        await resources.EnsureSecretAsync(entity, $"{name}-download-htpasswd",
            new Dictionary<string, string> { ["download.htpasswd"] = downloadHtpasswd }, ct);

        entity.Status.SetCondition("AuthReady", isTrue: true,
            reason: "HtpasswdGenerated",
            message: $"{spec.Auth.Download.SecretRefs.Count} download user(s) configured");
        await events.PublishAsync(entity, "AuthUpdated",
            $"htpasswd rebuilt: {spec.Auth.Download.SecretRefs.Count} download user(s)", ct: ct);

        // 1b ── Persistent proxy cache PVC (optional) ──────────────────────────
        var usePvcCache = !string.IsNullOrWhiteSpace(spec.Upstream.CachePvcSize);
        if (usePvcCache)
        {
            var cachePvcName = $"{name}-cache-pvc";
            await resources.EnsurePvcAsync(entity, cachePvcName, spec.Upstream.CachePvcSize!,
                storageClassName: null, setOwnerReference: true, ct);
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

        // 3 ── NGINX ConfigMap ─────────────────────────────────────────────────
        var nginxConfig   = nginx.RenderProxy(
            name,
            spec.Auth.Download.Policy,
            spec.Upstream.Url,
            spec.Upstream.CacheTtl,
            upstreamAuthHeader,
            spec.Metrics);

        var configMapName = $"{name}-nginx-cm";
        await resources.EnsureConfigMapAsync(entity, configMapName,
            new Dictionary<string, string> { ["default.conf"] = nginxConfig }, ct);

        // 3b ── mtail ConfigMap (when metrics enabled) ─────────────────────────
        if (spec.Metrics.Enabled)
        {
            var mtailConfig = nginx.RenderMtailConfig();
            await resources.EnsureConfigMapAsync(entity, $"{name}-mtail-cm",
                new Dictionary<string, string> { ["maven.mtail"] = mtailConfig }, ct);
        }

        // 4 ── Deployment ──────────────────────────────────────────────────────
        var configHash = ComputeHash(nginxConfig + downloadHtpasswd);
        var deployName = $"{name}-nginx";
        var podSpec    = BuildPodSpec(name, spec, usePvcCache);

        await resources.EnsureDeploymentAsync(entity, deployName, configHash, podSpec, replicas: 1, ct);

        // 5 ── Service ─────────────────────────────────────────────────────────
        var servicePorts = BuildServicePorts(spec.Metrics);
        await resources.EnsureServiceWithPortsAsync(entity, $"{name}-svc", deployName, servicePorts, ct);

        entity.Status.SetCondition("Available", isTrue: true,
            reason: "DeploymentEnsured", message: "NGINX proxy deployment ensured");

        // 6 ── Ingress (optional) ─────────────────────────────────────────────
        if (spec.Ingress.Enabled)
        {
            await resources.EnsureIngressAsync(entity, $"{name}-ingress", $"{name}-svc", spec.Ingress, name, ct);
            entity.Status.SetCondition("IngressReady", isTrue: true,
                reason: "IngressEnsured", message: $"Ingress for host '{spec.Ingress.Host}' ensured");
            var ingressPath = spec.Ingress.Path ?? $"/repository/{name}";
            var scheme = spec.Ingress.TlsSecretRef is not null ? "https" : "http";
            entity.Status.Url = spec.Ingress.Host is not null
                ? $"{scheme}://{spec.Ingress.Host}{ingressPath}"
                : ingressPath;
        }
        else
        {
            entity.Status.Url = $"http://{name}-svc/repository/{name}";
        }

        logger.LogInformation("[Proxy] {Namespace}/{Name} reconciled successfully", ns, name);
        await events.PublishAsync(entity, "Ready", $"Proxy repository '{name}' is ready at {entity.Status.Url}", ct: ct);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads each credential Secret referenced by the policy and returns
    /// a combined htpasswd file content. Returns empty string for Anonymous policy.
    /// </summary>
    private async Task<string> BuildHtpasswdAsync(
        MavenRepositoryV1Alpha1 entity,
        AuthPolicySpec policy,
        string ns,
        CancellationToken ct)
    {
        if (policy.Policy == AuthPolicy.Anonymous || policy.SecretRefs.Count == 0)
            return string.Empty;

        var credentials = new List<(string, string)>();

        foreach (var secretRef in policy.SecretRefs)
        {
            var secret = await k8s.GetAsync<V1Secret>(secretRef, ns, ct)
                ?? throw new InvalidOperationException(
                    $"Credential Secret '{secretRef}' not found in namespace '{ns}'.");

            var username = GetSecretKey(secret, "username", secretRef);
            var password = GetSecretKey(secret, "password", secretRef);
            credentials.Add((username, password));
        }

        return htpasswd.BuildHtpasswd(credentials);
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

    private static V1PodSpec BuildPodSpec(string name, MavenRepositorySpec spec, bool usePvcCache = false)
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

            var noPrivEsc = new V1SecurityContext
            {
                AllowPrivilegeEscalation = false,
                ReadOnlyRootFilesystem   = true,
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
                SecurityContext = noPrivEsc,
            });

            containers.Add(new V1Container
            {
                Name            = "mtail",
                Image           = spec.Metrics.MtailImage,
                ImagePullPolicy = "IfNotPresent",
                Args            =
                [
                    "--progs=/etc/mtail",
                    "--logs=/var/log/nginx/access.json",
                    $"--port={spec.Metrics.MtailPort}",
                    "--metric_expiry_interval=168h",
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
                SecurityContext = noPrivEsc,
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

