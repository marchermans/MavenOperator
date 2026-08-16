using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace MavenOperator.VirtualProxy.Services;

/// <summary>
/// Configuration for a single upload target, resolved by the reconciler.
/// </summary>
public sealed record UploadTargetConfig(
    string Name,
    string BaseUrl,
    string? AuthHeader); // base64-encoded Basic auth header (e.g. "Basic dXNlcjpwYXNz")

/// <summary>
/// Configuration for Virtual repository upload fan-out, injected via ConfigMap.
/// </summary>
public sealed class VirtualUploadConfig
{
    /// <summary>
    /// Name of the virtual repository (for metrics and logging).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// List of targets to which uploads are fanned out in parallel.
    /// Empty list means uploads are disabled (405).
    /// </summary>
    public List<UploadTargetConfig> Targets { get; set; } = [];

    /// <summary>
    /// Per-target timeout for upload requests (default: 30 seconds).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Number of retry attempts per target on transient failures (default: 2).
    /// </summary>
    public int RetryAttempts { get; set; } = 2;
}

/// <summary>
/// Result of an upload attempt to a single target.
/// </summary>
public sealed record TargetUploadResult(
    string TargetName,
    bool Success,
    int? StatusCode,
    string? ErrorMessage);

/// <summary>
/// Aggregated result of a fan-out upload operation across all targets.
/// </summary>
public sealed class VirtualUploadResult
{
    /// <summary>
    /// HTTP status code to return to the client:
    /// - 201 if all targets succeeded
    /// - 207 (Multi-Status) if some succeeded and some failed
    /// - 502 if all targets failed or no targets configured
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// Per-target results for logging and debugging.
    /// </summary>
    public IReadOnlyList<TargetUploadResult> TargetResults { get; init; } = [];

    /// <summary>
    /// Human-readable summary of the upload result.
    /// </summary>
    public string Summary => StatusCode switch
    {
        201 => $"Uploaded to all {TargetResults.Count} target(s)",
        207 => $"Partial success: {TargetResults.Count(r => r.Success)}/{TargetResults.Count} targets succeeded",
        _ => $"Upload failed on all {TargetResults.Count} target(s)"
    };

    /// <summary>
    /// Creates a VirtualUploadResult from individual target results.
    /// </summary>
    public static VirtualUploadResult FromTargets(IEnumerable<TargetUploadResult> results)
    {
        var list = results.ToList();
        if (list.Count == 0)
            return new VirtualUploadResult { StatusCode = 502, TargetResults = list };

        var successes = list.Count(r => r.Success);
        var statusCode = successes == list.Count ? 201 : successes > 0 ? 207 : 502;

        return new VirtualUploadResult { StatusCode = statusCode, TargetResults = list };
    }
}

/// <summary>
/// Handles upload fan-out for Virtual repositories.
/// Distributes PUT/DELETE requests to declared member repos in parallel with retry logic.
/// </summary>
public interface IVirtualUploadService
{
    /// <summary>
    /// Uploads content to all configured targets in parallel.
    /// Returns aggregated result (201/207/502).
    /// </summary>
    Task<VirtualUploadResult> UploadAsync(
        string artifactPath,
        Stream content,
        CancellationToken ct);

    /// <summary>
    /// Deletes an artifact from all configured targets in parallel.
    /// Returns aggregated result (201/207/502).
    /// </summary>
    Task<VirtualUploadResult> DeleteAsync(
        string artifactPath,
        CancellationToken ct);

    /// <summary>
    /// Creates a directory on all configured targets in parallel.
    /// Returns aggregated result (201/207/502).
    /// </summary>
    Task<VirtualUploadResult> CreateDirectoryAsync(
        string artifactPath,
        CancellationToken ct);

    /// <summary>
    /// Checks if upload fan-out is enabled (has at least one target configured).
    /// </summary>
    bool IsEnabled { get; }
}

/// <inheritdoc/>
public sealed class VirtualUploadService(
    VirtualUploadConfig config,
    HttpClient httpClient,
    ILogger<VirtualUploadService> logger,
    IVirtualProxyMetrics metrics)
    : IVirtualUploadService
{
    public bool IsEnabled => config.Targets.Count > 0;

    /// <inheritdoc/>
    public async Task<VirtualUploadResult> UploadAsync(string artifactPath, Stream content, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            logger.LogWarning("[VirtualUpload] Upload attempted but no targets configured for path: {Path}", artifactPath);
            return new VirtualUploadResult { StatusCode = 502, TargetResults = [] };
        }

        var results = await ExecuteFanOutAsync(artifactPath, ct, async (target, token) =>
        {
            // Read content into memory for parallel upload to multiple targets.
            // For large artifacts, consider streaming with tee or chunked approach in future.
            byte[]? buffer = null;
            try
            {
                if (!content.CanSeek || content.Position > 0)
                    content.Seek(0, SeekOrigin.Begin);

                using var ms = new MemoryStream();
                await content.CopyToAsync(ms, token);
                buffer = ms.ToArray();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "[VirtualUpload] Failed to read upload content for {Path}", artifactPath);
                return new TargetUploadResult(target.Name, false, null, "Failed to read upload content");
            }

            var url = target.BaseUrl.TrimEnd('/') + "/" + artifactPath.TrimStart('/');
            using var requestContent = new ByteArrayContent(buffer);
            requestContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            return await SendRequestAsync(target, HttpMethod.Put, url, requestContent, token);
        });

        return VirtualUploadResult.FromTargets(results);
    }

    /// <inheritdoc/>
    public async Task<VirtualUploadResult> DeleteAsync(string artifactPath, CancellationToken ct)
    {
        if (!IsEnabled)
            return new VirtualUploadResult { StatusCode = 502, TargetResults = [] };

        var results = await ExecuteFanOutAsync(artifactPath, ct, async (target, token) =>
        {
            var url = target.BaseUrl.TrimEnd('/') + "/" + artifactPath.TrimStart('/');
            return await SendRequestAsync(target, HttpMethod.Delete, url, null, token);
        });

        return VirtualUploadResult.FromTargets(results);
    }

    /// <inheritdoc/>
    public async Task<VirtualUploadResult> CreateDirectoryAsync(string artifactPath, CancellationToken ct)
    {
        if (!IsEnabled)
            return new VirtualUploadResult { StatusCode = 502, TargetResults = [] };

        var results = await ExecuteFanOutAsync(artifactPath, ct, async (target, token) =>
        {
            var url = target.BaseUrl.TrimEnd('/') + "/" + artifactPath.TrimStart('/');
            return await SendRequestAsync(target, HttpMethod.Put, url, new ByteArrayContent(Array.Empty<byte>()), token);
        });

        return VirtualUploadResult.FromTargets(results);
    }

    private async Task<List<TargetUploadResult>> ExecuteFanOutAsync(
        string artifactPath,
        CancellationToken ct,
        Func<UploadTargetConfig, CancellationToken, Task<TargetUploadResult>> operation)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("[VirtualUpload] Starting fan-out to {Count} target(s) for {Path}",
            config.Targets.Count, artifactPath);

        // Execute all uploads in parallel with per-target timeout.
        var tasks = config.Targets.Select(async target =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

            try
            {
                return await operation(target, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("[VirtualUpload] Target {Target} timed out after {Timeout}s for {Path}",
                    target.Name, config.TimeoutSeconds, artifactPath);
                return new TargetUploadResult(target.Name, false, null, "Request timed out");
            }
            catch (OperationCanceledException)
            {
                throw; // Propagate user-initiated cancellation.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "[VirtualUpload] Unexpected error uploading to target {Target}: {Message}",
                    target.Name, ex.Message);
                return new TargetUploadResult(target.Name, false, null, $"Internal error: {ex.GetType().Name}");
            }
        });

        var results = await Task.WhenAll(tasks);
        sw.Stop();

        // Log summary.
        var successCount = results.Count(r => r.Success);
        logger.LogInformation("[VirtualUpload] Fan-out complete for {Path}: {Success}/{Total} succeeded in {Elapsed:F1}s",
            artifactPath, successCount, config.Targets.Count, sw.Elapsed.TotalSeconds);

        // Record metrics.
        foreach (var result in results)
        {
            metrics.RecordUploadRequest(config.Name ?? "unknown", result.TargetName, result.Success, sw.Elapsed.TotalSeconds);
        }

        return results.ToList();
    }

    private async Task<TargetUploadResult> SendRequestAsync(
        UploadTargetConfig target,
        HttpMethod method,
        string url,
        HttpContent? content,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        logger.LogDebug("[VirtualUpload] {Method} to target {Target}: {Url}", method.Method, target.Name, url);

        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (content is not null)
                request.Content = content;

            // Apply auth header if configured for this target.
            if (!string.IsNullOrWhiteSpace(target.AuthHeader))
                request.Headers.Authorization = System.Net.Http.Headers.AuthenticationHeaderValue.Parse(target.AuthHeader);

            int statusCode = 0;
            Exception? lastException = null;

            // Manual retry with exponential backoff.
            for (int attempt = 0; attempt <= config.RetryAttempts; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        var waitTime = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        logger.LogDebug("[VirtualUpload] Retrying target {Target} (attempt {Attempt}) after {Wait}s",
                            target.Name, attempt, waitTime.TotalSeconds);
                        await Task.Delay(waitTime, ct);
                    }

                    using var resp = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    statusCode = (int)resp.StatusCode;
                    lastException = null; // Success.
                    break;
                }
                catch (HttpRequestException ex) when (attempt < config.RetryAttempts && !ct.IsCancellationRequested)
                {
                    lastException = ex;
                }
            }

            if (lastException is not null)
                throw lastException; // Will be caught below.

            sw.Stop();

            bool success = statusCode is >= 200 and < 300;
            logger.LogDebug("[VirtualUpload] Target {Target} returned {Status} in {Elapsed:F1}s",
                target.Name, statusCode, sw.Elapsed.TotalSeconds);

            return new TargetUploadResult(
                target.Name,
                success,
                statusCode,
                success ? null : $"Upstream returned {statusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            logger.LogWarning(ex, "[VirtualUpload] Target {Target} failed after {Elapsed:F1}s: {Message}",
                target.Name, sw.Elapsed.TotalSeconds, ex.Message);

            return new TargetUploadResult(
                target.Name,
                false,
                null,
                $"Connection error: {ex.GetType().Name}");
        }
    }
}
