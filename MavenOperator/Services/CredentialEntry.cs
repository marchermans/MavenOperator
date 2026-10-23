using System.Text.RegularExpressions;

namespace MavenOperator.Services;

/// <summary>
/// A single resolved credential user, loaded from a Kubernetes Secret.
/// Exactly one of <see cref="Password"/> or <see cref="BcryptHash"/> is set:
/// <list type="bullet">
///   <item><see cref="Password"/> — plain-text password; the operator hashes it (bcrypt) before writing the htpasswd line.</item>
///   <item><see cref="BcryptHash"/> — pre-hashed bcrypt hash; written verbatim into the htpasswd file without re-hashing.</item>
/// </list>
/// Instances are created through <see cref="FromPlainText"/> or <see cref="FromBcryptHash"/>, both of which validate their inputs.
/// </summary>
public sealed class CredentialEntry
{
    /// <summary>
    /// Well-formed bcrypt hash: $2[abxy]$&lt;cost 4..31&gt;$53 base64 chars — the format accepted by NGINX's auth_basic module.
    /// </summary>
    // Note: \z (not $) so a trailing newline cannot sneak past the end anchor.
    private static readonly Regex BcryptHashPattern = new(
        @"^\$2[abxy]\$(\d{1,2})\$[./A-Za-z0-9]{53}\z", RegexOptions.Compiled);

    public string Username { get; }

    /// <summary>Plain-text password. Null when the entry carries a pre-hashed bcrypt hash.</summary>
    public string? Password { get; }

    /// <summary>Pre-hashed bcrypt hash (e.g. $2y$10$...). Null when a plain-text password was supplied.</summary>
    public string? BcryptHash { get; }

    /// <summary>True when the entry carries a pre-hashed bcrypt hash instead of a plain-text password.</summary>
    public bool IsPrehashed => BcryptHash is not null;

    private CredentialEntry(string username, string? password, string? bcryptHash)
    {
        Username = username;
        Password = password;
        BcryptHash = bcryptHash;
    }

    /// <summary>Creates an entry from a plain-text password. The operator hashes it with bcrypt before writing the htpasswd line.</summary>
    public static CredentialEntry FromPlainText(string username, string password)
    {
        ValidateUsername(username);
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password must not be blank.", nameof(password));

        return new CredentialEntry(username, password, null);
    }

    /// <summary>Creates an entry from a pre-hashed bcrypt hash. The hash is written verbatim into the htpasswd file (no re-hashing).</summary>
    public static CredentialEntry FromBcryptHash(string username, string bcryptHash)
    {
        ValidateUsername(username);
        if (!IsValidBcryptHash(bcryptHash))
            throw new ArgumentException(
                $"Value is not a valid bcrypt hash. Expected format: '$2[abxy]$$<cost 4-31>$$53 base64 chars' (e.g. '$2y$10$...').",
                nameof(bcryptHash));

        return new CredentialEntry(username, null, bcryptHash);
    }

    /// <summary>Returns true when the value is a well-formed bcrypt hash.</summary>
    public static bool IsValidBcryptHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = BcryptHashPattern.Match(value);
        if (!match.Success)
            return false;

        // bcrypt's valid work-factor range is 4..31.
        return int.Parse(match.Groups[1].Value) is >= 4 and <= 31;
    }

    private static void ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username must not be blank.", nameof(username));
    }
}
