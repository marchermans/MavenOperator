using MavenOperator.ImportJob.Models;
using Microsoft.Extensions.FileSystemGlobbing;

namespace MavenOperator.ImportJob.Services;

/// <summary>
/// Shared filter helpers for group/version glob matching.
/// </summary>
public static class FilterHelper
{
    /// <summary>
    /// Returns true if the artifact's Maven group path matches the include/exclude group filters.
    /// The relative path is expected in Maven standard layout: com/example/lib/1.0/lib-1.0.jar
    /// </summary>
    public static bool MatchesGroupFilters(string relativePath, ImportFilters filters)
    {
        // Extract group from path (first N-2 segments, where last is filename and second-last is version)
        var parts = relativePath.Split('/');
        if (parts.Length < 3)
            return true; // shallow paths (e.g. root-level files) — allow through

        // group = all segments except last two (artifactId/version part) reversed to dot-notation
        // However we match on path segments, not dot-notation — simpler glob
        var groupPath = string.Join("/", parts[..^2]);  // drops filename+version
        var groupPathShort = string.Join("/", parts[..^3]);  // drops filename+version+artifactId
        var groupDotted = groupPathShort.Replace('/', '.');

        // Include filter: if non-empty, artifact must match at least one pattern
        if (filters.IncludeGroups.Count > 0)
        {
            var matches = filters.IncludeGroups.Any(pattern =>
                GlobMatch(groupDotted, pattern));

            if (!matches) return false;
        }

        // Exclude filter: artifact must not match any pattern
        if (filters.ExcludeGroups.Count > 0)
        {
            var excluded = filters.ExcludeGroups.Any(pattern =>
                GlobMatch(groupDotted, pattern));

            if (excluded) return false;
        }

        return true;
    }

    private static bool GlobMatch(string input, string pattern)
    {
        // Convert glob pattern to regex-style matching
        // Simple implementation: * = any segment, ** = any depth
        if (pattern.EndsWith(".*"))
        {
            var prefix = pattern[..^2];
            return input == prefix || input.StartsWith(prefix + ".", StringComparison.Ordinal);
        }

        return input == pattern ||
               string.Equals(input, pattern, StringComparison.OrdinalIgnoreCase);
    }
}

