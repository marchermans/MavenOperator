using MavenOperator.MetadataBroker.Services;
using Shouldly;

namespace MavenOperator.Tests.Unit.MetadataBroker;

public class RepositoryScannerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("repo-scan-");

    public void Dispose() => _root.Delete(recursive: true);

    // ── helpers ────────────────────────────────────────────────────────────────

    private static string WriteAt(DirectoryInfo dir, string name, byte[]? content = null, DateTimeOffset? mtime = null)
    {
        var path = Path.Combine(dir.FullName, name);
        File.WriteAllBytes(path, content ?? new byte[] { 1 });
        if (mtime is { } t) File.SetLastWriteTimeUtc(path, t.UtcDateTime);
        return path;
    }

    private static DirectoryInfo DirAt(DirectoryInfo root, params string[] parts)
    {
        var d = Directory.CreateDirectory(Path.Combine(root.FullName, Path.Combine(parts)));
        return d;
    }

    private static DateTimeOffset T(int hour) => new(2026, 10, 8, hour, 0, 0, TimeSpan.Zero);

    // ── ScanListing ────────────────────────────────────────────────────────────

    [Fact]
    public void ScanListingCountsOnlyDirsWithRecognizedAssets()
    {
        var artifactDir = DirAt(_root, "com", "acme", "lib");

        WriteAt(DirAt(artifactDir, "1.0"), "lib-1.0.jar", mtime: T(9));
        WriteAt(DirAt(artifactDir, "2.0"), "lib-2.0.pom", mtime: T(10));
        DirAt(artifactDir, ".hidden");                                  // hidden dir → ignored
        WriteAt(DirAt(artifactDir, "docs"), "readme.txt", mtime: T(11));  // no recognized asset

        // Scan order follows directory enumeration — compare as a set.
        RepositoryScanner.ScanListing(artifactDir)
            .Select(v => v.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["1.0", "2.0"]);
    }

    [Fact]
    public void ScanListingExcludesMetadataAndTempFilesFromMtime()
    {
        var artifactDir = DirAt(_root, "com", "acme", "lib");
        var v = DirAt(artifactDir, "1.0");

        WriteAt(v, "lib-1.0.jar", mtime: T(9));
        // A stale generated metadata file with a NEWER mtime must not skew lastUpdated.
        WriteAt(v, "maven-metadata.xml", mtime: T(23));
        File.Copy(Path.Combine(v.FullName, "lib-1.0.jar"), Path.Combine(v.FullName, ".tmp-1")); // temp → ignored

        var (name, mtime) = RepositoryScanner.ScanListing(artifactDir).Single();

        name.ShouldBe("1.0");
        mtime.UtcDateTime.ShouldBe(T(9).UtcDateTime);
    }

    [Fact]
    public void ScanListingUsesMaxDirectFileMtime()
    {
        var v = DirAt(DirAt(_root, "com", "acme", "lib"), "1.0");
        WriteAt(v, "lib-1.0.jar", mtime: T(9));
        WriteAt(v, "lib-1.0.pom", mtime: T(12));

        RepositoryScanner.ScanListing(DirAt(_root, "com", "acme", "lib"))[0].MtimeUtc.UtcDateTime
            .ShouldBe(T(12).UtcDateTime);
    }

    [Fact]
    public void ScanListingIgnoresNestedSubdirs()
    {
        var v = DirAt(DirAt(_root, "com", "acme", "lib"), "1.0");
        WriteAt(DirAt(v, "nested"), "inner.jar", mtime: T(9)); // only nested → no direct recognized file

        RepositoryScanner.ScanListing(DirAt(_root, "com", "acme", "lib")).ShouldBeEmpty();
    }

    // ── ScanSnapshot ───────────────────────────────────────────────────────────

    [Fact]
    public void ScanSnapshotMatchesTimestampedArtifactsAndIgnoresChecksums()
    {
        var v = DirAt(DirAt(_root, "com", "acme", "lib"), "1.0-SNAPSHOT");
        WriteAt(v, "lib-1.0-20261008.111111-5.jar", mtime: T(9));
        WriteAt(v, "lib-1.0-20261008.123456-7.jar", mtime: T(10));
        File.Copy(Path.Combine(v.FullName, "lib-1.0-20261008.123456-7.jar"),
                  Path.Combine(v.FullName, "lib-1.0-20261008.123456-7.jar.sha1")); // checksum → ignored
        WriteAt(v, "lib-1.0.pom", mtime: T(11)); // no timestamp → not a snapshot artifact

        RepositoryScanner.ScanSnapshot(v)
            .Select(f => (f.Extension, f.Value))
            .Single().ShouldBe(("jar", "lib-1.0-20261008.123456-7"));
    }

    [Fact]
    public void ScanSnapshotKeepsMultipleExtensions()
    {
        var v = DirAt(DirAt(_root, "com", "acme", "lib"), "1.0-SNAPSHOT");
        WriteAt(v, "lib-1.0-20261008.111111-5.jar", mtime: T(9));
        WriteAt(v, "lib-1.0-20261008.111111-5.pom", mtime: T(9));

        // Enumeration order is undefined — sort for a stable assertion.
        RepositoryScanner.ScanSnapshot(v)
            .Select(f => f.Extension).OrderBy(e => e, StringComparer.Ordinal)
            .ShouldBe(["jar", "pom"]);
    }

    // ── fingerprints ───────────────────────────────────────────────────────────

    [Fact]
    public void FingerprintIsStableAndOrderInsensitive()
        => RepositoryScanner.Fingerprint(new[] { ("1.0", T(9)), ("2.0", T(10)) })
            .ShouldBe(RepositoryScanner.Fingerprint(new[] { ("2.0", T(10)), ("1.0", T(9)) }));

    [Fact]
    public void FingerprintChangesWhenAnyEntryChanges()
    {
        var base_ = new[] { ("1.0", T(9)), ("2.0", T(10)) };
        RepositoryScanner.Fingerprint(base_)
            .ShouldNotBe(RepositoryScanner.Fingerprint(new[] { ("1.0", T(9)), ("3.0", T(10)) }));
        RepositoryScanner.Fingerprint(base_)
            .ShouldNotBe(RepositoryScanner.Fingerprint(new[] { ("1.0", T(9)), ("2.0", T(11)) }));
    }

    [Fact]
    public void FingerprintSnapshotChangesWhenMtimeOrNameChanges()
    {
        var a = new SnapshotFileEntry("jar", "lib-1.0-20261008.123456-7", T(9));
        RepositoryScanner.FingerprintSnapshot(new[] { a })
            .ShouldNotBe(RepositoryScanner.FingerprintSnapshot(new[] { new SnapshotFileEntry("jar", "lib-1.0-20261008.123456-7", T(10)) }));
        RepositoryScanner.FingerprintSnapshot(new[] { a })
            .ShouldNotBe(RepositoryScanner.FingerprintSnapshot(new[] { new SnapshotFileEntry("jar", "lib-1.0-20261008.999999-7", T(9)) }));
    }
}
