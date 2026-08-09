using MavenOperator.ImportJob.Models;

namespace MavenOperator.ImportJob.Sinks;

/// <summary>
/// Writes artifacts directly to a mounted PVC filesystem.
/// This is the preferred sink — no HTTP overhead, no auth policies,
/// and throughput is bounded only by disk I/O.
/// </summary>
public sealed class DirectPvcSink : IRepositorySink
{
    private readonly string _targetMountPath;
    private readonly bool _dryRun;
    private readonly ILogger<DirectPvcSink> _logger;

    public DirectPvcSink(string targetMountPath, bool dryRun, ILogger<DirectPvcSink> logger)
    {
        _targetMountPath = targetMountPath;
        _dryRun          = dryRun;
        _logger          = logger;
    }

    public async Task<long> WriteAsync(ArtifactDescriptor artifact, Stream? content, CancellationToken ct)
    {
        var dest = Path.Combine(_targetMountPath, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (_dryRun)
        {
            _logger.LogInformation("[DryRun] Would write: {Path}", artifact.RelativePath);
            return 0;
        }

        // Fast path: source is already on disk (PVC-to-PVC copy)
        if (artifact.FilePath is { } srcPath && content is null)
        {
            return await CopyFileAsync(srcPath, dest, ct);
        }

        if (content is null)
        {
            _logger.LogWarning("No content stream for {Path} — skipping", artifact.RelativePath);
            return 0;
        }

        return await WriteStreamAsync(content, dest, ct);
    }

    public Task<bool> ExistsAsync(ArtifactDescriptor artifact, CancellationToken ct)
    {
        var dest = Path.Combine(_targetMountPath, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        return Task.FromResult(File.Exists(dest));
    }

    private async Task<long> CopyFileAsync(string source, string dest, CancellationToken ct)
    {
        EnsureDirectory(dest);

        _logger.LogDebug("Copying {Source} → {Dest}", source, dest);
        await using var src  = File.OpenRead(source);
        await using var dst  = File.Create(dest);
        await src.CopyToAsync(dst, ct);
        SetFilePermissions(dest);
        return src.Length;
    }

    private async Task<long> WriteStreamAsync(Stream content, string dest, CancellationToken ct)
    {
        EnsureDirectory(dest);

        _logger.LogDebug("Writing stream → {Dest}", dest);
        await using var dst  = File.Create(dest);
        await content.CopyToAsync(dst, ct);
        SetFilePermissions(dest);
        return dst.Position;
    }

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            SetDirectoryPermissions(dir);
        }
    }

#if NET && UNIX
    /// <summary>
    /// Sets file permissions to 0644 (rw-r--r--) so nginx and other consumers can read artifacts.
    /// Source PVC files may be owned by uid/gid 999 with mode 660; we normalize on write.
    /// </summary>
    private static void SetFilePermissions(string path)
    {
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.FilePermissions_0644);
        }
        catch (PlatformNotSupportedException)
        {
            // Non-Unix platform — ignore
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to set permissions on {Path}", path);
        }
    }

    /// <summary>
    /// Sets directory permissions to 0755 (rwxr-xr-x) so they are traversable by all consumers.
    /// </summary>
    private static void SetDirectoryPermissions(string path)
    {
        try
        {
            Directory.SetUnixFileMode(path, UnixFileMode.FilePermissions_0755);
        }
        catch (PlatformNotSupportedException)
        {
            // Non-Unix platform — ignore
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to set permissions on directory {Path}", path);
        }
    }
#else
    private static void SetFilePermissions(string _) { /* No-op on non-Unix */ }
    private static void SetDirectoryPermissions(string _) { /* No-op on non-Unix */ }
#endif
}

