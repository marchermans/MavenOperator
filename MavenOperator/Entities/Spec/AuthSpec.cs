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

    /// <summary>
    /// Unified user list with per-user roles. Each entry references a Kubernetes Secret
    /// holding "username" + "password". Role determines which htpasswd files the user appears in.
    /// Backward-compatible: <see cref="AuthPolicySpec.SecretRefs"/> in Download/Upload are still supported.
    /// </summary>
    public List<UserRef> Users { get; set; } = [];

    /// <summary>
    /// CI platform OIDC trust bindings. When non-empty, the auth proxy sidecar validates
    /// Bearer JWTs from GitHub Actions or GitLab CI and maps them to roles.
    /// </summary>
    public List<CiTrustBinding> CiTrust { get; set; } = [];

    /// <summary>
    /// Per-artifact-path ACL rules. Applied after role assignment.
    /// Ordered by longest-prefix-first when rendering NGINX location blocks.
    /// </summary>
    public List<ArtifactAcl> Acls { get; set; } = [];
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
    /// Legacy path — prefer <see cref="AuthSpec.Users"/> with explicit roles.
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

/// <summary>
/// A user credential reference with an explicit role assignment.
/// </summary>
public sealed class UserRef
{
    /// <summary>
    /// Name of the Kubernetes Secret (in the same namespace) holding "username" + "password".
    /// </summary>
    public string SecretRef { get; set; } = "";

    /// <summary>
    /// Role granted to this user. Controls which htpasswd files the user appears in.
    /// Default: Deployer (download + upload access).
    /// </summary>
    public UserRole Role { get; set; } = UserRole.Deployer;
}

/// <summary>
/// Role controlling access level.
/// </summary>
public enum UserRole
{
    /// <summary>Download only.</summary>
    Reader,

    /// <summary>Download and upload.</summary>
    Deployer,

    /// <summary>Download, upload, and admin API access.</summary>
    Admin,
}

/// <summary>
/// A CI platform OIDC trust binding: maps a platform-issued JWT (matching all claims) to a role.
/// Bindings are evaluated in order; the first match wins.
/// </summary>
public sealed class CiTrustBinding
{
    /// <summary>
    /// The CI platform that issues the JWT.
    /// </summary>
    public CiPlatform Platform { get; set; } = CiPlatform.GitHubActions;

    /// <summary>
    /// Optional override for the OIDC issuer URL.
    /// When null/empty, the platform default is used (e.g. https://token.actions.githubusercontent.com for GitHub).
    /// Must use HTTPS when specified.
    /// </summary>
    public string? IssuerUrl { get; set; }

    /// <summary>
    /// Optional expected 'aud' claim. If set, JWT must include this audience.
    /// If null/empty, any audience is accepted.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Role granted when this binding matches.
    /// </summary>
    public UserRole Role { get; set; } = UserRole.Deployer;

    /// <summary>
    /// Claim matchers (all must match — AND logic).
    /// Values support glob with '*' wildcard. Must be non-empty.
    /// </summary>
    public Dictionary<string, string> Claims { get; set; } = new();
}

/// <summary>
/// Supported CI platforms for OIDC token trust.
/// </summary>
public enum CiPlatform
{
    /// <summary>GitHub Actions (https://token.actions.githubusercontent.com)</summary>
    GitHubActions,

    /// <summary>GitLab CI (https://gitlab.com or custom issuerUrl)</summary>
    GitLab,
}

/// <summary>
/// Per-artifact-path ACL entry.
/// </summary>
public sealed class ArtifactAcl
{
    /// <summary>
    /// Path glob pattern (e.g. "com/example/**"). Matched against the Maven artifact path.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Roles allowed to download artifacts matching this path.
    /// </summary>
    public List<UserRole> Roles { get; set; } = [];

    /// <summary>
    /// Roles allowed to upload artifacts matching this path.
    /// </summary>
    public List<UserRole> UploadRoles { get; set; } = [];
}
