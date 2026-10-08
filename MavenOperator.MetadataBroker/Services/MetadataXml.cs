using System.Text;
using System.Xml.Linq;

namespace MavenOperator.MetadataBroker.Services;

/// <summary>A parsed directory (version) name with its sortable components.</summary>
public sealed record VersionOrderKey(int Major, int Minor, int Patch, bool HasLabel, string Label);

/// <summary>Parses and orders Maven version directory names.</summary>
public static class VersionOrder
{
    /// <summary>
    /// Parses a Maven version directory name (e.g. "2.0", "1.3-SNAPSHOT", "4.1-RC1",
    /// "20260108"). The qualifier is everything after the first '-'; numeric tuple
    /// components missing from the base parse as zero.
    /// </summary>
    public static VersionOrderKey Parse(string name)
    {
        var dash = name.IndexOf('-');
        var label = dash >= 0 ? name[(dash + 1)..] : "";
        var parts = (dash >= 0 ? name[..dash] : name).Split('.');

        return new VersionOrderKey(
            ParseInt(parts, 0), ParseInt(parts, 1), ParseInt(parts, 2),
            label.Length > 0, label);
    }

    private static int ParseInt(string[] p, int i) =>
        i < p.Length && int.TryParse(p[i], out var v) ? v : 0;

    /// <summary>
    /// Ascending Maven-ish ordering: numeric tuple first; for equal tuples a labelled
    /// version (SNAPSHOT/RC…) sorts before the plain release.
    /// </summary>
    public static int Compare(string left, string right)
    {
        var l = Parse(left); var r = Parse(right);
        foreach (var c in new[]
                 {
                     Comparer<int>.Default.Compare(l.Major, r.Major),
                     Comparer<int>.Default.Compare(l.Minor, r.Minor),
                     Comparer<int>.Default.Compare(l.Patch, r.Patch),
                     // Unlabelled (release) sorts AFTER labelled with the same numeric value.
                     Comparer<bool>.Default.Compare(!l.HasLabel, !r.HasLabel),
                 })
        {
            if (c != 0) return c;
        }
        return string.CompareOrdinal(l.Label, r.Label);
    }

    /// <summary>Sorts version names ascending and returns a new list.</summary>
    public static List<string> SortAscending(IEnumerable<string> versions)
    {
        var copy = versions.ToList();
        copy.Sort(Compare);
        return copy;
    }
}

/// <summary>One recognized artifact file inside a snapshot version directory.</summary>
public sealed record SnapshotFileEntry(string Extension, string Value, DateTimeOffset MtimeUtc);

/// <summary>
/// Pure maven-metadata.xml rendering — no filesystem access, fully unit-testable.
/// </summary>
public static class MetadataXml
{
    /// <summary>Renders the version-listing metadata for one artifact.</summary>
    public static (byte[] Body, DateTimeOffset LastUpdatedUtc) RenderListing(
        string groupId, string artifactId, IEnumerable<(string Name, DateTimeOffset MtimeUtc)> versions)
    {
        var ordered = VersionOrder.SortAscending(versions.Select(v => v.Name));

        var latest = ordered[^1];
        // <release> = highest version without a qualifier (e.g. no -SNAPSHOT/-RC). Omitted if none.
        string? release = null;
        foreach (var name in ordered.AsEnumerable().Reverse())
            if (VersionOrder.Parse(name).HasLabel == false) { release = name; break; }

        var lastUpdated = versions.Max(v => v.MtimeUtc);

        var versioning = new XElement("versioning",
            new XElement("latest", latest),
            release is not null ? new XElement("release", release) : null,
            new XElement("versions", ordered.Select(v => new XElement("version", v))),
            new XElement("lastUpdated", FormatLastUpdated(lastUpdated)));

        var root = BuildRoot(groupId, artifactId, versioning: versioning);
        return (Serialize(root), lastUpdated);
    }

    /// <summary>Renders the snapshot metadata for one -SNAPSHOT version directory.</summary>
    public static (byte[] Body, DateTimeOffset LastUpdatedUtc) RenderSnapshot(
        string groupId, string artifactId, string versionDirName, IReadOnlyList<SnapshotFileEntry> files)
    {
        // One entry per extension — the highest build number wins.
        var best = new Dictionary<string, SnapshotFileEntry>();
        foreach (var f in files)
            if (!best.TryGetValue(f.Extension, out var cur) || Beats(f, cur))
                best[f.Extension] = f;

        var lastUpdated = files.Max(f => f.MtimeUtc);

        var entries = best.Values
            .OrderBy(f => f.Extension, StringComparer.Ordinal)
            .Select(f => new XElement("snapshotVersion",
                new XElement("extension", f.Extension),
                new XElement("value", f.Value),
                new XElement("updated", FormatLastUpdated(f.MtimeUtc))))
            .ToArray();

        var snapshot = new XElement("snapshot",
            new XElement("localCopy", "false"),
            new XElement("snapshotVersions", entries),
            new XElement("lastUpdated", FormatLastUpdated(lastUpdated)));

        var root = BuildRoot(groupId, artifactId, version: new XElement("version", versionDirName), snapshotEl: snapshot);
        return (Serialize(root), lastUpdated);
    }

    // ── internals ─────────────────────────────────────────────────────────────

    private static XElement BuildRoot(string groupId, string artifactId,
        XElement? version = null, XElement? versioning = null, XElement? snapshotEl = null) =>
        new("metadata", new XAttribute("modelVersion", "1.1.0"),
            new XElement("groupId", groupId),
            new XElement("artifactId", artifactId),
            version,
            versioning,
            snapshotEl);

    /// <summary>True when the candidate beats the current best for its extension.</summary>
    private static bool Beats(SnapshotFileEntry candidate, SnapshotFileEntry current)
    {
        var c = ExtractBuildNumber(candidate.Value);
        var cur = ExtractBuildNumber(current.Value);
        return c > cur || (c == cur && string.CompareOrdinal(candidate.Value, current.Value) > 0);
    }

    /// <summary>Extracts the trailing "-N" build number from a snapshot value.</summary>
    public static int ExtractBuildNumber(string value)
    {
        var dash = value.LastIndexOf('-');
        return dash >= 0 && int.TryParse(value[(dash + 1)..], out var n) ? n : -1;
    }

    private static string FormatLastUpdated(DateTimeOffset utc) =>
        utc.UtcDateTime.ToString("yyyyMMddHHmmss");

    private static byte[] Serialize(XElement root)
    {
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var ms = new MemoryStream();
        using var sw = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        doc.Save(sw);
        sw.Flush();
        return ms.ToArray();
    }
}
