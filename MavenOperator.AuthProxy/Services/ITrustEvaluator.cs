using System.IdentityModel.Tokens.Jwt;

namespace MavenOperator.AuthProxy.Services;

/// <summary>
/// Evaluates CI platform trust bindings against a validated JWT.
/// Returns the role string ("reader", "deployer", "admin") on first match, or null if no binding matches.
/// </summary>
public interface ITrustEvaluator
{
    /// <summary>
    /// Evaluates the ordered list of ciTrust bindings against the provided JWT.
    /// Returns the role of the first matching binding, or null if no binding matches.
    /// </summary>
    string? EvaluateRole(JwtSecurityToken token, IReadOnlyList<CiTrustBindingConfig> bindings);
}

