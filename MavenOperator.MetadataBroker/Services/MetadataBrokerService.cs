using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace MavenOperator.MetadataBroker.Services;

/// <summary>
/// Core broker service: scans directories on disk, fingerprints changes,
/// generates metadata XML (both version-listing and snapshot), serves cached
/// responses with ETag/304, and debounced atomic write-back to PVC.
/// </summary>
public sealed class MetadataBrokerService : IDisposable
{
    private readonly string _repositoryRoot;
    private readonly IMemoryCache _cache;
    private readonly MetadataGenerationService _generation;
    private readonly WriteBackService _writeBack;
    private readonly object _logger;

    // Per-artifact single-flight gate.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    // Cache entry: (xmlBody, eTag, isSnapshot).
    private record CacheEntry(string XmlBody, string ETag, bool IsSnapshot);

    private static CacheEntry CreateCacheEntry(string xmlBody, bool isSnapshot)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xmlBody)));
        var eTag = $"\"{sha256}\"";
        return new CacheEntry(xmlBody, eTag, isSnapshot);
    }

    /// <summary>
    /// DI constructor — called when registered via builder.Services.AddSingleton.
    /// The IMemoryCache is resolved from the container (registered by Program).
    /// The repository root is read from the REPOSITORY_ROOT env var.
    /// </summary>
    public MetadataBrokerService(
        IMemoryCache cache,
        object? logger = null)
    {
        var repoRoot = Environment.GetEnvironmentVariable("REPOSITORY_ROOT")
            ?? "/var/maven/repository";
        _repositoryRoot = Path.GetFullPath(repoRoot);
        _cache = cache;
        _generation = new MetadataGenerationService();
        _writeBack = new WriteBackService(_repositoryRoot, logger);
        _logger = logger ?? NullLogger.Instance;
    }

    // ── GET / HEAD — serve or regenerate ────────────────────────────────────────

    public async Task<(string? Body, string? ETag)> GetOrRegenerateAsync(
        string relativePath, CancellationToken ct)
    {
        var artifactDir = ResolveArtifactDirectory(relativePath);
        if (artifactDir is null)
            return (null, null);

        var cacheKey = BuildCacheKey(artifactDir);

        // Fast path: cache hit with matching fingerprint.
        if (_cache.TryGetValue(cacheKey, out CacheEntry? entry) && entry is not null)
        {
            LogDebug("Cache hit for {Path}", relativePath);
            return (entry.XmlBody, entry.ETag);
        }

        // Slow path: single-flight regeneration.
        var gate = _locks.GetOrAdd(artifactDir, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }

        try
        {
            // Double-check after acquiring the lock (another request may have
            // regenerated while we waited).
            if (_cache.TryGetValue(cacheKey, out entry) && entry is not null)
            {
                LogDebug("Regen completed by concurrent request for {Path}", relativePath);
                return (entry.XmlBody, entry.ETag);
            }

            var xml = _generation.GenerateMetadataAsync(artifactDir, relativePath);
            if (xml is null)
                return (null, null); // 404 — no version dirs.

            entry = CreateCacheEntry(xml, IsSnapshotPath(relativePath));

            // Store in cache.  Fingerprint-based eviction ensures stale entries
            // are invalidated automatically — no explicit expiration needed.
            _cache.Set(cacheKey, entry);

            // Schedule debounced write-back (fire-and-forget).
            _writeBack.ScheduleWriteBack(relativePath, artifactDir, xml);

            LogInformation("Regenerated metadata for {Path}", relativePath);
            return (entry.XmlBody, entry.ETag);
        }
        finally
        {
            gate.Release();
        }
    }

    // ── PUT / POST — invalidate ─────────────────────────────────────────────────

    public bool Invalidate(string relativePath)
    {
        var artifactDir = ResolveArtifactDirectory(relativePath);
        if (artifactDir is null)
            return false;

        var cacheKey = BuildCacheKey(artifactDir);
        // .NET 10 IMemoryCache.Remove returns bool, but some versions return object.
        // Just remove without checking the result.
        _cache.Remove(cacheKey);

        LogDebug("Cache invalidated for {Path} (PUT/POST)", relativePath);

        _writeBack.CancelPendingWriteBack(relativePath);
        return true;
    }

    // ── Cache accessor (for conditional GET) ─────────────────────────────────────

    public string? GetCacheETag(string relativePath)
    {
        var artifactDir = ResolveArtifactDirectory(relativePath);
        if (artifactDir is null) return null;

        var cacheKey = BuildCacheKey(artifactDir);
        return _cache.TryGetValue(cacheKey, out CacheEntry? entry) && entry is not null ? entry.ETag : null;
    }

    // ── Path resolution & fingerprinting ────────────────────────────────────────

    private string? ResolveArtifactDirectory(string relativePath)
    {
        var idx = relativePath.LastIndexOf("maven-metadata.xml", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return null;

        var baseName = relativePath[..idx].TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseName)) return null;

        var resolvedPath = Path.GetFullPath(Path.Combine(_repositoryRoot, baseName));

        // Security: must be under repository root.
        var rootSep = _repositoryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _repositoryRoot
            : _repositoryRoot + Path.DirectorySeparatorChar;

        if (!resolvedPath.StartsWith(rootSep))
        {
            LogWarning("Path traversal attempt: {Path}", relativePath);
            return null;
        }

        return resolvedPath;
    }

    private string BuildCacheKey(string artifactDir)
    {
        try
        {
            if (!Directory.Exists(artifactDir))
                return "nonexistent:" + artifactDir;

            var isSnapshot = IsSnapshotPath(artifactDir);
            var parts = new List<string>();

            if (isSnapshot)
            {
                foreach (var file in Directory.EnumerateFiles(artifactDir))
                {
                    var name = Path.GetFileName(file);
                    if (Regex.IsMatch(name, @"^(.*)-(\d{8}\.\d{6}-\d+)\.(\w+)$"))
                    {
                        var fi = new FileInfo(file);
                        parts.Add($"{name}|{fi.LastWriteTimeUtc:O}");
                    }
                }
            }
            else
            {
                foreach (var subDir in Directory.EnumerateDirectories(artifactDir))
                {
                    var versionName = Path.GetFileName(subDir);
                    var dirMaxTime = GetMaxDirectoryTime(subDir);
                    parts.Add($"{versionName}|{dirMaxTime:O}");
                }
            }

            parts.Sort();
            var joined = string.Join(",", parts);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
            return Convert.ToHexStringLower(hash);
        }
        catch (Exception ex)
        {
            LogWarning(ex, "Fingerprint failed for {Dir}", artifactDir);
            return "error:" + artifactDir + ":" + Guid.NewGuid();
        }
    }

    private static DateTime GetMaxDirectoryTime(string directory)
    {
        var maxTime = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(file);
            if (fi.LastWriteTimeUtc > maxTime)
                maxTime = fi.LastWriteTimeUtc;
        }
        return maxTime;
    }

    private static bool IsSnapshotPath(string relativePath)
    {
        var idx = relativePath.LastIndexOf("maven-metadata.xml", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return false;

        var baseName = relativePath[..idx].TrimEnd('/');
        var lastSegment = Path.GetFileName(baseName);
        return lastSegment.EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase);
    }

    // ── Logger facade (matches WriteBackService pattern) ────────────────────────

    private void LogInformation(string message, params object?[] args)
    {
        if (_logger is ILogger<T> logger)
            logger.LogInformation(message, args);
    }

    private void LogWarning(string message, params object?[] args)
    {
        if (_logger is ILogger<T> logger)
            logger.LogWarning(message, args);
    }

    private void LogWarning(Exception ex, string message, params object?[] args)
    {
        if (_logger is ILogger<T> logger)
            logger.LogWarning(ex, message, args);
    }

    private void LogDebug(string message, params object?[] args)
    {
        if (_logger is ILogger<T> logger)
            logger.LogDebug(message, args);
    }

    private sealed class T { }

    // ── IDisposable (cleanup) ────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (var sem in _locks.Values)
            sem.Dispose();
        _locks.Clear();
        _writeBack.Dispose();
    }
}
