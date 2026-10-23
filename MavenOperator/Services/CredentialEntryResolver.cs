using k8s.Models;
using System.Text;

namespace MavenOperator.Services;

/// <summary>
/// Resolves a credential Secret into a <see cref="CredentialEntry"/>.
/// The Secret must contain the 'username' key plus exactly one of:
/// <list type="bullet">
///   <item>'password' — plain-text password (hashed by the operator before writing htpasswd).</item>
///   <item>'bcryptHash' — pre-hashed bcrypt hash (written verbatim into htpasswd, no re-hashing).</item>
/// </list>
/// </summary>
public static class CredentialEntryResolver
{
    public const string UsernameKey = "username";
    public const string PasswordKey = "password";
    public const string BcryptHashKey = "bcryptHash";

    /// <summary>Reads the credential Secret and returns the resolved entry, throwing a descriptive error on any malformed shape.</summary>
    public static CredentialEntry FromSecret(V1Secret secret, string secretName)
    {
        var username = ReadKey(secret, UsernameKey, secretName);

        var hasPassword = secret.Data is not null && secret.Data.ContainsKey(PasswordKey);
        var hasBcrypt   = secret.Data is not null && secret.Data.ContainsKey(BcryptHashKey);

        if (hasPassword && hasBcrypt)
            throw new InvalidOperationException(
                $"Credential Secret '{secretName}' contains both 'password' and 'bcryptHash' keys — provide exactly one.");
        if (!hasPassword && !hasBcrypt)
            throw new InvalidOperationException(
                $"Credential Secret '{secretName}' is missing the credential key — " +
                "provide either 'password' (plain-text, hashed by the operator) or 'bcryptHash' (pre-hashed bcrypt).");

        return hasBcrypt
            ? CredentialEntry.FromBcryptHash(username, ReadKey(secret, BcryptHashKey, secretName))
            : CredentialEntry.FromPlainText(username, ReadKey(secret, PasswordKey, secretName));
    }

    private static string ReadKey(V1Secret secret, string key, string secretName)
    {
        if (secret.Data?.TryGetValue(key, out var bytes) == true)
            return Encoding.UTF8.GetString(bytes);

        throw new InvalidOperationException(
            $"Credential Secret '{secretName}' is missing the required key '{key}'.");
    }
}
