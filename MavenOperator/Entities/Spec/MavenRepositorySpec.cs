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
    /// Base URL path prefix served by this repository, for example <c>/repository/releases</c>
    /// or <c>/</c> to serve directly from the host root.
    /// Defaults to <c>/repository/{MavenRepository.Name}</c> when omitted.
    /// </summary>
    public string? PathPrefix { get; set; }

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
    /// Optional classic Ingress exposure configuration.
    /// Cannot be combined with <see cref="Gateway"/>.
    /// </summary>
    public IngressSpec Ingress { get; set; } = new();

    /// <summary>
    /// Optional Kubernetes Gateway API exposure configuration (creates an <c>HTTPRoute</c>).
    /// Cannot be combined with <see cref="Ingress"/>.
    /// </summary>
    public GatewaySpec Gateway { get; set; } = new();

    /// <summary>
    /// Resource requests and limits passed to the NGINX (and proxy) containers.
    /// </summary>
    public V1ResourceRequirements? Resources { get; set; }

    /// <summary>
    /// Prometheus metrics sidecar configuration. Controls injection of
    /// nginx-prometheus-exporter and mtail sidecars into NGINX pods.
    /// </summary>
    public MetricsSpec Metrics { get; set; } = new();

    /// <summary>
    /// Number of replicas for the repository deployment. Defaults to 1 when omitted.
    /// Set to 0 to pause the repository (deployment scaled down, no pods).
    /// </summary>
    public int? Replicas { get; set; }

    /// <summary>
    /// Topology spread constraint for the repository deployment pods.
    /// When configured, ensures pods are distributed across topologies.
    /// Defaults to topologyKey: kubernetes.io/hostname, maxSkew: 1,
    /// whenUnsatisfiable: ScheduleAnyway when topologyKey is omitted.
    /// </summary>
    public TopologySpreadConstraintConfig? TopologySpreadConstraint { get; set; }
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

/// <summary>
/// Configuration for a Kubernetes TopologySpreadConstraint applied to repository deployments.
/// </summary>
public sealed class TopologySpreadConstraintConfig
{
    /// <summary>
    /// The label key that defines the topology domain (e.g. kubernetes.io/hostname, kubernetes.io/os).
    /// Defaults to kubernetes.io/hostname when omitted.
    /// </summary>
    public string? TopologyKey { get; set; }

    /// <summary>
    /// How many pods may differ from the minimum number of pods in any topology relative to maxSkew.
    /// Defaults to 1 when omitted.
    /// </summary>
    public int MaxSkew { get; set; } = 1;

    /// <summary>
    /// What to do if a pod cannot be placed to satisfy the constraint.
    /// Defaults to ScheduleAnyway when omitted.
    /// </summary>
    public TopologySpreadConstraintPolicy WhenUnsatisfiable { get; set; }

    /// <summary>
    /// Optional label selector to restrict which pods are counted in the skew calculation.
    /// When null, all pods in the deployment are counted.
    /// </summary>
    public V1LabelSelector? LabelSelector { get; set; }
}

/// <summary>
/// Policy for what to do when a TopologySpreadConstraint cannot be satisfied.
/// </summary>
public enum TopologySpreadConstraintPolicy
{
    /// <summary>Always schedule the pod, even if the constraint is not met.</summary>
    ScheduleAnyway,

    /// <summary>Do not schedule the pod if the constraint cannot be met.</summary>
    DoNotSchedule,
}



