using MavenOperator.ImportJob.Models;
using MavenOperator.ImportJob.Sinks;
using MavenOperator.ImportJob.Sources;

namespace MavenOperator.ImportJob.Services;

/// <summary>
/// Orchestrates artifact discovery (source) and writing (sink) with bounded parallelism.
/// Errors on individual artifacts are isolated — one failure never aborts the whole job.
/// </summary>
public sealed class ArtifactCrawler
{
    private readonly ILogger<ArtifactCrawler> _logger;

    public ArtifactCrawler(ILogger<ArtifactCrawler> logger)
    {
        _logger = logger;
    }

    public async Task<ImportResult> RunAsync(
        IRepositorySource source,
        IRepositorySink sink,
        Func<ArtifactDescriptor, CancellationToken, Task<Stream?>>? openStream,
        ImportOptions options,
        ImportFilters filters,
        ProgressReporter? reporter,
        CancellationToken ct)
    {
        var failedArtifacts = new List<ArtifactResult>();

        long discovered = 0, copied = 0, failed = 0, bytesTotal = 0;

        using var semaphore = new SemaphoreSlim(options.Parallelism, options.Parallelism);
        var tasks = new List<Task>();

        await foreach (var artifact in source.CrawlAsync(filters, ct))
        {
            Interlocked.Increment(ref discovered);

            if (options.DryRun)
            {
                _logger.LogInformation("[DryRun] Discovered: {Path}", artifact.RelativePath);
                continue;
            }

            // Skip existing artifacts when overwriteExisting=false
            if (!options.OverwriteExisting && await sink.ExistsAsync(artifact, ct))
            {
                _logger.LogDebug("Skipping existing artifact: {Path}", artifact.RelativePath);
                continue;
            }

            await semaphore.WaitAsync(ct);

            var localArtifact = artifact;
            var task = Task.Run(async () =>
            {
                try
                {
                    Stream? content = null;

                    // For API sources, open HTTP download stream
                    if (localArtifact.FilePath is null && openStream is not null)
                    {
                        content = await openStream(localArtifact, ct);
                        if (content is null)
                        {
                            _logger.LogWarning("Could not open stream for {Path}", localArtifact.RelativePath);
                            Interlocked.Increment(ref failed);
                            lock (failedArtifacts)
                            {
                                if (failedArtifacts.Count < 100)
                                    failedArtifacts.Add(new ArtifactResult
                                    {
                                        RelativePath  = localArtifact.RelativePath,
                                        FailureReason = "Could not open download stream",
                                    });
                            }
                            return;
                        }
                    }

                    await using (content)
                    {
                        var bytes = await sink.WriteAsync(localArtifact, content, ct);
                        Interlocked.Add(ref bytesTotal, bytes);
                        Interlocked.Increment(ref copied);
                        _logger.LogDebug("Copied {Path} ({Bytes} bytes)", localArtifact.RelativePath, bytes);
                    }

                    if (reporter is not null)
                    {
                        var progress = MakeResult(discovered, copied, failed, bytesTotal, failedArtifacts);
                        await reporter.ReportAsync(progress, ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to copy artifact {Path}", localArtifact.RelativePath);
                    Interlocked.Increment(ref failed);
                    lock (failedArtifacts)
                    {
                        if (failedArtifacts.Count < 100)
                            failedArtifacts.Add(new ArtifactResult
                            {
                                RelativePath  = localArtifact.RelativePath,
                                FailureReason = ex.Message,
                            });
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct);

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        var result = MakeResult(discovered, copied, failed, bytesTotal, failedArtifacts);

        // Final progress report
        if (reporter is not null)
            await reporter.ReportAsync(result, ct);

        _logger.LogInformation(
            "Import complete: discovered={Discovered} copied={Copied} failed={Failed} bytes={Bytes}",
            result.ArtifactsDiscovered, result.ArtifactsCopied, result.ArtifactsFailed, result.BytesTransferred);

        return result;
    }

    private static ImportResult MakeResult(
        long discovered, long copied, long failed, long bytes, List<ArtifactResult> failedList) =>
        new()
        {
            ArtifactsDiscovered = discovered,
            ArtifactsCopied     = copied,
            ArtifactsFailed     = failed,
            BytesTransferred    = bytes,
            FailedArtifacts     = failedList,
        };
}
