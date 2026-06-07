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
    /// Defaults to <c>spec.pathPrefix</c> when set, otherwise <c>/repository/{MavenRepository.Name}</c>.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Name of a TLS <c>Secret</c> in the same namespace used for HTTPS termination.
    /// When set, <c>status.url</c> is prefixed with <c>https://</c>;
    /// otherwise <c>http://</c> is used.
    /// Cannot be combined with <see cref="CertManager"/>.
    /// </summary>
    public string? TlsSecretRef { get; set; }

    /// <summary>
    /// CertManager <c>Certificate</c> issuer configuration for automatic TLS certificate provisioning.
    /// When enabled, the operator creates a <c>Certificate</c> resource and the secret
    /// name is automatically managed by CertManager. Cannot be combined with <see cref="TlsSecretRef"/>.
    /// </summary>
    public CertManagerSpec? CertManager { get; set; }

    /// <summary>
    /// Extra labels merged into the generated <c>HTTPRoute</c>.
    /// </summary>
    public Dictionary<string, string> RouteLabels { get; set; } = new();

    /// <summary>
    /// Extra annotations merged into the generated <c>HTTPRoute</c>.
    /// </summary>
    public Dictionary<string, string> RouteAnnotations { get; set; } = new();

    /// <summary>
    /// Optional Gateway API <c>ExtensionRef</c> filters attached to every HTTPRoute rule.
    /// This can be used to require middleware integration (for example Traefik WAF middleware).
    /// </summary>
    public List<GatewayExtensionRefSpec> ExtensionRefs { get; set; } = new();
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

/// <summary>
/// CertManager configuration for automatic TLS certificate provisioning.
/// </summary>
public sealed class CertManagerSpec
{
    /// <summary>
    /// Name of an existing CertManager <c>Issuer</c> or <c>ClusterIssuer</c> resource.
    /// Required when CertManager support is enabled.
    /// </summary>
    public string IssuerName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the referenced issuer is a <c>ClusterIssuer</c> (true) or <c>Issuer</c> (false).
    /// Defaults to false (Issuer in the same namespace).
    /// </summary>
    public bool IsClusterIssuer { get; set; } = false;

    /// <summary>
    /// Optional email address for the certificate. Some issuers require this (e.g. Let's Encrypt).
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Duration after which the certificate should be renewed.
    /// Defaults to "2160h" (90 days). Set to a shorter duration for testing.
    /// </summary>
    public string RenewBefore { get; set; } = "2160h";

    /// <summary>
    /// Whether to automatically create the Certificate resource if it doesn't exist.
    /// Defaults to true.
    /// </summary>
    public bool AutoCreate { get; set; } = true;
}

/// <summary>
/// Gateway API HTTPRoute filter extension reference.
/// </summary>
public sealed class GatewayExtensionRefSpec
{
    /// <summary>
    /// API group of the extension resource, for example <c>traefik.io</c>.
    /// </summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// Kind of the extension resource, for example <c>Middleware</c>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Name of the extension resource in the same namespace as the HTTPRoute.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

