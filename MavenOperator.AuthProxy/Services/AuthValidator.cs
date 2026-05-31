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
    Task<(bool Success, string? Role)> ValidateAsync(
        string? authorizationHeader,
        string? originalUri,
        string? originalMethod,
        CancellationToken ct = default);
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
        string? originalUri,
        string? originalMethod,
        CancellationToken ct = default)
    {
        var isReadDirection = IsReadMethod(originalMethod);
        var direction = GetDirectionConfig(_config.CurrentValue, isReadDirection);

        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return AllowsAnonymous(direction)
                ? (true, null)
                : (false, null);

        if (authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorizationHeader["Bearer ".Length..].Trim();
            var (success, role) = await ValidateBearerAsync(token, direction.CiTrust, ct);
            return EnforceAcl(direction.Acls, success, role, originalUri);
        }

        if (authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = authorizationHeader["Basic ".Length..].Trim();
            var (success, role) = await ValidateBasicAsync(encoded, direction.HtpasswdPath, isReadDirection, direction.CiTrust, ct);
            return EnforceAcl(direction.Acls, success, role, originalUri);
        }

        return (false, null);
    }

    private static AuthDirectionConfig GetDirectionConfig(AuthProxyConfig cfg, bool isReadDirection)
    {
        var direction = isReadDirection ? cfg.Download : cfg.Upload;
        if (string.IsNullOrWhiteSpace(direction.HtpasswdPath))
        {
            direction.HtpasswdPath = isReadDirection
                ? "/etc/maven-auth/download.htpasswd"
                : "/etc/maven-auth/upload.htpasswd";
        }

        return direction;
    }

    private static bool AllowsAnonymous(AuthDirectionConfig direction) =>
        string.Equals(direction.Policy, "Anonymous", StringComparison.OrdinalIgnoreCase)
        && direction.CiTrust.Count == 0
        && direction.Acls.Count == 0;

    private static (bool Success, string? Role) EnforceAcl(
        List<ArtifactAclConfig> acls,
        bool success,
        string? role,
        string? originalUri)
    {
        if (!success || string.IsNullOrWhiteSpace(role))
            return (success, role);

        if (acls.Count == 0)
            return (true, role);

        var artifactPath = ExtractArtifactPath(originalUri);
        if (string.IsNullOrWhiteSpace(artifactPath))
            return (true, role);

        var acl = acls
            .OrderByDescending(a => Specificity(a.Path))
            .FirstOrDefault(a => GlobMatch(a.Path, artifactPath));

        if (acl is null)
            return (true, role);

        var allowed = acl.Roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowed.Count == 0 || !allowed.Contains(role))
            return (false, null);

        return (true, role);
    }

    private static bool IsReadMethod(string? method) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractArtifactPath(string? originalUri)
    {
        if (string.IsNullOrWhiteSpace(originalUri))
            return null;

        var pathOnly = originalUri.Split('?', 2)[0].Trim();
        var marker = "/repository/";
        var markerIdx = pathOnly.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx < 0)
            return null;

        var afterMarker = pathOnly[(markerIdx + marker.Length)..];
        var firstSlash = afterMarker.IndexOf('/');
        if (firstSlash < 0 || firstSlash == afterMarker.Length - 1)
            return null;

        return afterMarker[(firstSlash + 1)..];
    }

    private static int Specificity(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return 0;

        return pattern.Count(c => c != '*');
    }

    private static bool GlobMatch(string pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(value, regex, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    // ── Bearer (JWT) validation ─────────────────────────────────────────────

    private async Task<(bool, string?)> ValidateBearerAsync(
        string rawToken,
        IReadOnlyList<CiTrustBindingConfig> ciTrust,
        CancellationToken ct)
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
        // Find bindings that could match this issuer
        var matchingBindings = ciTrust
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

    private async Task<(bool, string?)> ValidateBasicAsync(
        string encoded,
        string htpasswdPath,
        bool isReadDirection,
        IReadOnlyList<CiTrustBindingConfig> ciTrust,
        CancellationToken ct)
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

        // Gradle (and other OAuth2-aware clients) send OIDC tokens as
        // Basic credentials with the reserved username "oauth2" and the
        // token as the password.  Treat that as a Bearer JWT request so
        // CI pipelines using Gradle or similar tools work transparently.
        if (string.Equals(username, "oauth2", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Basic auth with username 'oauth2' detected — treating password as Bearer token");
            return await ValidateBearerAsync(password, ciTrust, ct);
        }

        if (VerifyHtpasswd(username, password, htpasswdPath))
            return (true, isReadDirection ? "reader" : "deployer");

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

