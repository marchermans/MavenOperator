using k8s.Models;
// V1ResourceRequirements comes from the KubernetesClient package (k8s.Models namespace)

namespace MavenOperator.Entities.Spec;

/// <summary>
/// Full spec for a MavenRepository CRD instance.
/// </summary>
public sealed class MavenRepositorySpec
{
    /// <summary>
    /// The type of Maven repository. Exactly one of Storage, Upstream, or Virtual
    /// must be supplied, matching this type.
    /// </summary>
    public RepositoryType Type { get; set; } = RepositoryType.Hosted;

    /// <summary>
    /// Storage configuration. Required when Type == Hosted, forbidden otherwise.
    /// </summary>
    public StorageSpec? Storage { get; set; }

    /// <summary>
    /// Upstream proxy configuration. Required when Type == Proxy, forbidden otherwise.
    /// </summary>
    public UpstreamSpec? Upstream { get; set; }

    /// <summary>
    /// Virtual group configuration. Required when Type == Virtual, forbidden otherwise.
    /// </summary>
    public VirtualSpec? Virtual { get; set; }

    /// <summary>
    /// Authentication policies for download and upload.
    /// </summary>
    public AuthSpec Auth { get; set; } = new();

    /// <summary>
    /// Optional Ingress exposure configuration.
    /// </summary>
    public IngressSpec Ingress { get; set; } = new();

    /// <summary>
    /// Resource requests and limits passed to the NGINX (and proxy) containers.
    /// </summary>
    public V1ResourceRequirements? Resources { get; set; }

    /// <summary>
    /// Prometheus metrics sidecar configuration. Controls injection of
    /// nginx-prometheus-exporter and mtail sidecars into NGINX pods.
    /// </summary>
    public MetricsSpec Metrics { get; set; } = new();
}

public enum RepositoryType
{
    /// <summary>Stores artifacts on a PersistentVolume via NGINX + WebDAV.</summary>
    Hosted,

    /// <summary>Caches a remote upstream Maven repository via NGINX proxy_pass.</summary>
    Proxy,

    /// <summary>Fans out to multiple member repositories; served by the C# aggregation proxy.</summary>
    Virtual,
}



