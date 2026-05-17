using MavenOperator.ImportJob.Models;
using MavenOperator.ImportJob.Services;
using System.Runtime.CompilerServices;

namespace MavenOperator.ImportJob.Sources;

/// <summary>
/// Crawls a mounted PVC filesystem (Modes B and C).
/// Supports both Reposilite on-disk layout and raw Maven standard layout.
///
/// Reposilite on-disk: /&lt;repository&gt;/com/example/my-lib/1.0/my-lib-1.0.jar
/// Maven standard:     /com/example/my-lib/1.0/my-lib-1.0.jar
/// </summary>
public sealed class PvcSnapshotSource : IRepositorySource
{
    private readonly string _mountPath;
    private readonly string? _repositoryName;  // used for Reposilite layout stripping
    private readonly bool _reposiliteLayout;
    private readonly ILogger<PvcSnapshotSource> _logger;

    private static readonly string[] SkipFileNames = ["maven-metadata.xml"];
    private static readonly string[] SkipDirectories = [".index", ".cache", ".git"];

    public PvcSnapshotSource(
        string mountPath,
        bool reposiliteLayout,
        string? repositoryName,
        ILogger<PvcSnapshotSource> logger)
    {
        _mountPath       = mountPath;
        _reposiliteLayout = reposiliteLayout;
        _repositoryName  = repositoryName;
        _logger          = logger;
    }

    public async IAsyncEnumerable<ArtifactDescriptor> CrawlAsync(
        ImportFilters filters,
        [EnumeratorCancellation] CancellationToken ct)
    {
        DateTimeOffset? since = filters.SinceTimestamp is { Length: > 0 } ts
            ? DateTimeOffset.Parse(ts)
            : null;

        if (!Directory.Exists(_mountPath))
        {
            _logger.LogError("Source mount path does not exist: {MountPath}", _mountPath);
            yield break;
        }

        var files = Directory.EnumerateFiles(_mountPath, "*", new EnumerationOptions
        {
            RecurseSubdirectories  = true,
            IgnoreInaccessible     = true,
            AttributesToSkip       = FileAttributes.Hidden | FileAttributes.System,
        });

        foreach (var filePath in files)
        {
            if (ct.IsCancellationRequested) yield break;

            // Skip dot-directories
            var relativeFull = Path.GetRelativePath(_mountPath, filePath);
            if (relativeFull.Split(Path.DirectorySeparatorChar)
                    .Any(seg => SkipDirectories.Any(d => seg == d)))
                continue;

            var fileName = Path.GetFileName(filePath);

            // Skip maven-metadata.xml — operator regenerates it
            if (SkipFileNames.Any(s => fileName.Equals(s, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Build Maven-standard relative path
            var relativePath = BuildRelativePath(relativeFull);

            // sinceTimestamp filter
            if (since.HasValue)
            {
                var mtime = File.GetLastWriteTimeUtc(filePath);
                if (new DateTimeOffset(mtime) < since)
                    continue;
            }

            // Group filter
            if (!FilterHelper.MatchesGroupFilters(relativePath, filters))
                continue;

            _logger.LogDebug("Found PVC artifact: {RelativePath}", relativePath);

            var info = new FileInfo(filePath);

            yield return new ArtifactDescriptor
            {
                RelativePath = relativePath,
                FilePath     = filePath,
                SizeBytes    = info.Length,
                LastModified = info.LastWriteTimeUtc,
            };

            // Small yield to avoid starving the thread pool on huge repos
            await Task.Yield();
        }
    }

    private string BuildRelativePath(string relativeToMount)
    {
        // Normalise separators (Windows compat in tests)
        var normalized = relativeToMount.Replace(Path.DirectorySeparatorChar, '/');

        if (!_reposiliteLayout || string.IsNullOrEmpty(_repositoryName))
            return normalized;

        // Strip leading <repository>/ segment
        var prefix = _repositoryName.TrimEnd('/') + "/";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return normalized[prefix.Length..];

        return normalized;
    }
}

