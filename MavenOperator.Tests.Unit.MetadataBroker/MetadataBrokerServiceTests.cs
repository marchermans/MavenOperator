using System.Text;
using MavenOperator.MetadataBroker.Services;
using Shouldly;

namespace MavenOperator.Tests.Unit.MetadataBroker;

public class MetadataBrokerServiceTests : IDisposable
{
    private const string MetadataRel = "com/acme/lib/maven-metadata.xml";

    private readonly DirectoryInfo _root;
    private readonly MetadataBrokerService _svc;

    public MetadataBrokerServiceTests()
    {
        _root = Directory.CreateTempSubdirectory("broker-");
        _svc = new(_root.FullName, debounceMs: 50);
    }

    public void Dispose() => _root.Delete(recursive: true);

    // ── helpers ────────────────────────────────────────────────────────────────

    private static DateTimeOffset T(int hour) => new(2026, 10, 8, hour, 0, 0, TimeSpan.Zero);

    private void WriteFile(string rel, byte[] data, DateTimeOffset? mtime = null)
    {
        var path = Path.Combine(_root.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        if (mtime is { } t) File.SetLastWriteTimeUtc(path, t.UtcDateTime);
    }

    private void SeedVersion(string version, DateTimeOffset mtime, params string[] files)
    {
        foreach (var f in files)
            WriteFile($"com/acme/lib/{version}/{f}", new byte[] { 0x4a }, mtime);
    }

    private async Task WaitForAsync(Func<bool> cond, int timeoutMs = 15_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("write-back did not converge in time");
            await Task.Delay(25);
        }
    }

    // ── path safety & classification ───────────────────────────────────────────

    [Fact]
    public void TryResolveAcceptsValidPaths()
    {
        _svc.TryResolve(MetadataRel, out var full).ShouldBeTrue();
        Assert.StartsWith(_root.FullName, full);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../etc/passwd")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/../../x")]
    public void TryResolveRejectsTraversal(string rel)
        => _svc.TryResolve(rel, out _).ShouldBeFalse();

    [Fact]
    public void ClassifyDetectsListing()
        => _svc.Classify(MetadataRel).ShouldBe(MetadataKind.Listing);

    [Fact]
    public void ClassifyDetectsSnapshot()
        => _svc.Classify("com/acme/lib/1.0-SNAPSHOT/maven-metadata.xml").ShouldBe(MetadataKind.Snapshot);

    [Theory]
    [InlineData("a/b/maven-metadata.xml")]            // too shallow for a listing
    [InlineData("com/acme/lib/maven-metadata.xml.sha1")] // not the metadata file itself
    [InlineData("com/acme/lib/1.0/lib-1.0.jar")]      // plain artifact
    public void ClassifyPassthroughForEverythingElse(string rel)
        => _svc.Classify(rel).ShouldBe(MetadataKind.Passthrough);

    // ── reads ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadListingReturnsGeneratedXmlWithEtagAnd304RoundTrip()
    {
        SeedVersion("1.0", T(9), "lib-1.0.jar", "lib-1.0.pom");
        SeedVersion("2.0", T(12), "lib-2.0.jar");

        var r = await _svc.ReadAsync(MetadataRel, null);
        r.Status.ShouldBe(200);
        r.ContentType.ShouldBe("application/xml");
        r.ETag!.StartsWith("\"").ShouldBeTrue();
        var xml = Encoding.UTF8.GetString(r.Body!);
        // ascending: 1.0 appears before 2.0
        Assert.True(xml.Contains("<version>1.0</version>") && xml.IndexOf("<version>1.0</version>") < xml.IndexOf("<version>2.0</version>"));
        xml.IndexOf("1.0", StringComparison.Ordinal).ShouldBeLessThan(xml.IndexOf("2.0"));

        var notModified = await _svc.ReadAsync(MetadataRel, r.ETag);
        notModified.Status.ShouldBe(304);
        notModified.Body.ShouldBeNull();
    }

    [Fact]
    public async Task ReadListingPicksUpNewVersionDirWithoutExplicitInvalidation()
    {
        SeedVersion("1.0", T(9), "lib-1.0.jar");
        Assert.DoesNotContain("<version>2.0</version>", Encoding.UTF8.GetString((await _svc.ReadAsync(MetadataRel, null)).Body!));

        // A concurrent writer (dav/import job) adds a version — the next read must see it.
        SeedVersion("2.0", T(15), "lib-2.0.jar");
        var r = await _svc.ReadAsync(MetadataRel, null);
        Encoding.UTF8.GetString(r.Body!).ShouldContain("<version>2.0</version>");
    }

    [Fact]
    public async Task ReadListingWithNoVersionsReturns404()
    {
        // No artifact dir at all.
        (await _svc.ReadAsync(MetadataRel, null)).Status.ShouldBe(404);

        // And an artifact dir with no version subdirectories.
        var vdir = Path.Combine(_root.FullName, "com/acme/lib/1.0");
        Directory.CreateDirectory(vdir);
        Directory.Delete(vdir, recursive: true);   // lib/ exists but holds no versions
        (await _svc.ReadAsync(MetadataRel, null)).Status.ShouldBe(404);
    }

    [Fact]
    public async Task ReadSnapshotGeneratesPerExtensionBestBuild()
    {
        var rel = "com/acme/lib/1.0-SNAPSHOT/maven-metadata.xml";
        WriteFile("com/acme/lib/1.0-SNAPSHOT/lib-1.0-20261008.111111-5.jar", new byte[] { 1 }, T(9));
        WriteFile("com/acme/lib/1.0-SNAPSHOT/lib-1.0-20261008.123456-7.jar", new byte[] { 2 }, T(10));

        var r = await _svc.ReadAsync(rel, null);
        r.Status.ShouldBe(200);
        var xml = Encoding.UTF8.GetString(r.Body!);
        Assert.Contains("<value>lib-1.0-20261008.123456-7</value>", xml); // highest build wins
        Assert.DoesNotContain("111111-5", xml); // older builds are dropped
    }

    [Fact]
    public async Task ReadRejectsPathTraversal()
        => (await _svc.ReadAsync("../etc/passwd", null)).Status.ShouldBe(400);

    // ── passthrough ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PassthroughReadServesRawBytesWithEtagAnd304()
    {
        var bytes = Encoding.UTF8.GetBytes("da39a3ee566b"); // pretend checksum content
        WriteFile("com/acme/lib/1.0/lib-1.0.jar.sha1", bytes, T(9));

        var r = await _svc.ReadAsync("com/acme/lib/1.0/lib-1.0.jar.sha1", null);
        r.Status.ShouldBe(200);
        r.Body!.ShouldBe(bytes);
        r.Headers["X-Maven-Metadata-Source"].ShouldBe("passthrough");

        (await _svc.ReadAsync("com/acme/lib/1.0/lib-1.0.jar.sha1", r.ETag)).Status.ShouldBe(304);
    }

    [Fact]
    public async Task PassthroughReadMissingFileIs404()
        => (await _svc.ReadAsync("com/acme/lib/nope.txt", null)).Status.ShouldBe(404);

    [Fact]
    public async Task PassthroughPutWritesClientBytesVerbatim()
    {
        var bytes = Encoding.UTF8.GetBytes("5eb63bbbe01e"); // new checksum after re-upload
        using var stream = new MemoryStream(bytes, writable: true);

        var r = await _svc.WriteAckAsync("com/acme/lib/2.0/lib-2.0.jar.sha1", stream);
        r.Status.ShouldBe(201);

        File.ReadAllBytes(Path.Combine(_root.FullName, "com/acme/lib/2.0/lib-2.0.jar.sha1"))
            .ShouldBe(bytes);
    }

    [Fact]
    public async Task PassthroughDeleteRemovesFile()
    {
        var path = Path.Combine(_root.FullName, "com/acme/lib/3.0/x.txt");
        WriteFile("com/acme/lib/3.0/x.txt", new byte[] { 9 });

        (await _svc.DeleteAsync("com/acme/lib/3.0/x.txt")).Status.ShouldBe(204);
        File.Exists(path).ShouldBeFalse();
    }

    // ── ack-only writes: the actual race fix ───────────────────────────────────

    [Fact]
    public async Task PutDiscardsClientBytesAndDiskConvergesToRegeneratedXml()
    {
        SeedVersion("2.0", T(10), "lib-2.0.jar", "lib-2.0.pom");
        // A stale generated file from a previous generation is on disk already.
        WriteFile(MetadataRel, Encoding.UTF8.GetBytes("<stale/>"), T(1));

        var junk = Encoding.UTF8.GetBytes("<client-written-race-bytes/>");
        using var stream = new MemoryStream(junk, writable: true);

        var r = await _svc.WriteAckAsync(MetadataRel, stream);
        r.Status.ShouldBe(201);
        r.Headers["X-Maven-Metadata-Accepted"].ShouldBe("true");
        stream.Position.ShouldBe(junk.Length, "client body must be fully drained");

        // The client bytes must NEVER reach disk; the broker regenerates from artifacts.
        var libDir = new DirectoryInfo(Path.Combine(_root.FullName, "com/acme/lib"));
        var expected = MetadataXml.RenderListing("com.acme", "lib",
            RepositoryScanner.ScanListing(libDir).Select(v => (v.Name, v.MtimeUtc))).Body;

        await WaitForAsync(() =>
        {
            var path = Path.Combine(_root.FullName, MetadataRel);
            return File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(expected);
        });
    }

    [Fact]
    public async Task PutWhileNoVersionsExistRemovesStaleFile()
    {
        WriteFile(MetadataRel, Encoding.UTF8.GetBytes("<stale/>"), T(1));

        var r = await _svc.WriteAckAsync(MetadataRel, new MemoryStream(new byte[] { 0 }));
        r.Status.ShouldBe(201);

        var path = Path.Combine(_root.FullName, MetadataRel);
        await WaitForAsync(() => !File.Exists(path));
    }

    [Fact]
    public async Task DeleteIsAcknowledgedAndConvergesStaleFileAway()
    {
        SeedVersion("2.0", T(10), "lib-2.0.jar");
        var r = await _svc.ReadAsync(MetadataRel, null); // triggers write-back so the file exists
        var path = Path.Combine(_root.FullName, MetadataRel);
        await WaitForAsync(() => File.Exists(path));

        // Client deletes artifacts (dav) then DELETEs the metadata path.
        Directory.Delete(Path.Combine(_root.FullName, "com/acme/lib/2.0"), recursive: true);
        (await _svc.DeleteAsync(MetadataRel)).Status.ShouldBe(204);

        await WaitForAsync(() => !File.Exists(path));
    }

    [Fact]
    public async Task PutRejectsPathTraversalBeforeDraining()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2 }, writable: true);
        (await _svc.WriteAckAsync("../../etc/passwd", stream)).Status.ShouldBe(400);
    }

    // ── write-back atomicity helper is exercised implicitly above; direct check ─

    [Fact]
    public void AtomicWriteReplacesTargetAndLeavesNoTempFiles()
    {
        var dir = Directory.CreateTempSubdirectory("atomic-");
        try
        {
            var target = Path.Combine(dir.FullName, "maven-metadata.xml");
            MetadataBrokerService.AtomicWrite(target, new byte[] { 1 });
            File.ReadAllBytes(target).ShouldBe(new byte[] { 1 });

            MetadataBrokerService.AtomicWrite(target, new byte[] { 2, 3 }); // overwrite existing
            File.ReadAllBytes(target).ShouldBe(new byte[] { 2, 3 });
            dir.EnumerateFiles("*").Count(fi => fi.Name.StartsWith(".")).ShouldBe(0);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
