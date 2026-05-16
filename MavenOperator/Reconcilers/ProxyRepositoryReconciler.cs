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

        // 1 ── Download htpasswd Secret ────────────────────────────────────────
        var downloadHtpasswd = await BuildHtpasswdAsync(entity, spec.Auth.Download, ns, ct);

        await resources.EnsureSecretAsync(entity, $"{name}-download-htpasswd",
            new Dictionary<string, string> { ["download.htpasswd"] = downloadHtpasswd }, ct);

        entity.Status.SetCondition("AuthReady", isTrue: true,
            reason: "HtpasswdGenerated",
            message: $"{spec.Auth.Download.SecretRefs.Count} download user(s) configured");

        // 2 ── Upstream auth header (if upstream credentials are configured) ───
        var upstreamAuthHeader = await BuildUpstreamAuthHeaderAsync(entity, spec.Upstream, ns, ct);

        // 3 ── NGINX ConfigMap ─────────────────────────────────────────────────
        var nginxConfig   = nginx.RenderProxy(
            name,
            spec.Auth.Download.Policy,
            spec.Upstream.Url,
            spec.Upstream.CacheTtl,
            upstreamAuthHeader);

        var configMapName = $"{name}-nginx-cm";
        await resources.EnsureConfigMapAsync(entity, configMapName,
            new Dictionary<string, string> { ["default.conf"] = nginxConfig }, ct);

        // 4 ── Deployment ──────────────────────────────────────────────────────
        var configHash = ComputeHash(nginxConfig + downloadHtpasswd);
        var deployName = $"{name}-nginx";
        var podSpec    = BuildPodSpec(name, spec);

        await resources.EnsureDeploymentAsync(entity, deployName, configHash, podSpec, replicas: 1, ct);

        // 5 ── Service ─────────────────────────────────────────────────────────
        await resources.EnsureServiceAsync(entity, $"{name}-svc", deployName, ct);

        entity.Status.SetCondition("Available", isTrue: true,
            reason: "DeploymentEnsured", message: "NGINX proxy deployment ensured");

        // 6 ── Ingress (optional) ─────────────────────────────────────────────
        if (spec.Ingress.Enabled)
        {
            logger.LogInformation(
                "[Proxy] Ingress requested for {Name} but not yet implemented (Phase 4)", name);
        }

        logger.LogInformation("[Proxy] {Namespace}/{Name} reconciled successfully", ns, name);
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

    private static V1PodSpec BuildPodSpec(string name, MavenRepositorySpec spec)
    {
        var resourceRequirements = spec.Resources is not null
            ? new V1ResourceRequirements
            {
                Requests = spec.Resources.Requests,
                Limits   = spec.Resources.Limits,
            }
            : new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"]    = new("100m"),
                    ["memory"] = new("128Mi"),
                },
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"]    = new("500m"),
                    ["memory"] = new("512Mi"),
                },
            };

        return new V1PodSpec
        {
            Containers =
            [
                new V1Container
                {
                    Name            = "nginx",
                    Image           = NginxImage,
                    ImagePullPolicy = "IfNotPresent",
                    Ports           = [new V1ContainerPort { ContainerPort = 80, Name = "http" }],
                    Resources       = resourceRequirements,
                    VolumeMounts    =
                    [
                        new V1VolumeMount
                        {
                            Name      = "nginx-cache",
                            MountPath = CachePath,
                        },
                        new V1VolumeMount
                        {
                            Name             = "nginx-conf",
                            MountPath        = ConfPath,
                            ReadOnlyProperty = true,
                        },
                        new V1VolumeMount
                        {
                            Name             = "download-auth",
                            MountPath        = AuthPath,
                            ReadOnlyProperty = true,
                        },
                    ],
                    LivenessProbe = new V1Probe
                    {
                        HttpGet             = new V1HTTPGetAction { Path = "/healthz", Port = 80 },
                        InitialDelaySeconds = 5,
                        PeriodSeconds       = 15,
                    },
                    ReadinessProbe = new V1Probe
                    {
                        HttpGet             = new V1HTTPGetAction { Path = "/healthz", Port = 80 },
                        InitialDelaySeconds = 3,
                        PeriodSeconds       = 10,
                    },
                },
            ],
            Volumes =
            [
                // emptyDir for proxy cache — ephemeral, wiped on pod restart.
                // A PVC-backed cache is a Phase 4 hardening option.
                new V1Volume
                {
                    Name     = "nginx-cache",
                    EmptyDir = new V1EmptyDirVolumeSource(),
                },
                new V1Volume
                {
                    Name      = "nginx-conf",
                    ConfigMap = new V1ConfigMapVolumeSource { Name = $"{name}-nginx-cm" },
                },
                new V1Volume
                {
                    Name   = "download-auth",
                    Secret = new V1SecretVolumeSource
                    {
                        SecretName = $"{name}-download-htpasswd",
                        // Provide a default empty htpasswd when policy is Anonymous
                        // so the volume mount never fails due to a missing secret key.
                        Optional = true,
                    },
                },
            ],
        };
    }

    /// <summary>SHA-256 of the combined config content — used as a pod restart trigger.</summary>
    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}

