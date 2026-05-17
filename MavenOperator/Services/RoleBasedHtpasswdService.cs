using MavenOperator.Entities.Spec;

namespace MavenOperator.Services;

/// <summary>
/// Builds role-filtered htpasswd files for download and upload policies.
/// </summary>
public sealed class RoleBasedHtpasswdService : IRoleBasedHtpasswdService
{
    private readonly IHtpasswdService _htpasswd;

    public RoleBasedHtpasswdService(IHtpasswdService htpasswd)
    {
        _htpasswd = htpasswd;
    }

    /// <inheritdoc/>
    /// All roles (Reader, Deployer, Admin) can download.
    public string BuildDownloadHtpasswd(IEnumerable<(string Username, string Password, UserRole Role)> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        // All roles have download access
        var credentials = users
            .Select(u => (u.Username, u.Password));
        return _htpasswd.BuildHtpasswd(credentials);
    }

    /// <inheritdoc/>
    /// Only Deployer and Admin roles can upload.
    public string BuildUploadHtpasswd(IEnumerable<(string Username, string Password, UserRole Role)> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        var credentials = users
            .Where(u => u.Role == UserRole.Deployer || u.Role == UserRole.Admin)
            .Select(u => (u.Username, u.Password));
        return _htpasswd.BuildHtpasswd(credentials);
    }
}

