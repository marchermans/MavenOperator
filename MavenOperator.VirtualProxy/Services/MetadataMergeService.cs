using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace MavenOperator.VirtualProxy.Services;

/// <summary>
/// Represents a parsed maven-metadata.xml document.
/// </summary>
public sealed record MavenMetadata(
    string GroupId,
    string ArtifactId,
    IReadOnlyList<string> Versions,
    string? Latest,
    string? Release,
    string? LastUpdated);

/// <summary>
/// Fetches maven-metadata.xml from member repositories in parallel and merges
/// the results according to the rules in 05-virtual-repo-metadata.md.
/// </summary>
public interface IMetadataMergeService
{
    /// <summary>
    /// Fetches metadata from all member base URLs in parallel and merges them.
    /// Returns null if all members return 404.
    /// </summary>
    Task<string?> MergeMetadataAsync(
        IEnumerable<string> memberBaseUrls,
        string artifactPath,
        CancellationToken ct);
}

/// <inheritdoc/>
public sealed class MetadataMergeService(
    HttpClient httpClient,
    ILogger<MetadataMergeService> logger)
    : IMetadataMergeService
{
    /// <inheritdoc/>
    public async Task<string?> MergeMetadataAsync(
        IEnumerable<string> memberBaseUrls,
        string artifactPath,
        CancellationToken ct)
    {
        var urls = memberBaseUrls.ToList();
        var tasks = urls.Select(baseUrl => FetchMetadataAsync(baseUrl.TrimEnd('/') + "/" + artifactPath.TrimStart('/'), ct));
        var results = await Task.WhenAll(tasks);

        var valid = results.Where(r => r is not null).ToList();
        if (valid.Count == 0)
            return null;

        if (valid.Count == 1)
            return SerializeMetadata(valid[0]!);

        return SerializeMetadata(Merge(valid!));
    }

    private async Task<MavenMetadata?> FetchMetadataAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Member at {Url} returned {StatusCode}; excluding from merge",
                    url, response.StatusCode);
                return null;
            }

            var xml = await response.Content.ReadAsStringAsync(ct);
            return ParseMetadata(xml, url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch metadata from {Url}; excluding from merge", url);
            return null;
        }
    }

    /// <summary>Parses a maven-metadata.xml string into a <see cref="MavenMetadata"/>.</summary>
    public static MavenMetadata? ParseMetadata(string xml, string sourceUrl = "")
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root is null) return null;

            var groupId    = root.Element("groupId")?.Value    ?? string.Empty;
            var artifactId = root.Element("artifactId")?.Value ?? string.Empty;
            var versioning = root.Element("versioning");

            var versions = versioning?
                .Element("versions")?
                .Elements("version")
                .Select(v => v.Value)
                .ToList() ?? [];

            var latest      = versioning?.Element("latest")?.Value;
            var release     = versioning?.Element("release")?.Value;
            var lastUpdated = versioning?.Element("lastUpdated")?.Value;

            return new MavenMetadata(groupId, artifactId, versions, latest, release, lastUpdated);
        }
        catch (Exception)
        {
            // Log at call site if needed; return null to exclude from merge.
            return null;
        }
    }

    /// <summary>
    /// Merges multiple metadata documents following the rules:
    /// 1. versions = union + dedup + semver sorted
    /// 2. latest = highest version
    /// 3. release = highest non-SNAPSHOT version
    /// 4. lastUpdated = maximum timestamp string
    /// </summary>
    public static MavenMetadata Merge(IReadOnlyList<MavenMetadata> all)
    {
        if (all.Count == 0)
            throw new ArgumentException("Cannot merge zero metadata documents.", nameof(all));

        var first = all[0];

        // Union all versions, deduplicate, sort by semantic version.
        var allVersions = all
            .SelectMany(m => m.Versions)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, VersionComparer.Instance)
            .ToList();

        // latest = highest version overall
        var latest = allVersions.Count > 0
            ? allVersions[^1]
            : null;

        // release = highest non-SNAPSHOT version
        var release = allVersions
            .Where(v => !v.EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase))
            .LastOrDefault();

        // lastUpdated = maximum timestamp string (they are yyyyMMddHHmmss formatted)
        var lastUpdated = all
            .Select(m => m.LastUpdated)
            .Where(ts => ts is not null)
            .Max();

        return new MavenMetadata(
            first.GroupId,
            first.ArtifactId,
            allVersions,
            latest,
            release,
            lastUpdated);
    }

    /// <summary>Serializes a <see cref="MavenMetadata"/> record to XML string.</summary>
    public static string SerializeMetadata(MavenMetadata metadata)
    {
        var versionsEl = new XElement("versions",
            metadata.Versions.Select(v => new XElement("version", v)));

        var versioningEl = new XElement("versioning", versionsEl);

        if (metadata.Latest is not null)
            versioningEl.Add(new XElement("latest", metadata.Latest));
        if (metadata.Release is not null)
            versioningEl.Add(new XElement("release", metadata.Release));
        if (metadata.LastUpdated is not null)
            versioningEl.Add(new XElement("lastUpdated", metadata.LastUpdated));

        var root = new XElement("metadata",
            new XElement("groupId",    metadata.GroupId),
            new XElement("artifactId", metadata.ArtifactId),
            versioningEl);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            root);

        return doc.ToString();
    }

    // ── Version comparer ──────────────────────────────────────────────────────

    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            if (NuGetVersion.TryParse(x, out var vx) && NuGetVersion.TryParse(y, out var vy))
                return vx.CompareTo(vy);

            // Fallback to lexicographic for non-semver version strings
            return string.Compare(x, y, StringComparison.Ordinal);
        }
    }
}

