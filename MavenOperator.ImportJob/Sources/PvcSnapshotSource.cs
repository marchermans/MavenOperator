using MavenOperator.ImportJob.Models;
using MavenOperator.ImportJob.Services;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.ComTypes;

namespace MavenOperator.ImportJob.Sources;

/// <summary>
/// Crawls a mounted PVC filesystem (Modes B and C).
/// Supports three layouts:
///   - Reposilite on-disk: /&lt;repository&gt;/com/example/my-lib/1.0/my-lib-1.0.jar
///   - AppRoot layout:     /&lt;approot&gt;/repositories/&lt;repository&gt;/com/example/my-lib/1.0/my-lib-1.0.jar (crawls only that repo)
///   - Maven standard:     /com/example/my-lib/1.0/my-lib-1.0.jar
/// </summary>
public sealed class PvcSnapshotSource : IRepositorySource
{
    private readonly string _crawlRoot;         // effective directory to crawl from
    private readonly string? _repositoryName;   // used for Reposilite layout stripping (non-approot mode)
    private readonly bool _reposiliteLayout;
    private readonly ILogger<PvcSnapshotSource> _logger;

    private static readonly string[] SkipDirectories = [".index", ".cache", ".git"];

    public PvcSnapshotSource(
        string mountPath,
        bool reposiliteLayout,
        string? repositoryName,
        bool appRootPvcMode,
        ILogger<PvcSnapshotSource> logger)
    {
        _logger = logger;

        if (appRootPvcMode && string.IsNullOrEmpty(repositoryName))
            throw new ArgumentException("ReposiliteRepositoryName is required when AppRootPvcMode is true");

        // Determine the effective crawl root based on layout mode.
        if (appRootPvcMode)
        {
            _crawlRoot = FindAppRootRepoDirectory(mountPath, repositoryName!, logger);
            _repositoryName = null;  // not needed for path stripping in approot mode
            _reposiliteLayout = false;
        }
        else
        {
            _crawlRoot = mountPath;
            _repositoryName = repositoryName;
            _reposiliteLayout = reposiliteLayout;
        }

        logger.LogInformation(
            "PvcSnapshotSource initialized: crawlRoot={CrawlRoot}, reposiliteLayout={ReposiliteLayout}",
            _crawlRoot, _reposiliteLayout);
    }

    private static string FindAppRootRepoDirectory(string mountPath, string repositoryName, ILogger<PvcSnapshotSource> logger)
    {
        if (!Directory.Exists(mountPath))
            throw new DirectoryNotFoundException($"Mount path does not exist: {mountPath}");

        return Path.Combine(mountPath, "repositories", repositoryName);
    }

    public async IAsyncEnumerable<ArtifactDescriptor> CrawlAsync(
        ImportFilters filters,
        [EnumeratorCancellation] CancellationToken ct)
    {
        DateTimeOffset? since = filters.SinceTimestamp is { Length: > 0 } ts
            ? DateTimeOffset.Parse(ts)
            : null;

        if (!Directory.Exists(_crawlRoot))
        {
            _logger.LogError("Source crawl root does not exist: {CrawlRoot}", _crawlRoot);
            yield break;
        }

        var files = Directory.EnumerateFiles(_crawlRoot, "*", new EnumerationOptions
        {
            RecurseSubdirectories  = true,
            IgnoreInaccessible     = true,
            AttributesToSkip       = FileAttributes.Hidden | FileAttributes.System,
        });

        foreach (var filePath in files)
        {
            if (ct.IsCancellationRequested) yield break;

            // Skip dot-directories
            var relativeFull = Path.GetRelativePath(_crawlRoot, filePath);
            if (relativeFull.Split(Path.DirectorySeparatorChar)
                    .Any(seg => SkipDirectories.Any(d => seg == d)))
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

    private string BuildRelativePath(string relativeToCrawlRoot)
    {
        // Normalise separators (Windows compat in tests)
        var normalized = relativeToCrawlRoot.Replace(Path.DirectorySeparatorChar, '/');

        if (_reposiliteLayout && !string.IsNullOrEmpty(_repositoryName))
        {
            // Standard Reposilite layout: strip leading <repository>/ segment.
            // e.g., "releases/com/example/..." -> "com/example/..."
            var prefix = _repositoryName.TrimEnd('/') + "/";
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return normalized[prefix.Length..];
        }

        // AppRoot mode: we already crawl from the repo root directory, so no stripping needed.
        return normalized;
    }
}

