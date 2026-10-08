using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MavenOperator.MetadataBroker.Services;

/// <summary>A version directory discovered under an artifact directory.</summary>
public sealed record ScannedVersion(string Name, DateTimeOffset MtimeUtc);

/// <summary>
/// Reads the repository on disk. The disk is the single source of truth — this type
/// never writes. Generation and write-back live in MetadataBrokerService.
/// </summary>
public static class RepositoryScanner
{
    /// <summary>Asset extensions that make a version directory "real".</summary>
    public static readonly string[] RecognizedExtensions = ["jar", "pom", "war", "ear", "aar"];

    private const string MetadataFileName = "maven-metadata.xml";
    private static readonly Regex SnapshotFileRegex =
        new(@"^(?<base>.+)-(?<ts>\d{8}\.\d{6}-\d+)\.(?<ext>jar|pom|war|ear|aar)$", RegexOptions.Compiled);

    /// <summary>
    /// Scans an artifact directory (&lt;group.../artifactId&gt;) for version sub-directories.
    /// A sub-directory counts as a version if it directly contains at least one recognized
    /// asset file. Mtime = newest direct file mtime (the maven-metadata.xml itself and our
    /// temp files are excluded so regeneration is stable).
    /// </summary>
    public static IReadOnlyList<ScannedVersion> ScanListing(DirectoryInfo artifactDir)
    {
        var result = new List<ScannedVersion>();

        DirectoryInfo[] subdirs;
        try { subdirs = artifactDir.GetDirectories(); }
        catch (IOException) { return result; }
        catch (UnauthorizedAccessException) { return result; }

        foreach (var dir in subdirs)
        {
            if (dir.Name.StartsWith('.')) continue; // hidden / temp dirs

            var files = SafeGetFiles(dir);
            if (files is null || files.Length == 0) continue;

            var assets = files.Where(f =>
                !f.Name.StartsWith(MetadataFileName, StringComparison.Ordinal) &&
                RecognizedExtensions.Contains(ExtOf(f.Name)));
            if (!assets.Any()) continue;

            var mtime = assets.Max(f => f.LastWriteTimeUtc);
            result.Add(new ScannedVersion(dir.Name, mtime));
        }

        return result;
    }

    /// <summary>
    /// Scans a &lt;-SNAPSHOT&gt; version directory for timestamped artifact files
    /// (name pattern: anything-&lt;yyyyMMdd.HHmmss-buildNum&gt;.ext). Checksums and other
    /// files are ignored — they never appear in the metadata. Returns exactly one entry
    /// per extension: the highest build (timestamp) found on disk.
    /// </summary>
    public static IReadOnlyList<SnapshotFileEntry> ScanSnapshot(DirectoryInfo versionDir)
    {
        var best = new Dictionary<string, (string Ts, SnapshotFileEntry Entry)>();

        FileInfo[] files;
        try { files = versionDir.GetFiles(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }

        foreach (var f in files.Where(f => !f.Name.StartsWith('.')))
        {
            var m = SnapshotFileRegex.Match(f.Name);
            if (!m.Success) continue;

            // Fixed-width timestamp → ordinal comparison selects the newest build.
            var ts = m.Groups["ts"].Value;
            var entry = new SnapshotFileEntry(m.Groups["ext"].Value, $"{m.Groups["base"].Value}-{ts}", f.LastWriteTimeUtc);
            if (!best.TryGetValue(entry.Extension, out var cur) || string.CompareOrdinal(ts, cur.Ts) > 0)
                best[entry.Extension] = (ts, entry);
        }

        return best.Values.Select(v => v.Entry).OrderBy(e => e.Extension, StringComparer.Ordinal).ToList();
    }

    /// <summary>Stable fingerprint of the scanned data — any content change flips it.</summary>
    public static string Fingerprint(IEnumerable<(string Name, DateTimeOffset MtimeUtc)> entries) =>
        Hash(entries.Select(e => $"{e.Name}:{e.MtimeUtc.Ticks}").OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>Stable fingerprint of snapshot files (name + mtime).</summary>
    public static string FingerprintSnapshot(IEnumerable<SnapshotFileEntry> entries) =>
        Hash(entries.Select(e => $"{e.Value}:{e.Extension}:{e.MtimeUtc.Ticks}").OrderBy(s => s, StringComparer.Ordinal));

    // ── internals ─────────────────────────────────────────────────────────────

    private static FileInfo[]? SafeGetFiles(DirectoryInfo dir)
    {
        try { return dir.GetFiles(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string ExtOf(string fileName) =>
        Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();

    private static string Hash(IEnumerable<string> parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u0001", parts)));
        return Convert.ToHexStringLower(bytes);
    }
}
