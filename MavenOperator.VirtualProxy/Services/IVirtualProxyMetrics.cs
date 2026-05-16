using Prometheus;

namespace MavenOperator.VirtualProxy.Services;

/// <summary>
/// Prometheus metrics for the Virtual-repository aggregation proxy.
/// Asset-level counters let dashboards answer "what artifact was requested most often?"
/// </summary>
public interface IVirtualProxyMetrics
{
    /// <summary>
    /// Records an incoming GET request for a specific asset path.
    /// </summary>
    /// <param name="repoName">Name of the virtual repository (from config).</param>
    /// <param name="artifactPath">The full path requested (e.g. "com/example/foo/1.0/foo-1.0.jar").</param>
    /// <param name="assetType">Coarse bucket: "jar", "pom", "metadata", "checksum", or "other".</param>
    /// <param name="statusCode">HTTP status code returned to the caller (200, 404, …).</param>
    void RecordRequest(string repoName, string artifactPath, string assetType, int statusCode);

    /// <summary>
    /// Records the latency of forwarding a request to a member.
    /// </summary>
    /// <param name="repoName">Virtual repository name.</param>
    /// <param name="memberName">Name of the upstream member repo.</param>
    /// <param name="success">Whether the member returned a 2xx response.</param>
    /// <param name="durationSeconds">Round-trip latency in seconds.</param>
    void RecordMemberRequest(string repoName, string memberName, bool success, double durationSeconds);

    /// <summary>
    /// Records a metadata-merge operation and how many sources contributed.
    /// </summary>
    void RecordMetadataMerge(string repoName, int memberCount, double durationSeconds);
}

/// <summary>
/// Prometheus-backed implementation of <see cref="IVirtualProxyMetrics"/>.
/// </summary>
public sealed class VirtualProxyMetrics : IVirtualProxyMetrics
{
    // virtual_proxy_requests_total{repo,asset_path,asset_type,status}
    private readonly Counter _requestsTotal = Metrics.CreateCounter(
        "virtual_proxy_requests_total",
        "Total number of GET requests received by the virtual proxy, per asset path.",
        new CounterConfiguration
        {
            LabelNames = ["repo_name", "asset_path", "asset_type", "status_code"]
        });

    // virtual_proxy_member_request_duration_seconds{repo,member,success}
    private readonly Histogram _memberDuration = Metrics.CreateHistogram(
        "virtual_proxy_member_request_duration_seconds",
        "Latency of forwarding a request to a member repository.",
        new HistogramConfiguration
        {
            LabelNames = ["repo_name", "member_name", "success"],
            Buckets = Histogram.ExponentialBuckets(0.005, 2, 10)
        });

    // virtual_proxy_metadata_merge_duration_seconds{repo}
    private readonly Histogram _metadataMergeDuration = Metrics.CreateHistogram(
        "virtual_proxy_metadata_merge_duration_seconds",
        "Time spent merging maven-metadata.xml from all members.",
        new HistogramConfiguration
        {
            LabelNames = ["repo_name"],
            Buckets = Histogram.ExponentialBuckets(0.005, 2, 8)
        });

    // virtual_proxy_metadata_merge_member_count{repo}
    private readonly Histogram _metadataMergeMemberCount = Metrics.CreateHistogram(
        "virtual_proxy_metadata_merge_member_count",
        "Number of members that contributed to a metadata merge.",
        new HistogramConfiguration
        {
            LabelNames = ["repo_name"],
            Buckets = [1, 2, 3, 5, 10]
        });

    /// <inheritdoc/>
    public void RecordRequest(string repoName, string artifactPath, string assetType, int statusCode)
        => _requestsTotal.WithLabels(repoName, artifactPath, assetType, statusCode.ToString()).Inc();

    /// <inheritdoc/>
    public void RecordMemberRequest(string repoName, string memberName, bool success, double durationSeconds)
        => _memberDuration.WithLabels(repoName, memberName, success ? "true" : "false").Observe(durationSeconds);

    /// <inheritdoc/>
    public void RecordMetadataMerge(string repoName, int memberCount, double durationSeconds)
    {
        _metadataMergeDuration.WithLabels(repoName).Observe(durationSeconds);
        _metadataMergeMemberCount.WithLabels(repoName).Observe(memberCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Classifies an artifact path into a coarse asset-type bucket.
    /// </summary>
    public static string ClassifyAssetType(string path)
    {
        if (path.EndsWith("maven-metadata.xml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("maven-metadata.xml.sha1", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("maven-metadata.xml.md5", StringComparison.OrdinalIgnoreCase))
            return "metadata";

        if (path.EndsWith(".sha1", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".md5", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            return "checksum";

        if (path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".war", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".aar", StringComparison.OrdinalIgnoreCase))
            return "jar";

        if (path.EndsWith(".pom", StringComparison.OrdinalIgnoreCase))
            return "pom";

        return "other";
    }
}

