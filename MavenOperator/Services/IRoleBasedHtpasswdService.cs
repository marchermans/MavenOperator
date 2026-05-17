using MavenOperator.Entities.Spec;

namespace MavenOperator.Services;

/// <summary>
/// Extends <see cref="IHtpasswdService"/> with role-aware htpasswd file generation.
/// Filters users by role when building download and upload htpasswd content.
/// </summary>
public interface IRoleBasedHtpasswdService
{
    /// <summary>
    /// Builds the download htpasswd content from the unified user list.
    /// All roles (Reader, Deployer, Admin) are included in the download htpasswd.
    /// </summary>
    /// <param name="users">Resolved (username, password, role) tuples for all users.</param>
    /// <returns>htpasswd file content, or empty string if no users qualify.</returns>
    string BuildDownloadHtpasswd(IEnumerable<(string Username, string Password, UserRole Role)> users);

    /// <summary>
    /// Builds the upload htpasswd content from the unified user list.
    /// Only Deployer and Admin roles are included in the upload htpasswd.
    /// </summary>
    /// <param name="users">Resolved (username, password, role) tuples for all users.</param>
    /// <returns>htpasswd file content, or empty string if no users qualify.</returns>
    string BuildUploadHtpasswd(IEnumerable<(string Username, string Password, UserRole Role)> users);
}

