using MavenOperator.Entities.Spec;

namespace MavenOperator.Services;

/// <summary>
/// Normalizes and resolves repository base URL path prefixes.
/// </summary>
public static class RepositoryPathHelper
{
    public static string ResolvePathPrefix(MavenRepositorySpec spec, string repositoryName)
        => ResolvePathPrefix(spec.PathPrefix, repositoryName);

    public static string ResolvePathPrefix(string? configuredPathPrefix, string repositoryName)
    {
        if (string.IsNullOrWhiteSpace(configuredPathPrefix))
        {
            return $"/repository/{repositoryName}";
        }

        var trimmed = configuredPathPrefix.Trim();
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        if (trimmed.Length > 1)
        {
            trimmed = trimmed.TrimEnd('/');
        }

        return trimmed;
    }

    public static string ToLocationPrefix(string pathPrefix)
        => pathPrefix == "/" ? "/" : $"{pathPrefix}/";

    public static string BuildInternalRepositoryUrl(string serviceName, string pathPrefix)
    {
        var normalizedPath = pathPrefix == "/" ? string.Empty : pathPrefix;
        return $"http://{serviceName}{normalizedPath}";
    }
}

