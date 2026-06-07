using k8s;
using k8s.Autorest;
using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;

namespace MavenOperator.Services;

/// <summary>
/// Creates or patches child Kubernetes resources owned by a MavenRepository CRD.
/// All operations are idempotent — safe to call on every reconcile loop.
/// Uses server-side apply semantics via the KubeOps client where available,
/// falling back to create-or-patch for client-side equivalence.
/// </summary>
public interface IKubernetesResourceManager
{
    /// <summary>Ensures a PVC exists with the requested size and storage class.</summary>
    Task<V1PersistentVolumeClaim> EnsurePvcAsync(MavenRepositoryV1Alpha1 owner,
        string pvcName,
        string size,
        string accessMode,
        string? storageClassName,
        bool setOwnerReference,
        CancellationToken ct);

    /// <summary>Ensures a Secret exists with the given string data.</summary>
    Task<V1Secret> EnsureSecretAsync(
        MavenRepositoryV1Alpha1 owner,
        string secretName,
        IDictionary<string, string> stringData,
        CancellationToken ct);

    /// <summary>Ensures a ConfigMap exists with the given data.</summary>
    Task<V1ConfigMap> EnsureConfigMapAsync(
        MavenRepositoryV1Alpha1 owner,
        string configMapName,
        IDictionary<string, string> data,
        CancellationToken ct);

    /// <summary>
    /// Ensures an NGINX Deployment exists. If the config hash changes (content-addressed),
    /// the pod template annotation is updated to trigger a rolling restart.
    /// </summary>
    Task<V1Deployment> EnsureDeploymentAsync(
        MavenRepositoryV1Alpha1 owner,
        string deploymentName,
        string configHash,
        V1PodSpec podSpec,
        int replicas,
        CancellationToken ct);

    /// <summary>Ensures a ClusterIP Service exists exposing port 80.</summary>
    Task<V1Service> EnsureServiceAsync(
        MavenRepositoryV1Alpha1 owner,
        string serviceName,
        string selectorAppLabel,
        CancellationToken ct);

    /// <summary>Ensures a ClusterIP Service exists exposing the given port.</summary>
    Task<V1Service> EnsureServiceAsync(
        MavenRepositoryV1Alpha1 owner,
        string serviceName,
        string selectorAppLabel,
        int port,
        CancellationToken ct);

    /// <summary>Ensures a ClusterIP Service exists with a custom list of ports.</summary>
    Task<V1Service> EnsureServiceWithPortsAsync(
        MavenRepositoryV1Alpha1 owner,
        string serviceName,
        string selectorAppLabel,
        IList<V1ServicePort> ports,
        CancellationToken ct);

    /// <summary>
    /// Ensures a Kubernetes Ingress exists for the given repository.
    /// Creates or updates the ingress based on the provided IngressSpec.
    /// </summary>
    Task<V1Ingress> EnsureIngressAsync(
        MavenRepositoryV1Alpha1 owner,
        string ingressName,
        string serviceName,
        IngressSpec ingressSpec,
        string repositoryName,
        CancellationToken ct);

    /// <summary>
    /// Ensures a Gateway API HTTPRoute exists for the given repository.
    /// Creates or updates the HTTPRoute based on the provided GatewaySpec.
    /// Returns false when the HTTPRoute CRD is not installed on the target cluster.
    /// </summary>
    Task<bool> EnsureHttpRouteAsync(
        MavenRepositoryV1Alpha1 owner,
        string routeName,
        string serviceName,
        int servicePort,
        GatewaySpec gatewaySpec,
        string repositoryName,
        CancellationToken ct);

    /// <summary>
    /// Ensures a CertManager Certificate exists for the given hostname.
    /// Creates or updates the Certificate based on the provided CertManagerSpec.
    /// Returns false when CertManager CRD is not installed or CertManager is not configured.
    /// </summary>
    Task<bool> EnsureCertificateAsync(
        MavenRepositoryV1Alpha1 owner,
        string certificateName,
        string hostname,
        CertManagerSpec certManager,
        CancellationToken ct);

    /// <summary>
    /// Ensures a Prometheus PodMonitor exists for repository pod scraping.
    /// Returns false when the PodMonitor CRD is not installed on the target cluster.
    /// </summary>
    Task<bool> EnsurePodMonitorAsync(
        MavenRepositoryV1Alpha1 owner,
        string podMonitorName,
        string selectorAppLabel,
        MetricsSpec metrics,
        CancellationToken ct);

    /// <summary>
    /// Deletes a PVC if it exists. Used when DeletionPolicy=Delete on CRD deletion.
    /// Errors are swallowed — deletion is best-effort.
    /// </summary>
    Task DeletePvcIfExistsAsync(string pvcName, string namespaceName, CancellationToken ct);

    /// <summary>
    /// Deletes a standard Kubernetes resource if it exists.
    /// Errors are swallowed — deletion is best-effort (used during spec-change cleanup).
    /// </summary>
    Task DeleteResourceIfExistsAsync<T>(string name, string namespaceName, CancellationToken ct)
        where T : class, IKubernetesObject<V1ObjectMeta>, new();

    /// <summary>
    /// Deletes a namespaced custom resource (e.g. HTTPRoute, Certificate, PodMonitor) if it exists.
    /// Errors are swallowed — deletion is best-effort (used during spec-change cleanup).
    /// </summary>
    Task DeleteCustomResourceIfExistsAsync(
        string group,
        string version,
        string plural,
        string name,
        string namespaceName,
        CancellationToken ct);
}

/// <inheritdoc/>
public sealed class KubernetesResourceManager(
    IKubernetesClient client,
    IKubernetes? kubernetes,
    ILogger<KubernetesResourceManager> logger)
    : IKubernetesResourceManager
{
    public KubernetesResourceManager(
        IKubernetesClient client,
        ILogger<KubernetesResourceManager> logger)
        : this(client, kubernetes: null, logger)
    {
    }

    private const string ManagedByLabel   = "maven.operator.io/managed-by";
    private const string ConfigHashAnnotation = "maven.operator.io/config-hash";
    private const string PodMonitorApiGroup = "monitoring.coreos.com";
    private const string PodMonitorApiVersion = "v1";
    private const string PodMonitorPlural = "podmonitors";
    private const string FieldManager = "maven-operator";

    // ── PVC ──────────────────────────────────────────────────────────────────

    public async Task<V1PersistentVolumeClaim> EnsurePvcAsync(MavenRepositoryV1Alpha1 owner,
        string pvcName,
        string size,
        string accessMode,
        string? storageClassName,
        bool setOwnerReference,
        CancellationToken ct)
    {
        var ns = owner.Metadata.NamespaceProperty!;

        var existing = await client.GetAsync<V1PersistentVolumeClaim>(pvcName, ns, ct);
        if (existing is not null)
        {
            // Validate that size, access mode, and storage class match desired state
            var existingSize = existing.Spec?.Resources?.Requests?.TryGetValue("storage", out var sizeQty) == true
                ? sizeQty.ToString()
                : string.Empty;
            var existingAccessMode = existing.Spec?.AccessModes?.FirstOrDefault() ?? string.Empty;
            var existingStorageClass = existing.Spec?.StorageClassName ?? string.Empty;

            if (existingSize == size &&
                existingAccessMode == accessMode &&
                existingStorageClass == (storageClassName ?? string.Empty))
            {
                logger.LogDebug("PVC {Namespace}/{Name} already exists with matching properties", ns, pvcName);
                return existing;
            }

            logger.LogInformation("PVC {Namespace}/{Name} exists but properties changed — deleting for recreation", ns, pvcName);
            try
            {
                await client.DeleteAsync<V1PersistentVolumeClaim>(pvcName, ns, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete PVC {Namespace}/{Name} for recreation", ns, pvcName);
            }
        }

        var pvc = new V1PersistentVolumeClaim
        {
            Metadata = BuildMeta(pvcName, ns, owner, setOwnerReference),
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes      = [accessMode],
                StorageClassName = storageClassName,
                Resources        = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity>
                    {
                        ["storage"] = new ResourceQuantity(size),
                    },
                },
            },
        };

        try
        {
            var created = await client.CreateAsync(pvc, ct);
            logger.LogInformation("Created PVC {Namespace}/{Name}", ns, pvcName);
            return created;
        }
        catch (HttpOperationException ex) when (IsConflict(ex))
        {
            logger.LogDebug("PVC {Namespace}/{Name} already exists (race) — fetching", ns, pvcName);
            return (await client.GetAsync<V1PersistentVolumeClaim>(pvcName, ns, ct))!;
        }
    }

    // ── Secret ───────────────────────────────────────────────────────────────

    public async Task<V1Secret> EnsureSecretAsync(
        MavenRepositoryV1Alpha1 owner,
        string secretName,
        IDictionary<string, string> stringData,
        CancellationToken ct)
    {
        var ns = owner.Metadata.NamespaceProperty!;
        var desired = BuildSecretData(stringData);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var existing = await client.GetAsync<V1Secret>(secretName, ns, ct);
            if (existing is not null)
            {
                if (DataEquals(existing.Data, desired))
                {
                    logger.LogDebug("Secret {Namespace}/{Name} is up-to-date", ns, secretName);
                    return existing;
                }

                try
                {
                    existing.Data = desired;
                    var updated = await client.UpdateAsync(existing, ct);
                    logger.LogInformation("Updated Secret {Namespace}/{Name}", ns, secretName);
                    return updated;
                }
                catch (HttpOperationException ex) when (IsConflict(ex))
                {
                    logger.LogDebug("Secret {Namespace}/{Name} update conflict — retrying", ns, secretName);
                    continue;
                }
            }

            try
            {
                var secret = new V1Secret
                {
                    Metadata = BuildMeta(secretName, ns, owner, setOwnerReference: true),
                    Type     = "Opaque",
                    Data     = desired,
                };
                var created = await client.CreateAsync(secret, ct);
                logger.LogInformation("Created Secret {Namespace}/{Name}", ns, secretName);
                return created;
            }
            catch (HttpOperationException ex) when (IsConflict(ex))
            {
                logger.LogDebug("Secret {Namespace}/{Name} already exists (race) — retrying", ns, secretName);
            }
        }

        throw new InvalidOperationException($"Failed to ensure Secret {ns}/{secretName} after retries.");
    }

    // ── ConfigMap ─────────────────────────────────────────────────────────────

    public async Task<V1ConfigMap> EnsureConfigMapAsync(
        MavenRepositoryV1Alpha1 owner,
        string configMapName,
        IDictionary<string, string> data,
        CancellationToken ct)
    {
        var ns = owner.Metadata.NamespaceProperty!;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var existing = await client.GetAsync<V1ConfigMap>(configMapName, ns, ct);
            if (existing is not null)
            {
                if (MapsEqual(existing.Data, data))
                {
                    logger.LogDebug("ConfigMap {Namespace}/{Name} is up-to-date", ns, configMapName);
                    return existing;
                }

                try
                {
                    existing.Data = new Dictionary<string, string>(data);
                    var updated = await client.UpdateAsync(existing, ct);
                    logger.LogInformation("Updated ConfigMap {Namespace}/{Name}", ns, configMapName);
                    return updated;
                }
                catch (HttpOperationException ex) when (IsConflict(ex))
                {
                    logger.LogDebug("ConfigMap {Namespace}/{Name} update conflict — retrying", ns, configMapName);
                    continue;
                }
            }

            try
            {
                var cm = new V1ConfigMap
                {
                    Metadata = BuildMeta(configMapName, ns, owner, setOwnerReference: true),
                    Data     = new Dictionary<string, string>(data),
                };
                var created = await client.CreateAsync(cm, ct);
                logger.LogInformation("Created ConfigMap {Namespace}/{Name}", ns, configMapName);
                return created;
            }
            catch (HttpOperationException ex) when (IsConflict(ex))
            {
                logger.LogDebug("ConfigMap {Namespace}/{Name} already exists (race) — retrying", ns, configMapName);
            }
        }

        throw new InvalidOperationException($"Failed to ensure ConfigMap {ns}/{configMapName} after retries.");
    }

    // ── Deployment ───────────────────────────────────────────────────────────

    public async Task<V1Deployment> EnsureDeploymentAsync(
        MavenRepositoryV1Alpha1 owner,
        string deploymentName,
        string configHash,
        V1PodSpec podSpec,
        int replicas,
        CancellationToken ct)
    {
        var ns    = owner.Metadata.NamespaceProperty!;
        var labels = new Dictionary<string, string> { ["app"] = deploymentName };

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var existing = await client.GetAsync<V1Deployment>(deploymentName, ns, ct);
            if (existing is not null)
            {
                var currentHash = existing.Spec?.Template?.Metadata?.Annotations is { } ann
                    ? ann.TryGetValue(ConfigHashAnnotation, out var h) ? h : null
                    : null;

                if (currentHash == configHash)
                {
                    logger.LogDebug("Deployment {Namespace}/{Name} config unchanged", ns, deploymentName);
                    return existing;
                }

                try
                {
                    // Config changed — update annotation to trigger rolling restart.
                    existing.Spec!.Template.Metadata!.Annotations ??= new Dictionary<string, string>();
                    existing.Spec.Template.Metadata.Annotations[ConfigHashAnnotation] = configHash;
                    existing.Spec.Template.Spec = podSpec;

                    var updated = await client.UpdateAsync(existing, ct);
                    logger.LogInformation(
                        "Updated Deployment {Namespace}/{Name} (new config hash: {Hash})",
                        ns, deploymentName, configHash);
                    return updated;
                }
                catch (HttpOperationException ex) when (IsConflict(ex))
                {
                    logger.LogDebug("Deployment {Namespace}/{Name} update conflict — retrying", ns, deploymentName);
                    continue;
                }
            }

            try
            {
                var deployment = new V1Deployment
                {
                    Metadata = BuildMeta(deploymentName, ns, owner, setOwnerReference: true),
                    Spec = new V1DeploymentSpec
                    {
                        Replicas = replicas,
                        Selector = new V1LabelSelector { MatchLabels = labels },
                        Template = new V1PodTemplateSpec
                        {
                            Metadata = new V1ObjectMeta
                            {
                                Labels      = labels,
                                Annotations = new Dictionary<string, string>
                                {
                                    [ConfigHashAnnotation] = configHash,
                                },
                            },
                            Spec = podSpec,
                        },
                    },
                };

                var created = await client.CreateAsync(deployment, ct);
                logger.LogInformation("Created Deployment {Namespace}/{Name}", ns, deploymentName);
                return created;
            }
            catch (HttpOperationException ex) when (IsConflict(ex))
            {
                logger.LogDebug("Deployment {Namespace}/{Name} already exists (race) — retrying", ns, deploymentName);
            }
        }

        throw new InvalidOperationException($"Failed to ensure Deployment {ns}/{deploymentName} after retries.");
    }

    // ── Service ──────────────────────────────────────────────────────────────

    public async Task<V1Service> EnsureServiceAsync(
        MavenRepositoryV1Alpha1 owner,
        string serviceName,
        string selectorAppLabel,
        CancellationToken ct)
        => await EnsureServiceAsync(owner, serviceName, selectorAppLabel, port: 80, ct);

    public async Task<V1Service> EnsureServiceAsync(
        MavenRepositoryV1Alpha1 owner,
        string serviceName,
        string selectorAppLabel,
        int port,
        CancellationToken ct)
        => await EnsureServiceWithPortsAsync(owner, serviceName, selectorAppLabel,
            [new V1ServicePort { Name = "http", Port = port, TargetPort = port }], ct);

    public async Task<V1Service> EnsureServiceWithPortsAsync(
        MavenRepositoryV1Alpha1 owner,
        string serviceName,
        string selectorAppLabel,
        IList<V1ServicePort> ports,
        CancellationToken ct)
    {
        var ns = owner.Metadata.NamespaceProperty!;

        var existing = await client.GetAsync<V1Service>(serviceName, ns, ct);
        if (existing is not null)
        {
            // Validate that port configuration matches desired state
            var existingPorts = existing.Spec?.Ports ?? [];
            var portsMatch = existingPorts.Count == ports.Count &&
                existingPorts.All(ep =>
                    ports.Any(dp =>
                        dp.Port == ep.Port &&
                        dp.TargetPort == ep.TargetPort &&
                        dp.Name == ep.Name));

            if (portsMatch)
            {
                logger.LogDebug("Service {Namespace}/{Name} already exists with matching ports", ns, serviceName);
                return existing;
            }

            logger.LogInformation("Service {Namespace}/{Name} exists but port configuration changed — deleting for recreation", ns, serviceName);
            try
            {
                await client.DeleteAsync<V1Service>(serviceName, ns, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete Service {Namespace}/{Name} for recreation", ns, serviceName);
            }
        }

        var svc = new V1Service
        {
            Metadata = BuildMeta(serviceName, ns, owner, setOwnerReference: true),
            Spec = new V1ServiceSpec
            {
                Type     = "ClusterIP",
                Selector = new Dictionary<string, string> { ["app"] = selectorAppLabel },
                Ports    = ports,
            },
        };

        try
        {
            var created = await client.CreateAsync(svc, ct);
            logger.LogInformation("Created Service {Namespace}/{Name}", ns, serviceName);
            return created;
        }
        catch (HttpOperationException ex) when (IsConflict(ex))
        {
            logger.LogDebug("Service {Namespace}/{Name} already exists (race) — fetching", ns, serviceName);
            return (await client.GetAsync<V1Service>(serviceName, ns, ct))!;
        }
    }

    // ── Conflict-retry helpers ────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the <see cref="HttpOperationException"/> represents a 409 Conflict
    /// or 409 AlreadyExists response from the Kubernetes API.
    /// </summary>
    private static bool IsConflict(HttpOperationException ex)
        => ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict;

    private static bool IsNotFound(HttpOperationException ex)
        => ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound;

    private static bool IsForbidden(HttpOperationException ex)
        => ex.Response?.StatusCode == System.Net.HttpStatusCode.Forbidden;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static V1ObjectMeta BuildMeta(
        string name,
        string ns,
        MavenRepositoryV1Alpha1 owner,
        bool setOwnerReference)
    {
        var meta = new V1ObjectMeta
        {
            Name              = name,
            NamespaceProperty = ns,
            Labels            = new Dictionary<string, string>
            {
                [ManagedByLabel] = owner.Metadata.Name!,
            },
        };

        if (setOwnerReference)
        {
            meta.OwnerReferences =
            [
                owner.MakeOwnerReference(),
            ];
        }

        return meta;
    }

    private static Dictionary<string, byte[]> BuildSecretData(IDictionary<string, string> stringData) =>
        stringData.ToDictionary(
            kv => kv.Key,
            kv => System.Text.Encoding.UTF8.GetBytes(kv.Value));

    private static bool DataEquals(IDictionary<string, byte[]>? a, IDictionary<string, byte[]>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count)     return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var bv) || !kv.Value.SequenceEqual(bv)) return false;
        return true;
    }

    private static bool MapsEqual(IDictionary<string, string>? a, IDictionary<string, string>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count)     return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var bv) || kv.Value != bv) return false;
        return true;
    }

    // ── Ingress ───────────────────────────────────────────────────────────────

    public async Task<V1Ingress> EnsureIngressAsync(
        MavenRepositoryV1Alpha1 owner,
        string ingressName,
        string serviceName,
        IngressSpec ingressSpec,
        string repositoryName,
        CancellationToken ct)
    {
        var ns   = owner.Metadata.NamespaceProperty!;
        var defaultPathPrefix = RepositoryPathHelper.ResolvePathPrefix(owner.Spec, repositoryName);
        var path = ingressSpec.Path ?? defaultPathPrefix;
        var host = ingressSpec.Host ?? string.Empty;

        // Determine the TLS secret name:
        // - Explicit TlsSecretRef takes priority.
        // - When CertManager is configured AutoCreate creates a Certificate resource
        //   whose secretName is "<ingressName>-tls".
        var effectiveTlsSecretRef = ingressSpec.TlsSecretRef
            ?? (ingressSpec.CertManager?.AutoCreate == true ? $"{ingressName}-tls" : null);

        var existing = await client.GetAsync<V1Ingress>(ingressName, ns, ct);
        if (existing is not null)
        {
            // Validate that host, path, and TLS configuration match desired state
            var existingHost = existing.Spec?.Rules?.FirstOrDefault()?.Host ?? string.Empty;
            var existingPath = existing.Spec?.Rules?.FirstOrDefault()?.Http?.Paths?.FirstOrDefault()?.Path ?? string.Empty;
            var existingTlsSecret = existing.Spec?.Tls?.FirstOrDefault()?.SecretName ?? string.Empty;
            var desiredTlsSecret = effectiveTlsSecretRef ?? string.Empty;

            if (existingHost == host && existingPath == path && existingTlsSecret == desiredTlsSecret)
            {
                logger.LogDebug("Ingress {Namespace}/{Name} already exists with matching configuration", ns, ingressName);
                return existing;
            }

            logger.LogInformation("Ingress {Namespace}/{Name} exists but configuration changed — deleting for recreation", ns, ingressName);
            try
            {
                await client.DeleteAsync<V1Ingress>(ingressName, ns, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete Ingress {Namespace}/{Name} for recreation", ns, ingressName);
            }
        }

        var rules = new List<V1IngressRule>
        {
            new V1IngressRule
            {
                Host = string.IsNullOrWhiteSpace(host) ? null : host,
                Http = new V1HTTPIngressRuleValue
                {
                    Paths =
                    [
                        new V1HTTPIngressPath
                        {
                            Path     = path,
                            PathType = "Prefix",
                            Backend  = new V1IngressBackend
                            {
                                Service = new V1IngressServiceBackend
                                {
                                    Name = serviceName,
                                    Port = new V1ServiceBackendPort { Number = 80 },
                                },
                            },
                        },
                    ],
                },
            },
        };

        var tls = effectiveTlsSecretRef is not null
            ? new List<V1IngressTLS>
              {
                  new V1IngressTLS
                  {
                      Hosts      = string.IsNullOrWhiteSpace(host) ? null : [host],
                      SecretName = effectiveTlsSecretRef,
                  },
              }
            : null;

        var meta = BuildMeta(ingressName, ns, owner, setOwnerReference: true);
        if (ingressSpec.Annotations.Count > 0)
        {
            meta.Annotations ??= new Dictionary<string, string>();
            foreach (var kv in ingressSpec.Annotations)
            {
                meta.Annotations[kv.Key] = kv.Value;
            }
        }

        var ingress = new V1Ingress
        {
            Metadata = meta,
            Spec = new V1IngressSpec
            {
                Rules = rules,
                Tls   = tls,
            },
        };

        try
        {
            var created = await client.CreateAsync(ingress, ct);
            logger.LogInformation("Created Ingress {Namespace}/{Name}", ns, ingressName);
            return created;
        }
        catch (HttpOperationException ex) when (IsConflict(ex))
        {
            logger.LogDebug("Ingress {Namespace}/{Name} already exists (race) — fetching", ns, ingressName);
            return (await client.GetAsync<V1Ingress>(ingressName, ns, ct))!;
        }
    }

    // ── PodMonitor ─────────────────────────────────────────────────────────────

    public async Task<bool> EnsurePodMonitorAsync(
        MavenRepositoryV1Alpha1 owner,
        string podMonitorName,
        string selectorAppLabel,
        MetricsSpec metrics,
        CancellationToken ct)
    {
        if (kubernetes is null)
        {
            logger.LogDebug(
                "Raw Kubernetes client unavailable; skipping PodMonitor {Name}",
                podMonitorName);
            return false;
        }

        var ns = owner.Metadata.NamespaceProperty!;
        _ = metrics;

        var podMonitor = new Dictionary<string, object?>
        {
            ["apiVersion"] = $"{PodMonitorApiGroup}/{PodMonitorApiVersion}",
            ["kind"] = "PodMonitor",
            ["metadata"] = new Dictionary<string, object?>
            {
                ["name"] = podMonitorName,
                ["namespace"] = ns,
                ["labels"] = new Dictionary<string, string>
                {
                    [ManagedByLabel] = owner.Metadata.Name!,
                    ["maven.operator.io/repo"] = owner.Metadata.Name!,
                },
                ["ownerReferences"] = new[] { owner.MakeOwnerReference() },
            },
            ["spec"] = new Dictionary<string, object?>
            {
                ["selector"] = new Dictionary<string, object?>
                {
                    ["matchLabels"] = new Dictionary<string, string>
                    {
                        ["app"] = selectorAppLabel,
                    },
                },
                ["podMetricsEndpoints"] = new object[]
                {
                    new Dictionary<string, string>
                    {
                        ["port"] = "nginx-metrics",
                        ["path"] = "/metrics",
                        ["interval"] = "30s",
                        ["scrapeTimeout"] = "10s",
                    },
                    new Dictionary<string, string>
                    {
                        ["port"] = "mtail-metrics",
                        ["path"] = "/metrics",
                        ["interval"] = "30s",
                        ["scrapeTimeout"] = "10s",
                    },
                },
            },
        };

        var patchJson = System.Text.Json.JsonSerializer.Serialize(podMonitor);
        var patch = new V1Patch(patchJson, V1Patch.PatchType.ApplyPatch);

        try
        {
            await kubernetes.CustomObjects.PatchNamespacedCustomObjectAsync(
                patch,
                PodMonitorApiGroup,
                PodMonitorApiVersion,
                ns,
                PodMonitorPlural,
                podMonitorName,
                fieldManager: FieldManager,
                force: true,
                cancellationToken: ct);

            logger.LogInformation("Ensured PodMonitor {Namespace}/{Name}", ns, podMonitorName);
            return true;
        }
        catch (HttpOperationException ex) when (IsNotFound(ex))
        {
            logger.LogDebug(
                "PodMonitor CRD unavailable on cluster; skipping PodMonitor {Namespace}/{Name}",
                ns,
                podMonitorName);
            return false;
        }
        catch (HttpOperationException ex) when (IsForbidden(ex))
        {
            logger.LogWarning(
                "Missing RBAC to manage PodMonitor {Namespace}/{Name}; skipping PodMonitor creation",
                ns,
                podMonitorName);
            return false;
        }
    }

    // ── Gateway API HTTPRoute ──────────────────────────────────────────────────

    public async Task<bool> EnsureHttpRouteAsync(
        MavenRepositoryV1Alpha1 owner,
        string routeName,
        string serviceName,
        int servicePort,
        GatewaySpec gatewaySpec,
        string repositoryName,
        CancellationToken ct)
    {
        if (kubernetes is null)
        {
            logger.LogDebug(
                "Raw Kubernetes client unavailable; skipping HTTPRoute {Name}",
                routeName);
            return false;
        }

        var ns = owner.Metadata.NamespaceProperty!;
        var gatewayApiService = new GatewayApiService();
        var defaultPathPrefix = RepositoryPathHelper.ResolvePathPrefix(owner.Spec, repositoryName);
        var httpRoute = gatewayApiService.BuildHttpRoute(routeName, ns, serviceName, servicePort, gatewaySpec,
            repositoryName, defaultPathPrefix);
        var httpRouteJson = System.Text.Json.JsonSerializer.Serialize(httpRoute);
        var patch = new V1Patch(httpRouteJson, V1Patch.PatchType.ApplyPatch);

        const string httpRouteApiGroup = "gateway.networking.k8s.io";
        const string httpRouteApiVersion = "v1";
        const string httpRoutePlural = "httproutes";

        try
        {
            await kubernetes.CustomObjects.PatchNamespacedCustomObjectAsync(
                patch,
                httpRouteApiGroup,
                httpRouteApiVersion,
                ns,
                httpRoutePlural,
                routeName,
                fieldManager: FieldManager,
                force: true,
                cancellationToken: ct);

            logger.LogInformation("Ensured HTTPRoute {Namespace}/{Name}", ns, routeName);
            return true;
        }
        catch (HttpOperationException ex) when (IsNotFound(ex))
        {
            logger.LogDebug(
                "HTTPRoute CRD unavailable on cluster; skipping HTTPRoute {Namespace}/{Name}",
                ns,
                routeName);
            return false;
        }
        catch (HttpOperationException ex) when (IsForbidden(ex))
        {
            logger.LogWarning(
                ex,
                "Missing RBAC to manage HTTPRoute {Namespace}/{Name}; skipping HTTPRoute creation",
                ns,
                routeName);
            return false;
        }
    }

    // ── CertManager Certificate ────────────────────────────────────────────────

    public async Task<bool> EnsureCertificateAsync(
        MavenRepositoryV1Alpha1 owner,
        string certificateName,
        string hostname,
        CertManagerSpec certManager,
        CancellationToken ct)
    {
        if (kubernetes is null)
        {
            logger.LogDebug(
                "Raw Kubernetes client unavailable; skipping Certificate {Name}",
                certificateName);
            return false;
        }

        if (!certManager.AutoCreate)
        {
            logger.LogDebug("CertManager AutoCreate=false; skipping Certificate {Name}", certificateName);
            return false;
        }

        var ns = owner.Metadata.NamespaceProperty!;
        var gatewayApiService = new GatewayApiService();
        var certificate = gatewayApiService.BuildCertificate(
            certificateName,
            ns,
            hostname,
            certManager.Email,
            certManager);

        if (certificate is null)
        {
            return false;
        }

        var certificateJson = System.Text.Json.JsonSerializer.Serialize(certificate);
        var patch = new V1Patch(certificateJson, V1Patch.PatchType.ApplyPatch);

        const string certManagerApiGroup = "cert-manager.io";
        const string certManagerApiVersion = "v1";
        const string certificatePlural = "certificates";

        try
        {
            await kubernetes.CustomObjects.PatchNamespacedCustomObjectAsync(
                patch,
                certManagerApiGroup,
                certManagerApiVersion,
                ns,
                certificatePlural,
                certificateName,
                fieldManager: FieldManager,
                force: true,
                cancellationToken: ct);

            logger.LogInformation("Ensured Certificate {Namespace}/{Name}", ns, certificateName);
            return true;
        }
        catch (HttpOperationException ex) when (IsNotFound(ex))
        {
            logger.LogDebug(
                "CertManager CRD unavailable on cluster; skipping Certificate {Namespace}/{Name}",
                ns,
                certificateName);
            return false;
        }
        catch (HttpOperationException ex) when (IsForbidden(ex))
        {
            logger.LogWarning(
                ex,
                "Missing RBAC to manage Certificate {Namespace}/{Name}; skipping Certificate creation",
                ns,
                certificateName);
            return false;
        }
    }

    // ── PVC deletion ──────────────────────────────────────────────────────────

    public async Task DeletePvcIfExistsAsync(string pvcName, string namespaceName, CancellationToken ct)
    {
        try
        {
            var existing = await client.GetAsync<V1PersistentVolumeClaim>(pvcName, namespaceName, ct);
            if (existing is null)
            {
                logger.LogDebug("PVC {Namespace}/{Name} does not exist — nothing to delete", namespaceName, pvcName);
                return;
            }

            await client.DeleteAsync<V1PersistentVolumeClaim>(pvcName, namespaceName, ct);
            logger.LogInformation("Deleted PVC {Namespace}/{Name} (DeletionPolicy=Delete)", namespaceName, pvcName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete PVC {Namespace}/{Name}", namespaceName, pvcName);
        }
    }

    // ── Generic resource deletion (spec-change cleanup) ───────────────────────

    public async Task DeleteResourceIfExistsAsync<T>(string name, string namespaceName, CancellationToken ct)
        where T : class, IKubernetesObject<V1ObjectMeta>, new()
    {
        try
        {
            var existing = await client.GetAsync<T>(name, namespaceName, ct);
            if (existing is null)
            {
                logger.LogDebug("{Kind} {Namespace}/{Name} does not exist — nothing to delete",
                    typeof(T).Name, namespaceName, name);
                return;
            }

            await client.DeleteAsync<T>(name, namespaceName, ct);
            logger.LogInformation("Deleted {Kind} {Namespace}/{Name} (no longer required by spec)",
                typeof(T).Name, namespaceName, name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete {Kind} {Namespace}/{Name}",
                typeof(T).Name, namespaceName, name);
        }
    }

    public async Task DeleteCustomResourceIfExistsAsync(
        string group,
        string version,
        string plural,
        string name,
        string namespaceName,
        CancellationToken ct)
    {
        if (kubernetes is null)
        {
            logger.LogDebug(
                "Raw Kubernetes client unavailable; skipping deletion of {Group}/{Plural}/{Name}",
                group, plural, name);
            return;
        }

        try
        {
            // Try to fetch the resource first so we can skip gracefully if it doesn't exist.
            await kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
                group, version, namespaceName, plural, name, ct);

            await kubernetes.CustomObjects.DeleteNamespacedCustomObjectAsync(
                group, version, namespaceName, plural, name, cancellationToken: ct);

            logger.LogInformation(
                "Deleted custom resource {Group}/{Plural}/{Namespace}/{Name} (no longer required by spec)",
                group, plural, namespaceName, name);
        }
        catch (HttpOperationException ex) when (IsNotFound(ex))
        {
            logger.LogDebug(
                "Custom resource {Group}/{Plural}/{Namespace}/{Name} does not exist — nothing to delete",
                group, plural, namespaceName, name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to delete custom resource {Group}/{Plural}/{Namespace}/{Name}",
                group, plural, namespaceName, name);
        }
    }
}
