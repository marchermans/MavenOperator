namespace MavenOperator.Services;

/// <summary>
/// Generates htpasswd-formatted credential entries.
/// The operator owns all htpasswd files — users supply username plus either a plain-text
/// password (hashed by the operator) or a pre-hashed bcrypt hash (written verbatim) in Secrets.
/// </summary>
public interface IHtpasswdService
{
    /// <summary>
    /// Hashes a single password and returns one htpasswd line:
    ///   <c>username:$2y$10$...</c>
    /// Uses bcrypt (work factor 10) which is accepted by NGINX's auth_basic module.
    /// </summary>
    string HashPassword(string username, string plainTextPassword);

    /// <summary>
    /// Builds a complete htpasswd file content from a collection of (username, password) pairs.
    /// Each pair becomes one line. Duplicate usernames are rejected by throwing
    /// <see cref="ArgumentException"/>.
    /// </summary>
    /// <param name="credentials">Pairs of (username, plainTextPassword).</param>
    /// <returns>Multi-line string suitable for writing into an htpasswd file.</returns>
    string BuildHtpasswd(IEnumerable<(string Username, string Password)> credentials);

    /// <summary>
    /// Builds a complete htpasswd file content from resolved credential entries.
    /// Plain-text-password users are bcrypt-hashed; pre-hashed entries are written verbatim
    /// into the file (no re-hashing). Duplicate usernames are rejected by throwing
    /// <see cref="ArgumentException"/>.
    /// </summary>
    /// <param name="credentials">Resolved credential entries.</param>
    /// <returns>Multi-line string suitable for writing into an htpasswd file.</returns>
    string BuildHtpasswd(IEnumerable<CredentialEntry> credentials);

    /// <summary>
    /// Builds htpasswd content while reusing the existing bcrypt hashes for every user
    /// whose password is unchanged. BCrypt salts are random per hash call, so rebuilding
    /// from scratch changes the file bytes on every invocation — which churns the
    /// operator's config hash and triggers spurious Deployment rollouts. With this method
    /// the output is byte-stable across reconciles while credentials are unchanged;
    /// only new users or changed passwords get freshly hashed.
    /// </summary>
    /// <param name="credentials">Pairs of (username, plainTextPassword).</param>
    /// <param name="existingContent">
    /// Current file content (e.g. read from the htpasswd Secret), or null when none exists yet.
    /// </param>
    string BuildHtpasswdStable(
        IEnumerable<(string Username, string Password)> credentials,
        string? existingContent);

    /// <summary>
    /// Entry-based variant of <see cref="BuildHtpasswdStable"/> with the same byte-stability guarantee.
    /// Plain-text-password users reuse their previous hash when it still verifies; pre-hashed
    /// entries are written verbatim (they are inherently byte-stable).
    /// </summary>
    /// <param name="credentials">Resolved credential entries.</param>
    /// <param name="existingContent">
    /// Current file content (e.g. read from the htpasswd Secret), or null when none exists yet.
    /// </param>
    string BuildHtpasswdStable(
        IEnumerable<CredentialEntry> credentials,
        string? existingContent);
}

