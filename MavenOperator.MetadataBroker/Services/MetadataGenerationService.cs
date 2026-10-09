using System.Text.RegularExpressions;
using System.Xml.Linq;
using NuGet.Versioning;

namespace MavenOperator.MetadataBroker.Services;

/// <summary>
/// Generates maven-metadata.xml content from directories on disk.
/// Handles both version-listing metadata and snapshot metadata.
/// </summary>
public sealed class MetadataGenerationService
{
    private static readonly HashSet<string> RecognizedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jar", ".war", ".ear", ".aar", ".pom",
    };

    // Snapshot file pattern: <name>-(<timestamp>)(.<ext>)
    // e.g. mylib-1.0-20261008.123456-1.jar
    private static readonly Regex SnapshotFilePattern = new(
        @"^(.*)-(\d{8}\.\d{6}-\d+)\.(\w+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Generates the appropriate maven-metadata.xml for the given artifact directory.
    /// Returns null if no version content exists (should return 404).
    /// </summary>
    public string? GenerateMetadataAsync(string artifactDir, string relativePath)
    {
        if (!Directory.Exists(artifactDir))
            return null;

        if (IsSnapshotPath(relativePath))
            return GenerateSnapshotMetadata(artifactDir, relativePath);

        return GenerateVersionListingMetadata(artifactDir, relativePath);
    }

    // ── Version listing metadata ────────────────────────────────────────────────
    // Path: <g>/<a>/maven-metadata.xml
    // Source: version subdirectories containing recognized artifacts.

    private string? GenerateVersionListingMetadata(string artifactDir, string relativePath)
    {
        var versionDirs = new List<(string VersionName, DateTime MaxTime)>();

        foreach (var subDir in Directory.EnumerateDirectories(artifactDir))
        {
            var versionName = Path.GetFileName(subDir);
            var hasArtifact = Directory.EnumerateFiles(subDir)
                .Any(f => HasRecognizedExtension(f));

            if (hasArtifact)
            {
                var entryMaxTime = GetMaxDirectoryTime(subDir);
                versionDirs.Add((versionName, entryMaxTime));
            }
        }

        if (versionDirs.Count == 0)
            return null;

        // Sort versions using semantic version comparison.
        versionDirs.Sort((a, b) => CompareVersions(a.VersionName, b.VersionName));

        var allVersions = versionDirs.Select(v => v.VersionName).ToList();
        var nonSnapshots = allVersions
            .Where(v => !v.EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var latest = allVersions.Count > 0 ? allVersions[^1] : null;
        var release = nonSnapshots.Count > 0 ? nonSnapshots[^1] : null;

        var dirMaxTime = versionDirs.Max(v => v.MaxTime);
        var lastUpdated = FormatTimestamp(dirMaxTime);

        var versionsEl = new XElement("versions",
            allVersions.Select(v => new XElement("version", v)));

        var versioningEl = new XElement("versioning", versionsEl);

        if (release is not null)
            versioningEl.Add(new XElement("release", release));
        if (latest is not null && latest != release)
            versioningEl.Add(new XElement("latest", latest));
        versioningEl.Add(new XElement("lastUpdated", lastUpdated));

        var root = new XElement("metadata",
            new XElement("groupId", ExtractGroup(relativePath)),
            new XElement("artifactId", ExtractArtifactId(relativePath)),
            versioningEl);

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            root).ToString();
    }

    // ── Snapshot metadata ────────────────────────────────────────────────────────
    // Path: <g>/<a>/<v>-SNAPSHOT/maven-metadata.xml
    // Source: timestamped files in the version directory.

    private string? GenerateSnapshotMetadata(string artifactDir, string relativePath)
    {
        var snapshotFiles = new List<(string DistName, string Extension, DateTime FileTime)>();

        foreach (var file in Directory.EnumerateFiles(artifactDir))
        {
            var name = Path.GetFileName(file);
            var match = SnapshotFilePattern.Match(name);
            if (!match.Success)
                continue;

            var extension = match.Groups[3].Value;
            if (!RecognizedExtensions.Contains("." + extension))
                continue;

            var fi = new FileInfo(file);
            var distName = match.Groups[1].Value
                + "-" + match.Groups[2].Value
                + "." + extension;

            snapshotFiles.Add((distName, extension, fi.LastWriteTimeUtc));
        }

        if (snapshotFiles.Count == 0)
            return null;

        // Sort by timestamp (most recent first).
        snapshotFiles.Sort((a, b) => b.FileTime.CompareTo(a.FileTime));

        var snapshotVersion = ExtractSnapshotVersion(relativePath);
        var fileMaxTime = snapshotFiles.Max(f => f.FileTime);
        var lastUpdated = FormatTimestamp(fileMaxTime);

        var snapshotVersions = new List<XElement>(snapshotFiles.Count);
        foreach (var (distName, extension, fileTime) in snapshotFiles)
        {
            snapshotVersions.Add(new XElement("snapshotVersion",
                new XElement("extension", extension),
                new XElement("value", distName),
                new XElement("updated", FormatTimestamp(fileTime))));
        }

        var versioningEl = new XElement("versioning",
            new XElement("snapshot",
                new XElement("localCopy", "false"),
                new XElement("value", snapshotVersion),
                new XElement("updated", lastUpdated)),
            new XElement("lastUpdated", lastUpdated),
            new XElement("snapshotVersions", snapshotVersions));

        var root = new XElement("metadata",
            new XElement("groupId", ExtractGroup(relativePath)),
            new XElement("artifactId", ExtractArtifactId(relativePath)),
            versioningEl);

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            root).ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string FormatTimestamp(DateTime timeUtc)
        => timeUtc.ToString("yyyyMMddHHmmss");

    private static string ExtractGroup(string relativePath)
    {
        var idx = relativePath.LastIndexOf("maven-metadata.xml", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return "";

        var baseName = relativePath[..idx].TrimEnd('/');
        var segments = baseName.Split('/');

        // Snapshot path: <g>/<a>/<v>-SNAPSHOT → group = <g>/<a>
        // Version listing: <g>/<a> → group = <g>
        if (segments[^1].EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase))
            return string.Join("/", segments[..^2]);

        return segments.Length >= 2 ? string.Join("/", segments[..^1]) : "";
    }

    private static string ExtractArtifactId(string relativePath)
    {
        var idx = relativePath.LastIndexOf("maven-metadata.xml", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return "";

        var baseName = relativePath[..idx].TrimEnd('/');
        var segments = baseName.Split('/');

        // For snapshot paths: <g>/<a>/<v>-SNAPSHOT → artifactId = <a> = segments[^2]
        // For version listing: <g>/<a> → artifactId = <a> = segments[^1]
        if (segments[^1].EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase))
        {
            // Snapshot: artifactId is the segment before the version
            return segments.Length >= 2 ? segments[^2] : "";
        }
        return segments.Length > 0 ? segments[^1] : "";
    }

    private static string ExtractSnapshotVersion(string relativePath)
    {
        var idx = relativePath.LastIndexOf("maven-metadata.xml", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return "";

        var baseName = relativePath[..idx].TrimEnd('/');
        var segments = baseName.Split('/');
        var lastSegment = segments[^1];
        return lastSegment;
    }

    private static bool HasRecognizedExtension(string filePath)
        => RecognizedExtensions.Contains(Path.GetExtension(filePath));

    private static bool IsSnapshotPath(string relativePath)
    {
        var idx = relativePath.LastIndexOf("maven-metadata.xml", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return false;

        var baseName = relativePath[..idx].TrimEnd('/');
        var lastSegment = Path.GetFileName(baseName);
        return lastSegment.EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime GetMaxDirectoryTime(string directory)
    {
        var maxTime = DateTime.MinValue;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(file);
                if (fi.LastWriteTimeUtc > maxTime)
                    maxTime = fi.LastWriteTimeUtc;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MetadataBroker] Failed to read directory {directory}: {ex.Message}");
        }
        return maxTime;
    }

    private static int CompareVersions(string a, string b)
    {
        if (NuGetVersion.TryParse(a, out var va) && NuGetVersion.TryParse(b, out var vb))
            return va.CompareTo(vb);

        return string.Compare(a, b, StringComparison.Ordinal);
    }
}
