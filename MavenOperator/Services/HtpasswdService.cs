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
        => BuildHtpasswd(credentials.Select(c => CredentialEntry.FromPlainText(c.Username, c.Password)));

    /// <inheritdoc/>
    public string BuildHtpasswd(IEnumerable<CredentialEntry> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var lines = new List<string>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in credentials)
        {
            Validate(entry, seen);
            lines.Add(RenderLine(entry));
        }

        return string.Join('\n', lines);
    }

    /// <inheritdoc/>
    public string BuildHtpasswdStable(
        IEnumerable<(string Username, string Password)> credentials,
        string? existingContent)
        => BuildHtpasswdStable(credentials.Select(c => CredentialEntry.FromPlainText(c.Username, c.Password)), existingContent);

    /// <inheritdoc/>
    public string BuildHtpasswdStable(
        IEnumerable<CredentialEntry> credentials,
        string? existingContent)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var previous = ParseExisting(existingContent);
        var lines = new List<string>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in credentials)
        {
            Validate(entry, seen);

            // Reuse the previous hash when it still verifies against the current
            // password — bcrypt salts are random per call, so re-hashing would change
            // the file bytes (and thus the operator's config hash) on every reconcile.
            // Pre-hashed entries pass through verbatim: we control no salt there, and
            // identical input always produces identical output.
            if (!entry.IsPrehashed &&
                previous.TryGetValue(entry.Username, out var previousHash)
                && Verifies(entry.Password!, previousHash))
            {
                lines.Add($"{entry.Username}:{previousHash}");
            }
            else
            {
                lines.Add(RenderLine(entry));
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>Renders one htpasswd line: pre-hashed entries verbatim, plain-text users freshly bcrypt-hashed.</summary>
    private string RenderLine(CredentialEntry entry) =>
        entry.IsPrehashed ? $"{entry.Username}:{entry.BcryptHash}"
                         : HashPassword(entry.Username, entry.Password!);

    private static void Validate(
        CredentialEntry entry,
        HashSet<string> seen)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Username))
            throw new ArgumentException("Username must not be blank.", nameof(entry));

        // Factory methods guarantee exactly one of password/bcryptHash is present;
        // re-check defensively so malformed entries can't slip through.
        var hasPassword = !string.IsNullOrWhiteSpace(entry.Password);
        var hasBcrypt   = !string.IsNullOrWhiteSpace(entry.BcryptHash);
        if (hasPassword == hasBcrypt)
            throw new ArgumentException(
                $"Entry for user '{entry.Username}' must contain exactly one of password or bcryptHash.", nameof(entry));

        if (!seen.Add(entry.Username))
            throw new ArgumentException(
                $"Duplicate username '{entry.Username}' detected. Usernames must be unique within a policy.");
    }

    /// <summary>
    /// Parses existing htpasswd content into a (case-insensitive) username → hash map.
    /// Malformed lines are ignored — they will simply be re-hashed for the current spec.
    /// </summary>
    private static Dictionary<string, string> ParseExisting(string? content)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content))
            return map;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
                continue;

            map.TryAdd(trimmed[..separator], trimmed[(separator + 1)..]);
        }

        return map;
    }

    private static bool Verifies(string password, string hash)
    {
        try
        {
            return BC.Verify(password, hash);
        }
        catch
        {
            // Malformed/legacy/foreign hash format — treat as unverifiable and re-hash.
            return false;
        }
    }
}

