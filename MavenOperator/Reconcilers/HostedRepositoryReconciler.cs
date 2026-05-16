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
    INginxConfigRenderer nginx,
    IKubernetesEventService events,
    ILogger<HostedRepositoryReconciler> logger)
    : IHostedRepositoryReconciler
{
    private const string NginxImage     = "nginx:1.27-alpine";
    private const string RepositoryPath = "/var/maven/repository";
    private const string AuthPath       = "/etc/nginx/auth";
    private const string ConfPath       = "/etc/nginx/conf.d";

    public async Task ReconcileAsync(MavenRepositoryV1Alpha1 entity, CancellationToken ct)
    {
        var name = entity.Metadata.Name!;
        var ns   = entity.Metadata.NamespaceProperty!;
        var spec = entity.Spec;

        logger.LogInformation("[Hosted] Reconciling {Namespace}/{Name}", ns, name);
        await events.PublishAsync(entity, "Provisioning", $"Reconciling Hosted repository '{name}'", ct: ct);

        // 1 ── PVC ─────────────────────────────────────────────────────────────
        var storage  = spec.Storage ?? new StorageSpec();
        var pvcName  = $"{name}-pvc";
        // DeletionPolicy=Retain → no owner reference (PVC survives CRD deletion)
        var setOwner = storage.DeletionPolicy == DeletionPolicy.Delete;

        await resources.EnsurePvcAsync(entity, pvcName, storage.Size,
            storage.StorageClassName, setOwner, ct);

        entity.Status.SetCondition("StorageBound", isTrue: true,
            reason: "PVCEnsured", message: $"PVC {pvcName} ensured");

        // 2 ── htpasswd Secrets ────────────────────────────────────────────────
        var downloadHtpasswd = await BuildHtpasswdAsync(entity, spec.Auth.Download, ns, ct);
        var uploadHtpasswd   = await BuildHtpasswdAsync(entity, spec.Auth.Upload,   ns, ct);

        await resources.EnsureSecretAsync(entity, $"{name}-download-htpasswd",
            new Dictionary<string, string> { ["download.htpasswd"] = downloadHtpasswd }, ct);

        await resources.EnsureSecretAsync(entity, $"{name}-upload-htpasswd",
            new Dictionary<string, string> { ["upload.htpasswd"] = uploadHtpasswd }, ct);

        entity.Status.SetCondition("AuthReady", isTrue: true,
            reason: "HtpasswdGenerated",
            message: $"{spec.Auth.Download.SecretRefs.Count} download user(s), " +
                     $"{spec.Auth.Upload.SecretRefs.Count} upload user(s) configured");
        await events.PublishAsync(entity, "AuthUpdated",
            $"htpasswd rebuilt: {spec.Auth.Download.SecretRefs.Count} download, {spec.Auth.Upload.SecretRefs.Count} upload user(s)",
            ct: ct);

        // 3 ── NGINX ConfigMap ─────────────────────────────────────────────────
        var nginxConfig  = nginx.RenderHosted(name, spec.Auth.Download.Policy, spec.Auth.Upload.Policy);
        var configMapName = $"{name}-nginx-cm";

        await resources.EnsureConfigMapAsync(entity, configMapName,
            new Dictionary<string, string> { ["default.conf"] = nginxConfig }, ct);

        // 4 ── Deployment ──────────────────────────────────────────────────────
        var configHash    = ComputeHash(nginxConfig + downloadHtpasswd + uploadHtpasswd);
        var deployName    = $"{name}-nginx";
        var podSpec       = BuildPodSpec(name, spec);

        await resources.EnsureDeploymentAsync(entity, deployName, configHash, podSpec, replicas: 1, ct);

        // 5 ── Service ─────────────────────────────────────────────────────────
        await resources.EnsureServiceAsync(entity, $"{name}-svc", deployName, ct);

        entity.Status.SetCondition("Available", isTrue: true,
            reason: "DeploymentEnsured", message: "NGINX deployment ensured");

        // 6 ── Ingress (optional) ─────────────────────────────────────────────
        if (spec.Ingress.Enabled)
        {
            await resources.EnsureIngressAsync(entity, $"{name}-ingress", $"{name}-svc", spec.Ingress, name, ct);
            entity.Status.SetCondition("IngressReady", isTrue: true,
                reason: "IngressEnsured", message: $"Ingress for host '{spec.Ingress.Host}' ensured");
            // Set URL from Ingress
            var ingressPath = spec.Ingress.Path ?? $"/repository/{name}";
            var scheme = spec.Ingress.TlsSecretRef is not null ? "https" : "http";
            entity.Status.Url = spec.Ingress.Host is not null
                ? $"{scheme}://{spec.Ingress.Host}{ingressPath}"
                : ingressPath;
        }
        else
        {
            // Use cluster-internal URL
            entity.Status.Url = $"http://{name}-svc/repository/{name}";
        }

        logger.LogInformation("[Hosted] {Namespace}/{Name} reconciled successfully", ns, name);
        await events.PublishAsync(entity, "Ready", $"Hosted repository '{name}' is ready at {entity.Status.Url}", ct: ct);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads each credential Secret from the cluster, extracts username+password,
    /// and returns a combined htpasswd file content.
    /// If the policy is Anonymous (no secretRefs), returns an empty string.
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
            var secret = await k8s.GetAsync<k8s.Models.V1Secret>(secretRef, ns, ct)
                ?? throw new InvalidOperationException(
                    $"Credential Secret '{secretRef}' not found in namespace '{ns}'. " +
                    $"Create it with 'username' and 'password' keys.");

            var username = GetSecretKey(secret, "username", secretRef);
            var password = GetSecretKey(secret, "password", secretRef);
            credentials.Add((username, password));
        }

        return htpasswd.BuildHtpasswd(credentials);
    }

    private static string GetSecretKey(k8s.Models.V1Secret secret, string key, string secretName)
    {
        if (secret.Data?.TryGetValue(key, out var bytes) == true)
            return Encoding.UTF8.GetString(bytes);

        throw new InvalidOperationException(
            $"Credential Secret '{secretName}' is missing the required key '{key}'. " +
            $"Each credential Secret must have 'username' and 'password' keys.");
    }

    private static V1PodSpec BuildPodSpec(string name, MavenRepositorySpec spec)
    {
        var resources = spec.Resources is not null
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
                    Resources       = resources,
                    VolumeMounts    =
                    [
                        new V1VolumeMount
                        {
                            Name      = "repository",
                            MountPath = RepositoryPath,
                        },
                        new V1VolumeMount
                        {
                            Name      = "nginx-conf",
                            MountPath = ConfPath,
                            ReadOnlyProperty = true,
                        },
                        new V1VolumeMount
                        {
                            Name      = "download-auth",
                            MountPath = AuthPath,
                            ReadOnlyProperty = true,
                        },
                        new V1VolumeMount
                        {
                            Name      = "upload-auth",
                            MountPath = "/etc/nginx/upload-auth",
                            ReadOnlyProperty = true,
                        },
                    ],
                    LivenessProbe = new V1Probe
                    {
                        HttpGet = new V1HTTPGetAction { Path = "/healthz", Port = 80 },
                        InitialDelaySeconds = 5,
                        PeriodSeconds       = 15,
                    },
                    ReadinessProbe = new V1Probe
                    {
                        HttpGet = new V1HTTPGetAction { Path = "/healthz", Port = 80 },
                        InitialDelaySeconds = 3,
                        PeriodSeconds       = 10,
                    },
                },
            ],
            Volumes =
            [
                new V1Volume
                {
                    Name = "repository",
                    PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                    {
                        ClaimName = $"{name}-pvc",
                    },
                },
                new V1Volume
                {
                    Name      = "nginx-conf",
                    ConfigMap = new V1ConfigMapVolumeSource { Name = $"{name}-nginx-cm" },
                },
                new V1Volume
                {
                    Name   = "download-auth",
                    Secret = new V1SecretVolumeSource { SecretName = $"{name}-download-htpasswd" },
                },
                new V1Volume
                {
                    Name   = "upload-auth",
                    Secret = new V1SecretVolumeSource { SecretName = $"{name}-upload-htpasswd" },
                },
            ],
        };
    }

    /// <summary>SHA-256 of the combined config content — used as a pod restart trigger.</summary>
    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes)[..16]; // first 16 hex chars is plenty
    }
}



