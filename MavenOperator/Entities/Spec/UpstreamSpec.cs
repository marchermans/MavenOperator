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

