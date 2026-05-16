using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MavenOperator.Reconcilers;

/// <summary>
/// Full Phase 3 implementation of the Virtual repository reconciler.
///
/// Architecture per Virtual repo (all in the same namespace):
///   nginx Deployment  ──► C# proxy Deployment  ──► member Services
///
/// Steps (all idempotent):
///   1. Validate members list is non-empty
///   2. EnsureDownloadHtpasswdSecret
///   3. EnsureProxyConfigMap          (JSON member list for C# proxy)
///   4. EnsureProxyDeployment         (operator image in proxy mode)
///   5. EnsureProxyService            (ClusterIP for NGINX → proxy)
///   6. EnsureNginxConfigMap          (auth + proxy_pass to C# proxy)
///   7. EnsureNginxDeployment
///   8. EnsureNginxService            (external ClusterIP)
///   9. EnsureIngress (when enabled)
/// </summary>
public sealed class VirtualRepositoryReconciler(
    IKubernetesClient k8s,
    IKubernetesResourceManager resources,
    IHtpasswdService htpasswd,
    IKubernetesEventService events,
    ILogger<VirtualRepositoryReconciler> logger)
    : IVirtualRepositoryReconciler
{
    // The virtual proxy is now a separate binary (MavenOperator.VirtualProxy).
    // Read its image from the VIRTUAL_PROXY_IMAGE env-var; falls back to "maven-virtual-proxy:dev".
    private static string ProxyImage =>
        Environment.GetEnvironmentVariable("VIRTUAL_PROXY_IMAGE") ?? "maven-virtual-proxy:dev";
    private const string NginxImage     = "nginx:1.27-alpine";
    private const string AuthPath       = "/etc/nginx/auth";
    private const string ConfPath       = "/etc/nginx/conf.d";
    private const int    ProxyPort      = 8080;

    public async Task ReconcileAsync(MavenRepositoryV1Alpha1 entity, CancellationToken ct)
    {
        var name = entity.Metadata.Name!;
        var ns   = entity.Metadata.NamespaceProperty!;
        var spec = entity.Spec;

        if (spec.Virtual is null)
            throw new InvalidOperationException(
                $"MavenRepository '{name}' has type Virtual but spec.virtual is not set.");

        if (spec.Virtual.Members.Count == 0)
            throw new InvalidOperationException(
                $"MavenRepository '{name}' (Virtual) must have at least one member.");

        logger.LogInformation("[Virtual] Reconciling {Namespace}/{Name} with {Count} member(s)",
            ns, name, spec.Virtual.Members.Count);
        await events.PublishAsync(entity, "Provisioning",
            $"Reconciling Virtual repository '{name}' with {spec.Virtual.Members.Count} member(s)", ct: ct);

        // 1 ── Resolve member Service URLs ─────────────────────────────────────
        // Each member is a MavenRepository name in the same namespace.
        // Its Service is named "<member>-svc" (created by the member's own reconciler).
        var members = spec.Virtual.Members
            .Select(m => new { Name = m, BaseUrl = $"http://{m}-svc/repository/{m}" })
            .ToList();

        // 2 ── Download htpasswd Secret ────────────────────────────────────────
        var downloadHtpasswd = await BuildHtpasswdAsync(entity, spec.Auth.Download, ns, ct);

        await resources.EnsureSecretAsync(entity, $"{name}-download-htpasswd",
            new Dictionary<string, string> { ["download.htpasswd"] = downloadHtpasswd }, ct);

        entity.Status.SetCondition("AuthReady", isTrue: true,
            reason: "HtpasswdGenerated",
            message: $"{spec.Auth.Download.SecretRefs.Count} download user(s) configured");

        // 3 ── C# proxy ConfigMap (VirtualRepoConfig JSON) ─────────────────────
        var proxyConfig = new
        {
            VirtualRepo = new
            {
                Name    = name,
                Members = members.Select(m => new { m.Name, BaseUrl = m.BaseUrl }).ToArray(),
                MetadataCacheTtlSeconds = spec.Virtual.MetadataCacheTtlSeconds,
            },
        };
        var proxyConfigJson = JsonSerializer.Serialize(proxyConfig, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        await resources.EnsureConfigMapAsync(entity, $"{name}-proxy-cm",
            new Dictionary<string, string> { ["appsettings.json"] = proxyConfigJson }, ct);

        // 4 ── C# proxy Deployment ─────────────────────────────────────────────
        var proxyPodSpec  = BuildProxyPodSpec(name, spec);
        var proxyHash     = ComputeHash(proxyConfigJson + downloadHtpasswd);
        var proxyDeplName = $"{name}-proxy";

        await resources.EnsureDeploymentAsync(entity, proxyDeplName, proxyHash, proxyPodSpec, replicas: 1, ct);

        // 5 ── C# proxy internal Service ───────────────────────────────────────
        await resources.EnsureServiceAsync(entity, $"{name}-proxy-svc", proxyDeplName, ProxyPort, ct);

        // 6 ── NGINX ConfigMap (auth_basic front + proxy_pass to C# proxy) ─────
        var nginxConfig   = RenderNginxVirtualConfig(name, spec.Auth.Download.Policy);
        var nginxCmName   = $"{name}-nginx-cm";

        await resources.EnsureConfigMapAsync(entity, nginxCmName,
            new Dictionary<string, string> { ["default.conf"] = nginxConfig }, ct);

        // 7 ── NGINX Deployment ────────────────────────────────────────────────
        var nginxHash     = ComputeHash(nginxConfig + downloadHtpasswd);
        var nginxPodSpec  = BuildNginxPodSpec(name, spec);
        var nginxDeplName = $"{name}-nginx";

        await resources.EnsureDeploymentAsync(entity, nginxDeplName, nginxHash, nginxPodSpec, replicas: 1, ct);

        // 8 ── External NGINX Service ──────────────────────────────────────────
        await resources.EnsureServiceAsync(entity, $"{name}-svc", nginxDeplName, ct);

        entity.Status.SetCondition("Available", isTrue: true,
            reason: "DeploymentEnsured", message: "Virtual repo NGINX + proxy deployments ensured");

        // 9 ── Ingress (optional) ─────────────────────────────────────────────
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

        logger.LogInformation("[Virtual] {Namespace}/{Name} reconciled successfully", ns, name);
        await events.PublishAsync(entity, "Ready", $"Virtual repository '{name}' is ready at {entity.Status.Url}", ct: ct);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<string> BuildHtpasswdAsync(
        MavenRepositoryV1Alpha1 _,
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

    private static string GetSecretKey(V1Secret secret, string key, string secretName)
    {
        if (secret.Data?.TryGetValue(key, out var bytes) == true)
            return Encoding.UTF8.GetString(bytes);

        throw new InvalidOperationException(
            $"Credential Secret '{secretName}' is missing the required key '{key}'.");
    }

    /// <summary>
    /// Builds the NGINX config that:
    /// - Optionally enforces Basic Auth for downloads
    /// - Rejects PUT/DELETE with 405
    /// - Proxies all GET/HEAD to the C# aggregation proxy
    /// </summary>
    private static string RenderNginxVirtualConfig(string name, AuthPolicy downloadPolicy)
    {
        var authBlock = downloadPolicy == AuthPolicy.Authenticated
            ? $"""
                  auth_basic "Maven Virtual Repository";
                  auth_basic_user_file {AuthPath}/download.htpasswd;
              """
            : "  # anonymous download — no auth required";

        return $$"""
            server {
                listen 80;
                server_name _;

                location = /healthz {
                    access_log off;
                    return 200 "OK\n";
                    add_header Content-Type text/plain;
                }

                # Block everything except GET/HEAD — Virtual repositories are read-only.
                location ~ ^/repository/{{name}}/ {
                    if ($request_method !~ ^(GET|HEAD)$) {
                        return 405;
                    }

            {{authBlock}}

                    # Strip the /repository/<name>/ prefix before forwarding to the C# proxy.
                    # The proxy expects bare artifact paths (e.g. "com/example/foo/1.0/foo-1.0.jar").
                    rewrite ^/repository/{{name}}/(.*)$ /$1 break;

                    proxy_pass         http://{{name}}-proxy-svc:{{ProxyPort}};
                    proxy_http_version 1.1;
                    proxy_set_header   Host $host;
                    proxy_set_header   X-Real-IP $remote_addr;
                    proxy_read_timeout 120s;
                }
            }
            """;
    }

    private static V1PodSpec BuildProxyPodSpec(string name, MavenRepositorySpec spec)
    {
        var res = BuildResources(spec);

        return new V1PodSpec
        {
            Containers =
            [
                new V1Container
                {
                    Name            = "proxy",
                    Image           = ProxyImage,
                    ImagePullPolicy = "IfNotPresent",
                    Ports           = [new V1ContainerPort { ContainerPort = ProxyPort, Name = "http" }],
                    Resources       = res,
                    Env =
                    [
                        new V1EnvVar { Name = "ASPNETCORE_URLS", Value = $"http://+:{ProxyPort}" },
                    ],
                    VolumeMounts =
                    [
                        new V1VolumeMount
                        {
                            Name             = "proxy-config",
                            MountPath        = "/app/config",
                            ReadOnlyProperty = true,
                        },
                    ],
                    LivenessProbe = new V1Probe
                    {
                        HttpGet             = new V1HTTPGetAction { Path = "/health", Port = ProxyPort },
                        InitialDelaySeconds = 5,
                        PeriodSeconds       = 15,
                    },
                    ReadinessProbe = new V1Probe
                    {
                        HttpGet             = new V1HTTPGetAction { Path = "/health", Port = ProxyPort },
                        InitialDelaySeconds = 3,
                        PeriodSeconds       = 10,
                    },
                },
            ],
            Volumes =
            [
                new V1Volume
                {
                    Name      = "proxy-config",
                    ConfigMap = new V1ConfigMapVolumeSource { Name = $"{name}-proxy-cm" },
                },
            ],
        };
    }

    private static V1PodSpec BuildNginxPodSpec(string name, MavenRepositorySpec spec)
    {
        var res = BuildResources(spec);

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
                    Resources       = res,
                    VolumeMounts    =
                    [
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
                        Optional   = true,
                    },
                },
            ],
        };
    }

    private static V1ResourceRequirements BuildResources(MavenRepositorySpec spec) =>
        spec.Resources is not null
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

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}




