namespace MavenOperator.Entities.Spec;

/// <summary>
/// Authentication configuration for a MavenRepository.
/// Download and upload policies are independent and produce separate htpasswd files.
/// </summary>
public sealed class AuthSpec
{
    /// <summary>
    /// Policy governing artifact downloads (GET/HEAD).
    /// </summary>
    public AuthPolicySpec Download { get; set; } = new() { Policy = AuthPolicy.Anonymous };

    /// <summary>
    /// Policy governing artifact uploads (PUT/DELETE).
    /// Only meaningful for Hosted repositories; Virtual always returns 405.
    /// </summary>
    public AuthPolicySpec Upload { get; set; } = new() { Policy = AuthPolicy.Authenticated };
}

/// <summary>
/// A single auth policy (download or upload) with its list of credential Secrets.
/// </summary>
public sealed class AuthPolicySpec
{
    /// <summary>
    /// Whether this operation requires authentication.
    /// </summary>
    public AuthPolicy Policy { get; set; } = AuthPolicy.Anonymous;

    /// <summary>
    /// Names of Kubernetes Secrets (in the same namespace), each holding exactly one user's
    /// "username" and "password". All referenced Secrets are compiled into a single htpasswd file.
    /// Required when Policy == Authenticated.
    /// </summary>
    public List<string> SecretRefs { get; set; } = [];
}

public enum AuthPolicy
{
    /// <summary>No credentials required.</summary>
    Anonymous,

    /// <summary>HTTP Basic Auth required; credentials come from SecretRefs.</summary>
    Authenticated,
}

