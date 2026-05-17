using Microsoft.IdentityModel.Tokens;

namespace MavenOperator.AuthProxy.Services;

/// <summary>
/// Fetches and caches JWKS (JSON Web Key Sets) per issuer URL.
/// Caches for 1 hour; force-refreshes on unknown key ID (kid) to handle key rotation.
/// </summary>
public interface IJwksCache
{
    /// <summary>
    /// Gets the signing keys for the given issuer.
    /// Fetches from the JWKS endpoint if not cached.
    /// </summary>
    /// <param name="jwksUrl">URL of the JWKS endpoint.</param>
    /// <param name="forceRefresh">When true, bypasses the cache and re-fetches.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SecurityKey>> GetKeysAsync(string jwksUrl, bool forceRefresh = false, CancellationToken ct = default);
}

