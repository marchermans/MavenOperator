using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace MavenOperator.VirtualProxy.Services;

/// <summary>
/// Describes a single member of a virtual repository.
/// </summary>
public sealed record VirtualMember(string Name, string BaseUrl);

/// <summary>
/// Configuration for the virtual aggregation proxy, injected via ConfigMap.
/// </summary>
public sealed class VirtualRepoConfig
{
    public string Name { get; set; } = string.Empty;
    public List<VirtualMember> Members { get; set; } = [];
    public int MetadataCacheTtlSeconds { get; set; } = 60;
}

/// <summary>
/// Handles fan-out GET requests to Virtual repository members.
/// Merges maven-metadata.xml; forwards first-success for all other artifacts.
/// </summary>
public interface IVirtualProxyService
{
    /// <summary>
    /// For maven-metadata.xml paths: fetch from all members in parallel and merge.
    /// For all other paths: return the first successful response.
    /// Returns null when all members return 404.
    /// </summary>
    Task<VirtualProxyResult?> ForwardAsync(string artifactPath, CancellationToken ct);
}

/// <summary>The result of a virtual proxy forward operation.</summary>
public sealed record VirtualProxyResult(
    Stream Content,
    string ContentType,
    long? ContentLength);

/// <inheritdoc/>
public sealed class VirtualProxyService(
    VirtualRepoConfig config,
    IMetadataMergeService metadataMerge,
    HttpClient httpClient,
    ILogger<VirtualProxyService> logger,
    IMemoryCache cache,
    IVirtualProxyMetrics metrics)
    : IVirtualProxyService
{
    /// <inheritdoc/>
    public async Task<VirtualProxyResult?> ForwardAsync(string artifactPath, CancellationToken ct)
    {
        if (artifactPath.EndsWith("maven-metadata.xml", StringComparison.OrdinalIgnoreCase))
            return await ForwardMetadataAsync(artifactPath, ct);

        return await ForwardFirstSuccessAsync(artifactPath, ct);
    }

    private async Task<VirtualProxyResult?> ForwardMetadataAsync(string artifactPath, CancellationToken ct)
    {
        var cacheKey = $"{config.Name}:{artifactPath}";

        if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
        {
            logger.LogDebug("[Virtual] Metadata cache hit for {Path}", artifactPath);
            return ToStreamResult(cached, "application/xml");
        }

        var sw = Stopwatch.StartNew();
        var memberUrls = config.Members.Select(m => m.BaseUrl).ToList();
        var merged = await metadataMerge.MergeMetadataAsync(memberUrls, artifactPath, ct);
        sw.Stop();

        metrics.RecordMetadataMerge(config.Name, memberUrls.Count, sw.Elapsed.TotalSeconds);

        if (merged is null)
            return null;

        cache.Set(cacheKey, merged, TimeSpan.FromSeconds(config.MetadataCacheTtlSeconds));

        return ToStreamResult(merged, "application/xml");
    }

    private async Task<VirtualProxyResult?> ForwardFirstSuccessAsync(string artifactPath, CancellationToken ct)
    {
        foreach (var member in config.Members)
        {
            var url = member.BaseUrl.TrimEnd('/') + "/" + artifactPath.TrimStart('/');
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    metrics.RecordMemberRequest(config.Name, member.Name, false, sw.Elapsed.TotalSeconds);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    metrics.RecordMemberRequest(config.Name, member.Name, false, sw.Elapsed.TotalSeconds);
                    logger.LogWarning("[Virtual] Member {Name} returned {Status} for {Path}",
                        member.Name, response.StatusCode, artifactPath);
                    continue;
                }

                metrics.RecordMemberRequest(config.Name, member.Name, true, sw.Elapsed.TotalSeconds);

                var stream      = await response.Content.ReadAsStreamAsync(ct);
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                var length      = response.Content.Headers.ContentLength;

                return new VirtualProxyResult(stream, contentType, length);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                metrics.RecordMemberRequest(config.Name, member.Name, false, sw.Elapsed.TotalSeconds);
                logger.LogWarning(ex, "[Virtual] Member {Name} failed for {Path}", member.Name, artifactPath);
            }
        }

        return null;
    }

    private static VirtualProxyResult ToStreamResult(string content, string contentType)
    {
        var bytes  = System.Text.Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        return new VirtualProxyResult(stream, contentType, bytes.Length);
    }
}

