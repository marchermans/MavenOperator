namespace MavenOperator.Services;

/// <summary>
/// Generates htpasswd-formatted credential entries from plain-text username/password pairs.
/// The operator owns all htpasswd files — users only supply username + password in Secrets.
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
}

