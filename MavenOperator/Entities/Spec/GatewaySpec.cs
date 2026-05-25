namespace MavenOperator.Entities.Spec;

/// <summary>
/// Kubernetes Gateway API exposure configuration for a MavenRepository.
/// When enabled the operator creates an <c>HTTPRoute</c> resource pointing at
/// an existing <c>Gateway</c>. Cannot be combined with <see cref="IngressSpec.Enabled"/>.
/// </summary>
public sealed class GatewaySpec
{
    /// <summary>
    /// Whether to create a Gateway API <c>HTTPRoute</c> resource for this repository.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Reference to the <c>Gateway</c> resource that must already exist in the cluster.
    /// The operator does not create the Gateway itself.
    /// </summary>
    public GatewayRefSpec GatewayRef { get; set; } = new();

    /// <summary>
    /// Hostname added to the <c>HTTPRoute</c>'s <c>hostnames</c> list, e.g.
    /// <c>"maven.example.com"</c>. Defaults to a wildcard if absent.
    /// </summary>
    public string? Hostname { get; set; }

    /// <summary>
    /// URL path prefix for the <c>HTTPRoute</c> match rule, e.g.
    /// <c>"/repository/my-releases"</c>.
    /// Defaults to <c>/repository/{MavenRepository.Name}</c> when not specified.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Name of a TLS <c>Secret</c> in the same namespace used for HTTPS termination.
    /// When set, <c>status.url</c> is prefixed with <c>https://</c>;
    /// otherwise <c>http://</c> is used.
    /// </summary>
    public string? TlsSecretRef { get; set; }

    /// <summary>
    /// Extra labels merged into the generated <c>HTTPRoute</c>.
    /// </summary>
    public Dictionary<string, string> RouteLabels { get; set; } = new();

    /// <summary>
    /// Extra annotations merged into the generated <c>HTTPRoute</c>.
    /// </summary>
    public Dictionary<string, string> RouteAnnotations { get; set; } = new();
}

/// <summary>
/// Identifies an existing <c>Gateway</c> resource in the cluster.
/// </summary>
public sealed class GatewayRefSpec
{
    /// <summary>
    /// Name of the <c>Gateway</c> resource. Required when <see cref="GatewaySpec.Enabled"/> is <c>true</c>.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Namespace of the <c>Gateway</c> resource.
    /// Defaults to the same namespace as the <c>MavenRepository</c> if not set.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Optional listener name inside the <c>Gateway</c> to attach to.
    /// When absent the route attaches to all compatible listeners.
    /// </summary>
    public string? SectionName { get; set; }
}

