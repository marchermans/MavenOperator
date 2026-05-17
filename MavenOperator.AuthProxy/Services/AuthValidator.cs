using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MavenOperator.AuthProxy.Services;

/// <summary>
/// Validates incoming Authorization headers (Basic or Bearer) and returns
/// the resolved role, or null if authentication fails.
/// </summary>
public interface IAuthValidator
{
    /// <summary>
    /// Validates the Authorization header and resolves the user's role.
    /// </summary>
    /// <param name="authorizationHeader">Value of the Authorization HTTP header.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// (success: true, role: "reader"|"deployer"|"admin") on success;
    /// (success: false, role: null) on invalid credentials;
    /// </returns>
    Task<(bool Success, string? Role)> ValidateAsync(string? authorizationHeader, CancellationToken ct = default);
}

/// <summary>
/// Validates both HTTP Basic Auth (htpasswd) and Bearer JWT (CI platform OIDC) requests.
/// </summary>
public sealed class AuthValidator : IAuthValidator
{
    private readonly IOptionsMonitor<AuthProxyConfig> _config;
    private readonly IJwksCache                       _jwksCache;
    private readonly ITrustEvaluator                  _trustEvaluator;
    private readonly ILogger<AuthValidator>           _logger;
    private readonly JwtSecurityTokenHandler          _jwtHandler = new();

    public AuthValidator(
        IOptionsMonitor<AuthProxyConfig> config,
        IJwksCache                       jwksCache,
        ITrustEvaluator                  trustEvaluator,
        ILogger<AuthValidator>           logger)
    {
        _config         = config;
        _jwksCache      = jwksCache;
        _trustEvaluator = trustEvaluator;
        _logger         = logger;
    }

    public async Task<(bool Success, string? Role)> ValidateAsync(
        string? authorizationHeader,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return (false, null);

        if (authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorizationHeader["Bearer ".Length..].Trim();
            return await ValidateBearerAsync(token, ct);
        }

        if (authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = authorizationHeader["Basic ".Length..].Trim();
            return ValidateBasic(encoded);
        }

        return (false, null);
    }

    // ── Bearer (JWT) validation ─────────────────────────────────────────────

    private async Task<(bool, string?)> ValidateBearerAsync(string rawToken, CancellationToken ct)
    {
        // Decode header/claims without verification first to read iss and kid
        JwtSecurityToken unverified;
        try
        {
            unverified = _jwtHandler.ReadJwtToken(rawToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read JWT token");
            return (false, null);
        }

        var issuer = unverified.Issuer;
        var config = _config.CurrentValue;

        // Find bindings that could match this issuer
        var matchingBindings = config.CiTrust
            .Where(b =>
            {
                try { return string.Equals(b.ResolveIssuerUrl(), issuer, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            })
            .ToList();

        if (matchingBindings.Count == 0)
        {
            _logger.LogWarning("No ciTrust bindings for issuer {Issuer}", issuer);
            return (false, null);
        }

        // Find the JWKS URL (all bindings for same issuer share the same JWKS URL)
        string jwksUrl;
        try { jwksUrl = matchingBindings[0].ResolveJwksUrl(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot resolve JWKS URL for issuer {Issuer}", issuer);
            return (false, null);
        }

        // Validate JWT signature
        var kid = unverified.Header.Kid;
        var (valid, jwtToken) = await TryValidateJwtAsync(rawToken, issuer, jwksUrl, kid, forceRefresh: false, ct);

        if (!valid || jwtToken is null)
        {
            // Try once with force-refresh (handles key rotation)
            (valid, jwtToken) = await TryValidateJwtAsync(rawToken, issuer, jwksUrl, kid, forceRefresh: true, ct);
        }

        if (!valid || jwtToken is null)
        {
            _logger.LogWarning("JWT validation failed for issuer {Issuer}", issuer);
            return (false, null);
        }

        // Evaluate trust bindings
        var role = _trustEvaluator.EvaluateRole(jwtToken, matchingBindings.AsReadOnly());
        if (role is null)
        {
            _logger.LogWarning("No ciTrust binding matched JWT claims from {Issuer}", issuer);
            return (false, null);
        }

        return (true, role);
    }

    private async Task<(bool, JwtSecurityToken?)> TryValidateJwtAsync(
        string rawToken, string issuer, string jwksUrl, string? kid,
        bool forceRefresh, CancellationToken ct)
    {
        IReadOnlyList<SecurityKey> keys;
        try
        {
            keys = await _jwksCache.GetKeysAsync(jwksUrl, forceRefresh, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch JWKS from {Url}", jwksUrl);
            return (false, null);
        }

        var validationParams = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = issuer,
            ValidateAudience         = false,   // audience checked in TrustEvaluator
            ValidateLifetime         = true,
            IssuerSigningKeys        = keys,
            ValidateIssuerSigningKey = true,
        };

        try
        {
            _jwtHandler.ValidateToken(rawToken, validationParams, out var secToken);
            return (true, (JwtSecurityToken)secToken);
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogDebug(ex, "JWT signature validation failed (forceRefresh={F})", forceRefresh);
            return (false, null);
        }
    }

    // ── Basic Auth (htpasswd) validation ────────────────────────────────────

    private (bool, string?) ValidateBasic(string encoded)
    {
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            return (false, null);
        }

        var colon = decoded.IndexOf(':');
        if (colon < 0) return (false, null);

        var username = decoded[..colon];
        var password = decoded[(colon + 1)..];

        var config = _config.CurrentValue;

        // Check upload htpasswd first (deployer/admin) then download htpasswd (reader)
        if (VerifyHtpasswd(username, password, config.UploadHtpasswdPath))
            return (true, "deployer");

        if (VerifyHtpasswd(username, password, config.DownloadHtpasswdPath))
            return (true, "reader");

        return (false, null);
    }

    private bool VerifyHtpasswd(string username, string password, string path)
    {
        if (!File.Exists(path)) return false;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#') || trimmed.Length == 0) continue;

                var colon = trimmed.IndexOf(':');
                if (colon < 0) continue;

                var lineUser = trimmed[..colon];
                var lineHash = trimmed[(colon + 1)..];

                if (!string.Equals(lineUser, username, StringComparison.OrdinalIgnoreCase))
                    continue;

                return BCrypt.Net.BCrypt.Verify(password, lineHash);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading htpasswd file {Path}", path);
        }

        return false;
    }
}

