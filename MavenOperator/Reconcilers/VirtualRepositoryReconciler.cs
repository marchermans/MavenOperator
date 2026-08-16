using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;
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
    private sealed record MemberRoute(string Name, string BaseUrl);

    // Matches MavenOperator.VirtualProxy.Services.UploadTargetConfig — used to build proxy config JSON.
    private sealed record UploadTargetConfig(
        string Name,
        string BaseUrl,
        string? AuthHeader);

    // The virtual proxy is now a separate binary (MavenOperator.VirtualProxy).
    // Read its image from the VIRTUAL_PROXY_IMAGE env-var; falls back to the published GHCR image.
    private static string ProxyImage =>
        Environment.GetEnvironmentVariable("VIRTUAL_PROXY_IMAGE") ?? "ghcr.io/marchermans/maven-virtual-proxy:0.3.0-pre.1";
    private const string NginxImage     = "nginx:1.27-alpine";
    private const string AuthPath       = "/etc/nginx/auth";
    private const string ConfPath       = "/etc/nginx/conf.d";
    private const int    ProxyPort      = 8080;

    public async Task ReconcileAsync(MavenRepositoryV1Alpha1 entity, CancellationToken ct)
    {
        var name = entity.Metadata.Name!;
        var ns   = entity.Metadata.NamespaceProperty!;
        var spec = entity.Spec;
        var repositoryPathPrefix = RepositoryPathHelper.ResolvePathPrefix(spec, name);

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
        var members = await ResolveMemberBaseUrlsAsync(spec.Virtual.Members, ns, ct);

        // 2 ── Download htpasswd Secret ────────────────────────────────────────
        var downloadHtpasswd = await BuildHtpasswdAsync(spec.Auth.Download, ns, ct);

        await resources.EnsureSecretAsync(entity, $"{name}-download-htpasswd",
            new Dictionary<string, string> { ["download.htpasswd"] = downloadHtpasswd }, ct);

        entity.Status.SetCondition("AuthReady", isTrue: true,
            reason: "HtpasswdGenerated",
            message: $"{spec.Auth.Download.Users.Count} download user(s) configured");

        // 2b ── Upload htpasswd Secret (when upload fan-out enabled) ───────────
        var uploadSpec = spec.Virtual.Upload;
        bool uploadEnabled = uploadSpec.Targets.Count > 0;
        string? uploadSyncError = null;
        List<UploadTargetConfig> resolvedUploadTargets = [];

        if (uploadEnabled)
        {
            // Build upload htpasswd for client auth enforcement.
            var uploadHtpasswd = await BuildHtpasswdAsync(spec.Auth.Upload, ns, ct);
            await resources.EnsureSecretAsync(entity, $"{name}-upload-htpasswd",
                new Dictionary<string, string> { ["upload.htpasswd"] = uploadHtpasswd }, ct);

            // Resolve upload targets with credentials.
            try
            {
                resolvedUploadTargets = await ResolveUploadTargetsAsync(uploadSpec, members, ns, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                uploadSyncError = $"Failed to resolve upload targets: {ex.Message}";
                uploadEnabled = false;
                entity.Status.SetCondition("UploadConfigurationError", isTrue: true,
                    reason: "TargetResolutionFailed", message: uploadSyncError);
                await events.PublishAsync(entity, "Warning", "UploadTargetsUnresolvable",
                    $"Could not resolve upload targets for '{name}': {ex.Message}", ct: ct);
            }
        }

        // 3 ── C# proxy ConfigMap (VirtualRepoConfig JSON) ─────────────────────
        var uploadConfigObj = uploadEnabled && resolvedUploadTargets.Count > 0
            ? new
            {
                Name = name,
                Targets = resolvedUploadTargets.Select(t => new { t.Name, t.BaseUrl, t.AuthHeader }).ToArray(),
                TimeoutSeconds = 30,
                RetryAttempts = 2,
            }
            : null;

        var proxyConfig = new
        {
            VirtualRepo = new
            {
                Name    = name,
                Members = members.Select(m => new { m.Name, BaseUrl = m.BaseUrl }).ToArray(),
                MetadataCacheTtlSeconds = spec.Virtual.MetadataCacheTtlSeconds,
                Upload = uploadConfigObj,
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
        var uploadPolicy = spec.Auth.Upload.Policy;
        var nginxConfig   = RenderNginxVirtualConfig(name, spec.Auth.Download.Policy, uploadPolicy, repositoryPathPrefix, uploadEnabled);
        var nginxCmName   = $"{name}-nginx-cm";

        await resources.EnsureConfigMapAsync(entity, nginxCmName,
            new Dictionary<string, string> { ["default.conf"] = nginxConfig }, ct);

        // 7 ── NGINX Deployment ────────────────────────────────────────────────
        var uploadHtpasswdForHash = uploadEnabled ? await BuildHtpasswdAsync(spec.Auth.Upload, ns, ct) : string.Empty;
        var nginxHash     = ComputeHash(nginxConfig + downloadHtpasswd + uploadHtpasswdForHash);
        var nginxPodSpec  = BuildNginxPodSpec(name, spec);
        var nginxDeplName = $"{name}-nginx";

        await resources.EnsureDeploymentAsync(entity, nginxDeplName, nginxHash, nginxPodSpec, replicas: 1, ct);

        // 8 ── External NGINX Service ──────────────────────────────────────────
        await resources.EnsureServiceAsync(entity, $"{name}-svc", nginxDeplName, ct);

        entity.Status.SetCondition("Available", isTrue: true,
            reason: "DeploymentEnsured", message: "Virtual repo NGINX + proxy deployments ensured");

        // 8b ── Upload status and target reachability probing ──────────────────
        if (uploadSpec.Targets.Count > 0)
        {
            entity.Status.Upload ??= new UploadStatus();
            var virtualUploadStatus = new VirtualUploadStatus
            {
                Enabled = uploadEnabled,
                Targets = [],
            };

            // Probe each target for reachability.
            foreach (var target in resolvedUploadTargets)
            {
                var reachable = await ProbeTargetReachableAsync(target.BaseUrl, ct);
                virtualUploadStatus.Targets.Add(new VirtualUploadTargetStatus
                {
                    Name = target.Name,
                    Reachable = reachable,
                    LastError = reachable ? null : "Target unreachable (HEAD probe failed)",
                });
            }

            entity.Status.Upload.Virtual = virtualUploadStatus;

            if (uploadEnabled && string.IsNullOrEmpty(uploadSyncError))
            {
                var allReachable = virtualUploadStatus.Targets.All(t => t.Reachable);
                entity.Status.SetCondition("UploadReady", isTrue: true,
                    reason: allReachable ? "AllTargetsReachable" : "SomeTargetsUnreachable",
                    message: $"Upload fan-out enabled with {resolvedUploadTargets.Count} target(s) ({virtualUploadStatus.Targets.Count(t => t.Reachable)} reachable)");

                if (allReachable)
                {
                    await events.PublishAsync(entity, "Normal", "UploadEnabled",
                        $"Upload fan-out enabled for '{name}' to {resolvedUploadTargets.Count} target(s)", ct: ct);
                }
                else
                {
                    await events.PublishAsync(entity, "Warning", "SomeUploadTargetsUnreachable",
                        $"Some upload targets unreachable for '{name}': {string.Join(", ", virtualUploadStatus.Targets.Where(t => !t.Reachable).Select(t => t.Name))}", ct: ct);
                }
            }
        }

        // 9 ── Ingress or Gateway API (optional) ──────────────────────────────
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

        // 10 ── Cleanup resources no longer required by spec ───────────────────
        await CleanupObsoleteResourcesAsync(name, ns, spec, ct);

        logger.LogInformation("[Virtual] {Namespace}/{Name} reconciled successfully", ns, name);
        await events.PublishAsync(entity, "Ready", $"Virtual repository '{name}' is ready at {entity.Status.Url}", ct: ct);
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
    }

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

    private static string GetSecretKey(V1Secret secret, string key, string secretName)
    {
        if (secret.Data?.TryGetValue(key, out var bytes) == true)
            return Encoding.UTF8.GetString(bytes);

        throw new InvalidOperationException(
            $"Credential Secret '{secretName}' is missing the required key '{key}'.");
    }

    /// <summary>
    /// Builds the NGINX config that:
    /// - Optionally enforces Basic Auth for downloads and uploads
    /// - Proxies GET/HEAD to the C# aggregation proxy (read-only behavior)
    /// - When uploadEnabled is true, also proxies PUT/DELETE/MKCOL through with upload auth enforcement
    /// </summary>
    private static string RenderNginxVirtualConfig(
        string name,
        AuthPolicy downloadPolicy,
        AuthPolicy uploadPolicy,
        string repositoryPathPrefix,
        bool uploadEnabled)
    {
        var locationPrefix = RepositoryPathHelper.ToLocationPrefix(repositoryPathPrefix);
        var regexPrefix = locationPrefix == "/"
            ? "/"
            : System.Text.RegularExpressions.Regex.Escape(locationPrefix);

        // Build auth blocks.
        string downloadAuthBlock;
        if (downloadPolicy == AuthPolicy.Authenticated)
        {
            downloadAuthBlock = $"auth_basic \"Maven Virtual Repository\";\n" +
                                $"auth_basic_user_file {AuthPath}/download.htpasswd;";
        }
        else
        {
            downloadAuthBlock = "# anonymous download — no auth required";
        }

        string uploadAuthBlock;
        if (uploadEnabled && uploadPolicy == AuthPolicy.Authenticated)
        {
            uploadAuthBlock = $"auth_basic \"Maven Upload - {name}\";\n" +
                              $"auth_basic_user_file {AuthPath}/upload.htpasswd;";
        }
        else
        {
            uploadAuthBlock = "";
        }

        // Build write handling block.
        string writeHandlingBlock;
        if (uploadEnabled)
        {
            var inner = uploadAuthBlock + "\n" +
                        "client_max_body_size 512m;\n" +
                        "proxy_read_timeout 300s;\n" +
                        "proxy_send_timeout 300s;";
            writeHandlingBlock = "# Upload forwarding (PUT/DELETE/MKCOL) — proxied to C# fan-out service.\n" +
                                 "limit_except GET HEAD OPTIONS {\n" +
                                 Indent(inner, 4) + "\n" +
                                 "}";
        }
        else
        {
            writeHandlingBlock = "# Block everything except GET/HEAD — Virtual repositories are read-only.\n" +
                                 "if ($request_method !~ ^(GET|HEAD)$) {\n" +
                                 "    return 405 \"Virtual repository does not accept uploads.\\n\";\n" +
                                 "}";
        }

        var clientMaxBodySize = uploadEnabled ? "512m" : "1m";

        // Build the full config using double-dollar raw string to escape NGINX braces.
        return $$"""
            server {
                listen 80;
                server_name _;

                location = /healthz {
                    access_log off;
                    return 200 "OK\n";
                    add_header Content-Type text/plain;
                }

                location ~ ^{{regexPrefix}} {
                    {{writeHandlingBlock}}

                    {{downloadAuthBlock}}

                    # Strip the repository path prefix before forwarding to the C# proxy.
                    # The proxy expects bare artifact paths (e.g. "com/example/foo/1.0/foo-1.0.jar").
                    rewrite ^{{regexPrefix}}(.*)$ /$1 break;

                    proxy_pass         http://{{name}}-proxy-svc:{{ProxyPort}};
                    proxy_http_version 1.1;
                    proxy_set_header   Host $host;
                    proxy_set_header   X-Real-IP $remote_addr;
                    proxy_read_timeout 120s;

                    client_max_body_size {{clientMaxBodySize}};
                }
            }
            """;
    }

    private static string Indent(string text, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join("\n" + prefix, text.Split('\n'));
    }

    private async Task<List<MemberRoute>> ResolveMemberBaseUrlsAsync(
        IEnumerable<string> memberNames,
        string ns,
        CancellationToken ct)
    {
        var resolvedMembers = new List<MemberRoute>();

        foreach (var memberName in memberNames)
        {
            var memberRepo = await k8s.GetAsync<MavenRepositoryV1Alpha1>(memberName, ns, ct);
            var memberPathPrefix = memberRepo is null
                ? RepositoryPathHelper.ResolvePathPrefix(configuredPathPrefix: null, memberName)
                : RepositoryPathHelper.ResolvePathPrefix(memberRepo.Spec, memberName);

            resolvedMembers.Add(new MemberRoute(
                memberName,
                RepositoryPathHelper.BuildInternalRepositoryUrl($"{memberName}-svc", memberPathPrefix)));
        }

        return resolvedMembers;
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

    /// <summary>
    /// Resolves upload targets from spec.virtual.upload.targets by matching target names to member repos,
    /// and loads credentials (per-target or shared) into base64-encoded Basic auth headers.
    /// </summary>
    private async Task<List<UploadTargetConfig>> ResolveUploadTargetsAsync(
        VirtualUploadSpec uploadSpec,
        List<MemberRoute> members,
        string ns,
        CancellationToken ct)
    {
        var resolved = new List<UploadTargetConfig>();

        // Build a lookup from member name to base URL.
        var memberLookup = members.ToDictionary(m => m.Name, m => m.BaseUrl);

        foreach (var target in uploadSpec.Targets)
        {
            if (!memberLookup.TryGetValue(target.Name, out var baseUrl))
                throw new InvalidOperationException(
                    $"Upload target '{target.Name}' is not a declared member of this Virtual repository.");

            // Resolve credentials: per-target override first, then shared fallback.
            string? authHeader = null;
            var credsRef = target.CredentialsRef ?? uploadSpec.SharedCredentialsRef;

            if (credsRef is not null && !string.IsNullOrWhiteSpace(credsRef.Name))
            {
                try
                {
                    authHeader = await BuildUpstreamAuthHeaderFromSecretAsync(
                        credsRef.Name,
                        string.IsNullOrWhiteSpace(credsRef.Namespace) ? ns : credsRef.Namespace,
                        ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"Failed to load credentials for upload target '{target.Name}' from Secret '{credsRef.Name}': {ex.Message}", ex);
                }
            }

            resolved.Add(new UploadTargetConfig(target.Name, baseUrl, authHeader));
        }

        return resolved;
    }

    /// <summary>
    /// Probes a target URL with a HEAD request to check reachability.
    /// </summary>
    private async Task<bool> ProbeTargetReachableAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5),
            };

            // HEAD the root of the repository to check if it's responding.
            using var response = await client.GetAsync(baseUrl.TrimEnd('/') + "/", HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Virtual] Target probe failed for {BaseUrl}", baseUrl);
            return false;
        }
    }

    /// <summary>
    /// Loads a credential Secret and returns a base64-encoded Basic auth header.
    /// </summary>
    private async Task<string> BuildUpstreamAuthHeaderFromSecretAsync(
        string secretName,
        string ns,
        CancellationToken ct)
    {
        var secret = await k8s.GetAsync<V1Secret>(secretName, ns, ct)
            ?? throw new InvalidOperationException($"Credential Secret '{secretName}' not found in namespace '{ns}'.");

        var username = GetSecretKey(secret, "username", secretName);
        var password = GetSecretKey(secret, "password", secretName);

        var credentials = $"{username}:{password}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        return $"Basic {encoded}";
    }
}




