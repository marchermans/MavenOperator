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
        var nginxConfig  = nginx.RenderHosted(name, spec.Auth.Download.Policy, spec.Auth.Upload.Policy, spec.Metrics);
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
        var configHash    = ComputeHash(nginxConfig + downloadHtpasswd + uploadHtpasswd);
        var deployName    = $"{name}-nginx";
        var podSpec       = BuildPodSpec(name, spec);

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



