using k8s;
using k8s.Autorest;
using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;

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
    Task<V1PersistentVolumeClaim> EnsurePvcAsync(
        MavenRepositoryV1Alpha1 owner,
        string pvcName,
        string size,
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
}

/// <inheritdoc/>
public sealed class KubernetesResourceManager(
    IKubernetesClient client,
    ILogger<KubernetesResourceManager> logger)
    : IKubernetesResourceManager
{
    private const string ManagedByLabel   = "maven.operator.io/managed-by";
    private const string ConfigHashAnnotation = "maven.operator.io/config-hash";

    // ── PVC ──────────────────────────────────────────────────────────────────

    public async Task<V1PersistentVolumeClaim> EnsurePvcAsync(
        MavenRepositoryV1Alpha1 owner,
        string pvcName,
        string size,
        string? storageClassName,
        bool setOwnerReference,
        CancellationToken ct)
    {
        var ns = owner.Metadata.NamespaceProperty!;

        var existing = await client.GetAsync<V1PersistentVolumeClaim>(pvcName, ns, ct);
        if (existing is not null)
        {
            logger.LogDebug("PVC {Namespace}/{Name} already exists — skipping", ns, pvcName);
            return existing;
        }

        var pvc = new V1PersistentVolumeClaim
        {
            Metadata = BuildMeta(pvcName, ns, owner, setOwnerReference),
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes      = ["ReadWriteOnce"],
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
    {
        var ns = owner.Metadata.NamespaceProperty!;

        var existing = await client.GetAsync<V1Service>(serviceName, ns, ct);
        if (existing is not null)
        {
            logger.LogDebug("Service {Namespace}/{Name} already exists", ns, serviceName);
            return existing;
        }

        var svc = new V1Service
        {
            Metadata = BuildMeta(serviceName, ns, owner, setOwnerReference: true),
            Spec = new V1ServiceSpec
            {
                Type     = "ClusterIP",
                Selector = new Dictionary<string, string> { ["app"] = selectorAppLabel },
                Ports =
                [
                    new V1ServicePort { Name = "http", Port = port, TargetPort = port },
                ],
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
            // Race: already created by another reconcile; fetch and return it.
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
}


