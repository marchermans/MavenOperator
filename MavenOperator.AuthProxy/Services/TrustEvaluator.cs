using System.IdentityModel.Tokens.Jwt;

namespace MavenOperator.AuthProxy.Services;

/// <summary>
/// Evaluates CI trust bindings against JWT claims.
/// Glob matching with '*' wildcard, ordered first-match semantics.
/// </summary>
public sealed class TrustEvaluator : ITrustEvaluator
{
    /// <inheritdoc/>
    public string? EvaluateRole(JwtSecurityToken token, IReadOnlyList<CiTrustBindingConfig> bindings)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(bindings);

        var issuer = token.Issuer ?? "";

        foreach (var binding in bindings)
        {
            // Skip bindings with empty claims (safety guard — should be rejected at admission)
            if (binding.Claims.Count == 0)
                continue;

            // Check that the token issuer matches the binding's expected issuer
            var expectedIssuer = binding.ResolveIssuerUrl();
            if (!string.Equals(issuer, expectedIssuer, StringComparison.OrdinalIgnoreCase))
                continue;

            // Check audience if specified
            if (!string.IsNullOrWhiteSpace(binding.Audience))
            {
                var audiences = token.Audiences.ToList();
                if (!audiences.Any(a => string.Equals(a, binding.Audience, StringComparison.Ordinal)))
                    continue;
            }

            // Check all claim matchers (AND logic)
            if (!AllClaimsMatch(token, binding.Claims))
                continue;

            return binding.Role;
        }

        return null;
    }

    private static bool AllClaimsMatch(JwtSecurityToken token, Dictionary<string, string> claimMatchers)
    {
        foreach (var (claimName, pattern) in claimMatchers)
        {
            // Look up all claim values for this claim name
            var claimValues = token.Claims
                .Where(c => string.Equals(c.Type, claimName, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Value)
                .ToList();

            if (claimValues.Count == 0)
                return false;

            // At least one value must match the pattern
            if (!claimValues.Any(v => GlobMatch(pattern, v)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Glob matching: '*' matches zero or more characters (at any position).
    /// Matching is case-sensitive.
    /// </summary>
    public static bool GlobMatch(string pattern, string value)
    {
        // Shortcut: no wildcard
        if (!pattern.Contains('*'))
            return string.Equals(pattern, value, StringComparison.Ordinal);

        // Split on '*' and match parts in order
        var parts = pattern.Split('*');
        var pos   = 0;

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                // Empty part from leading/trailing/consecutive '*' — skip
                if (i == parts.Length - 1)
                    return true; // trailing wildcard always matches remainder
                continue;
            }

            var idx = value.IndexOf(part, pos, StringComparison.Ordinal);
            if (idx < 0)
                return false;

            // First part must match at start (no leading wildcard)
            if (i == 0 && idx != 0)
                return false;

            pos = idx + part.Length;
        }

        // Last part must match up to the end (no trailing wildcard)
        return parts[^1].Length == 0 || pos == value.Length;
    }
}


