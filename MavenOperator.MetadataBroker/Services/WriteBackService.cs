using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace MavenOperator.MetadataBroker.Services;

/// <summary>
/// Debounced, atomic write-back service.
///
/// When metadata is regenerated from disk, the broker stores it in memory
/// and schedules a delayed write to the PVC (default: 2 seconds after the
/// last regeneration).  The write uses temp-file + rename for atomicity
/// on POSIX filesystems (no torn reads for direct PVC consumers).
/// </summary>
public sealed class WriteBackService : IDisposable
{
    // Key: artifact path (relative, e.g. "com/example/mylib/maven-metadata.xml")
    // Value: timer + cancellation token source for pending write-back.
    private readonly ConcurrentDictionary<string, (System.Timers.Timer Timer, CancellationTokenSource Cts)>
        _pendingWrites = new(StringComparer.Ordinal);

    private readonly string _repositoryRoot;
    private readonly TimeSpan _debounceInterval;
    private readonly object _logger; // Any ILogger instance — we avoid generics.

    /// <param name="repositoryRoot">Mounted PVC root (e.g. /var/maven/repository).</param>
    /// <param name="logger">Optional logger (any type; we just call .LogInformation / .LogError).</param>
    /// <param name="debounceSeconds">Write-back delay in seconds (default: 2).</param>
    public WriteBackService(
        string repositoryRoot,
        object? logger = null,
        int debounceSeconds = 2)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _logger = logger ?? NullLogger.Instance;
        _debounceInterval = TimeSpan.FromSeconds(debounceSeconds);
    }

    public void ScheduleWriteBack(
        string relativePath, string artifactDir, string xmlBody)
    {
        var metadataPath = Path.Combine(artifactDir, "maven-metadata.xml");

        if (!Directory.Exists(artifactDir))
        {
            LogDebug("Write-back skipped: artifact directory does not exist: {Dir}", artifactDir);
            return;
        }

        // Cancel any existing pending write for this path before scheduling a new one.
        CancelPendingWriteBack(relativePath);

        var cts = new CancellationTokenSource();

        var timer = new System.Timers.Timer
        {
            Interval = _debounceInterval.TotalMilliseconds,
            AutoReset = false,
        };

        var tmpPath = metadataPath + ".tmp-" + Guid.NewGuid();

        timer.Elapsed += async (_, _) =>
        {
            try
            {
                await File.WriteAllBytesAsync(tmpPath, Encoding.UTF8.GetBytes(xmlBody), cts.Token);
                File.Move(tmpPath, metadataPath, overwrite: true);
                LogInformation(
                    "Debounced write-back completed: {Path} ({Size} bytes)",
                    metadataPath, xmlBody.Length);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown or re-invalidation.
                try { File.Delete(tmpPath); } catch { }
            }
            catch (Exception ex)
            {
                LogError(ex,
                    "Debounced write-back failed for {Path}: {Message}", metadataPath, ex.Message);
                try { File.Delete(tmpPath); } catch { }
            }
            finally
            {
                lock (_pendingWrites)
                {
                    _pendingWrites.TryRemove(relativePath, out _);
                }
            }
        };

        lock (_pendingWrites)
        {
            timer.Start();
            _pendingWrites[relativePath] = (timer, cts);
        }
    }

    public void CancelPendingWriteBack(string relativePath)
    {
        lock (_pendingWrites)
        {
            if (_pendingWrites.TryRemove(relativePath, out var entry))
            {
                entry.Cts.Cancel();
                entry.Timer.Dispose();
                entry.Cts.Dispose();
                LogDebug("Cancelled pending write-back for {Path}", relativePath);
            }
        }
    }

    public int PendingWriteCount => _pendingWrites.Count;

    public void Dispose()
    {
        foreach (var entry in _pendingWrites.Values)
        {
            entry.Timer.Dispose();
            entry.Cts.Dispose();
        }
        _pendingWrites.Clear();
    }

    // ── Logger facade ──────────────────────────────────────────────────────────

    private void LogInformation(string message, params object?[] args)
    {
        if (_logger is ILogger<T> logger)
            logger.LogInformation(message, args);
    }

    private void LogError(Exception ex, string message, params object?[] args)
    {
        if (_logger is ILogger<T> logger)
            logger.LogError(ex, message, args);
    }

    private void LogDebug(string message, params object?[] args)
    {
        if (_logger is ILogger<T> logger)
            logger.LogDebug(message, args);
    }

    // Dummy generic to let us check `is ILogger<T>`.
    private sealed class T { }
}
