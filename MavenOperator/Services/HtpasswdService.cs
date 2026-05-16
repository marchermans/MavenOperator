using BC = BCrypt.Net.BCrypt;

namespace MavenOperator.Services;

/// <summary>
/// Generates bcrypt-hashed htpasswd entries compatible with NGINX's auth_basic module.
/// bcrypt (work factor 10) is preferred over APR1-MD5 for security.
/// NGINX ngx_http_auth_basic_module supports bcrypt since 1.3.
/// </summary>
public sealed class HtpasswdService : IHtpasswdService
{
    private const int WorkFactor = 10;

    /// <inheritdoc/>
    public string HashPassword(string username, string plainTextPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(plainTextPassword);

        var hash = BC.HashPassword(plainTextPassword, WorkFactor);
        return $"{username}:{hash}";
    }

    /// <inheritdoc/>
    public string BuildHtpasswd(IEnumerable<(string Username, string Password)> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var lines = new List<string>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (username, password) in credentials)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username must not be blank.", nameof(credentials));
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException($"Password for user '{username}' must not be blank.", nameof(credentials));
            if (!seen.Add(username))
                throw new ArgumentException(
                    $"Duplicate username '{username}' detected. Usernames must be unique within a policy.",
                    nameof(credentials));

            lines.Add(HashPassword(username, password));
        }

        return string.Join('\n', lines);
    }
}

