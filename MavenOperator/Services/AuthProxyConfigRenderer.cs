using System.Text.Json;
using MavenOperator.Entities.Spec;

namespace MavenOperator.Services;

/// <summary>
/// Renders the JSON config consumed by the maven-auth-proxy sidecar.
/// </summary>
public interface IAuthProxyConfigRenderer
{
    /// <summary>
    /// Builds auth proxy config JSON from MavenRepository auth spec.
    /// </summary>
    string Render(AuthSpec auth);
}

/// <inheritdoc/>
public sealed class AuthProxyConfigRenderer : IAuthProxyConfigRenderer
{
    public string Render(AuthSpec auth)
    {
        ArgumentNullException.ThrowIfNull(auth);

        var config = new
        {
            download = new
            {
                policy = auth.Download.Policy.ToString(),
                ciTrust = auth.Download.CiTrust.Select(ToCiTrustBinding).ToArray(),
                acls = auth.Download.Acls.Select(ToAcl).ToArray(),
                htpasswdPath = "/etc/maven-auth/download.htpasswd",
            },
            upload = new
            {
                policy = auth.Upload.Policy.ToString(),
                ciTrust = auth.Upload.CiTrust.Select(ToCiTrustBinding).ToArray(),
                acls = auth.Upload.Acls.Select(ToAcl).ToArray(),
                htpasswdPath = "/etc/maven-auth/upload.htpasswd",
            },
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
    }

    private static object ToCiTrustBinding(CiTrustBinding b) => new
    {
        platform = ToPlatform(b.Platform),
        issuerUrl = string.IsNullOrWhiteSpace(b.IssuerUrl) ? null : b.IssuerUrl,
        audience = string.IsNullOrWhiteSpace(b.Audience) ? null : b.Audience,
        role = ToRole(b.Role),
        claims = b.Claims,
    };

    private static object ToAcl(ArtifactAcl a) => new
    {
        path = a.Path,
        roles = a.Roles.Select(ToRole).ToArray(),
    };

    private static string ToPlatform(CiPlatform platform) => platform switch
    {
        CiPlatform.GitHubActions => "github-actions",
        CiPlatform.GitLab => "gitlab",
        _ => throw new InvalidOperationException($"Unsupported CI platform '{platform}'."),
    };

    private static string ToRole(UserRole role) => role switch
    {
        UserRole.Reader => "reader",
        UserRole.Deployer => "deployer",
        UserRole.Admin => "admin",
        _ => throw new InvalidOperationException($"Unsupported user role '{role}'."),
    };
}

