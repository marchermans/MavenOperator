namespace MavenOperator.ImportJob.Services;

/// <summary>
/// Translates Reposilite on-disk paths to Maven standard layout paths,
/// and removes internal metadata directories.
///
/// Reposilite: /&lt;repository&gt;/com/example/my-lib/1.0/my-lib-1.0.jar
/// Maven:      /com/example/my-lib/1.0/my-lib-1.0.jar
/// </summary>
public static class MavenLayoutTranslator
{
    private static readonly string[] InternalDirectories = [".index", ".cache", ".reposilite", ".trash"];

    /// <summary>
    /// Strips the leading repository name segment and normalises separators.
    /// Returns null when the path belongs to an internal Reposilite directory.
    /// </summary>
    public static string? Translate(string reposiliteRelativePath, string repositoryName)
    {
        // Normalise path separators
        var path = reposiliteRelativePath.Replace('\\', '/').Trim('/');

        // Remove leading repository name segment
        var prefix = repositoryName.TrimEnd('/') + "/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            path = path[prefix.Length..];

        // Skip internal Reposilite directories
        var segments = path.Split('/');
        if (segments.Any(seg => InternalDirectories.Contains(seg, StringComparer.OrdinalIgnoreCase)))
            return null;

        return path;
    }

    /// <summary>
    /// Normalises a raw Maven layout path (no stripping, just normalise separators
    /// and drop internal directories).
    /// </summary>
    public static string? NormalizeMavenPath(string rawPath)
    {
        var path = rawPath.Replace('\\', '/').Trim('/');

        var segments = path.Split('/');
        if (segments.Any(seg => InternalDirectories.Contains(seg, StringComparer.OrdinalIgnoreCase)))
            return null;

        return path;
    }
}

