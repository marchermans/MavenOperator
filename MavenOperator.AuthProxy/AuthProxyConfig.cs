namespace MavenOperator.AuthProxy;

/// <summary>
/// Configuration for the maven-auth-proxy sidecar.
/// Loaded from /etc/maven-auth/config.json and hot-reloaded via IOptionsMonitor.
/// </summary>
public sealed class AuthProxyConfig
{
    /// <summary>
    /// CI platform OIDC trust bindings. Evaluated in order; first match wins.
    /// </summary>
    public List<CiTrustBindingConfig> CiTrust { get; set; } = [];

    /// <summary>
    /// Path to the download htpasswd file for Basic Auth validation.
    /// </summary>
    public string DownloadHtpasswdPath { get; set; } = "/etc/maven-auth/download.htpasswd";

    /// <summary>
    /// Path to the upload htpasswd file for Basic Auth validation.
    /// </summary>
    public string UploadHtpasswdPath { get; set; } = "/etc/maven-auth/upload.htpasswd";
}

/// <summary>
/// A single CI trust binding in config form (serialisable as JSON from ConfigMap).
/// </summary>
public sealed class CiTrustBindingConfig
{
    /// <summary>Platform: "github-actions" or "gitlab".</summary>
    public string Platform { get; set; } = "";

    /// <summary>Optional override issuer URL. Defaults to the platform's well-known issuer.</summary>
    public string? IssuerUrl { get; set; }

    /// <summary>Optional expected 'aud' claim value. Null means any audience is accepted.</summary>
    public string? Audience { get; set; }

    /// <summary>Role granted when this binding matches: "reader", "deployer", or "admin".</summary>
    public string Role { get; set; } = "deployer";

    /// <summary>All claims must match (AND logic). Must be non-empty.</summary>
    public Dictionary<string, string> Claims { get; set; } = new();

    /// <summary>
    /// Resolves the effective OIDC issuer URL for this binding.
    /// </summary>
    public string ResolveIssuerUrl() =>
        !string.IsNullOrWhiteSpace(IssuerUrl)
            ? IssuerUrl
            : Platform.ToLowerInvariant() switch
            {
                "github-actions" => "https://token.actions.githubusercontent.com",
                "gitlab"         => "https://gitlab.com",
                _                => throw new InvalidOperationException($"Unknown platform: '{Platform}'"),
            };

    /// <summary>
    /// Resolves the JWKS endpoint URL for this binding's issuer.
    /// </summary>
    public string ResolveJwksUrl()
    {
        var issuer = ResolveIssuerUrl();
        return Platform.ToLowerInvariant() switch
        {
            "github-actions" => $"{issuer.TrimEnd('/')}/.well-known/jwks",
            "gitlab"         => $"{issuer.TrimEnd('/')}/oauth/discovery/keys",
            _                => $"{issuer.TrimEnd('/')}/.well-known/jwks.json",
        };
    }
}

