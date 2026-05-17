using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace MavenOperator.AuthProxy.Services;

/// <summary>
/// JWKS cache implementation backed by IMemoryCache with 1-hour TTL.
/// </summary>
public sealed class JwksCache : IJwksCache
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly IMemoryCache    _cache;
    private readonly HttpClient      _http;
    private readonly ILogger<JwksCache> _logger;

    public JwksCache(IMemoryCache cache, HttpClient http, ILogger<JwksCache> logger)
    {
        _cache  = cache;
        _http   = http;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SecurityKey>> GetKeysAsync(
        string jwksUrl,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jwksUrl);

        if (!forceRefresh && _cache.TryGetValue(jwksUrl, out IReadOnlyList<SecurityKey>? cached) && cached is not null)
            return cached;

        _logger.LogInformation("Fetching JWKS from {JwksUrl} (force={Force})", jwksUrl, forceRefresh);

        var json = await _http.GetStringAsync(jwksUrl, ct);
        var keys = ParseJwks(json);

        _cache.Set(jwksUrl, keys, CacheTtl);
        return keys;
    }

    private static IReadOnlyList<SecurityKey> ParseJwks(string json)
    {
        using var doc   = JsonDocument.Parse(json);
        var       root  = doc.RootElement;
        var       keysEl = root.GetProperty("keys");

        var jsonWebKeySet = new JsonWebKeySet();
        foreach (var keyEl in keysEl.EnumerateArray())
        {
            var jwk = new JsonWebKey(keyEl.GetRawText());
            jsonWebKeySet.Keys.Add(jwk);
        }

        return jsonWebKeySet.GetSigningKeys().ToList().AsReadOnly();
    }
}

