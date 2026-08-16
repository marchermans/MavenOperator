namespace MavenOperator.Entities.Spec;

/// <summary>
/// Upstream configuration for Proxy repositories.
/// </summary>
public sealed class UpstreamSpec
{
    /// <summary>
    /// URL of the remote Maven repository to proxy, e.g. "https://repo1.maven.org/maven2".
    /// Required when type == Proxy.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// How long NGINX should cache successful responses. Defaults to "1d".
    /// </summary>
    public string CacheTtl { get; set; } = "1d";

    /// <summary>
    /// Optional credentials for the upstream repository.
    /// </summary>
    public UpstreamAuthSpec? Auth { get; set; }

    /// <summary>
    /// When set, a PVC of this size is used for the NGINX proxy cache instead of an emptyDir.
    /// Example: "5Gi". Omit or leave null to use ephemeral emptyDir (default).
    /// </summary>
    public string? CachePvcSize { get; set; }

    /// <summary>
    /// Upload forwarding configuration for Proxy repositories.
    /// When enabled, PUT/DELETE requests are forwarded to the upstream using fixed credentials.
    /// Defaults to disabled (read-only cache).
    /// </summary>
    public ProxyUploadSpec Upload { get; set; } = new();
}

/// <summary>
/// Credentials for authenticating against an upstream repository.
/// A single credential Secret is sufficient for server-to-server proxy auth.
/// </summary>
public sealed class UpstreamAuthSpec
{
    /// <summary>
    /// Name of a Kubernetes Secret (in the same namespace) containing
    /// "username" and "password" keys for the upstream.
    /// </summary>
    public string SecretRef { get; set; } = string.Empty;
}

/// <summary>
/// How client authentication is handled for uploads to a Proxy repository.
/// </summary>
public enum ProxyUploadMode
{
    /// <summary>Enforce auth.upload policy; client must provide valid credentials.</summary>
    Passthrough,

    /// <summary>Skip client auth entirely; accept uploads from anyone (migration mode).</summary>
    Override,
}

/// <summary>
/// Reference to a Kubernetes object in the same or another namespace.
/// </summary>
public sealed class LocalObjectReference
{
    /// <summary>Name of the referenced object.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional namespace; defaults to the same namespace as the CRD.</summary>
    public string? Namespace { get; set; }
}

/// <summary>
/// Upload forwarding configuration for Proxy repositories.
/// </summary>
public sealed class ProxyUploadSpec
{
    /// <summary>
    /// Whether upload forwarding is enabled on this proxy.
    /// Default: false (read-only cache).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// How client authentication is handled for uploads.
    /// - Passthrough: enforce auth.upload policy (default).
    /// - Override: skip client auth entirely (migration mode).
    /// </summary>
    public ProxyUploadMode Mode { get; set; } = ProxyUploadMode.Passthrough;

    /// <summary>
    /// Credentials used to authenticate with the upstream Maven server for uploads.
    /// Required when Enabled is true.
    /// </summary>
    public LocalObjectReference? UpstreamCredentialsRef { get; set; }

    /// <summary>
    /// If true, allow uploads even if the proxy is externally exposed (LoadBalancer/Ingress).
    /// Default: false — uploads are blocked on externally exposed proxies for security.
    /// </summary>
    public bool ForceAllowOnExternal { get; set; } = false;
}

