using MavenOperator.ImportJob.Models;
using MavenOperator.ImportJob.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace MavenOperator.Tests.Unit.ImportJob.Sources;

public sealed class PvcSnapshotSourceTests : IDisposable
{
    private readonly string _sourceDir;

    public PvcSnapshotSourceTests()
    {
        _sourceDir = Path.Combine(Path.GetTempPath(), "pvc-src-" + Guid.NewGuid());
        Directory.CreateDirectory(_sourceDir);
    }

    public void Dispose() => Directory.Delete(_sourceDir, recursive: true);

    private void SeedFile(string relativePath, string content = "data", bool repoPrefix = true)
    {
        var path = repoPrefix
            ? Path.Combine(_sourceDir, relativePath)
            : Path.Combine(_sourceDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task CrawlAsync_ReposiliteLayout_StripsRepoPrefix()
    {
        SeedFile("releases/com/example/lib/1.0/lib.jar");

        var source = new PvcSnapshotSource(
            _sourceDir, reposiliteLayout: true, repositoryName: "releases", appRootPvcMode: false,
            NullLogger<PvcSnapshotSource>.Instance);

        var artifacts = new List<ArtifactDescriptor>();
        await foreach (var a in source.CrawlAsync(new ImportFilters(), CancellationToken.None))
            artifacts.Add(a);

        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldBe("com/example/lib/1.0/lib.jar");
    }

    [Fact]
    public async Task CrawlAsync_RawMavenLayout_DoesNotStripPrefix()
    {
        SeedFile("com/example/lib/1.0/lib.jar");

        var source = new PvcSnapshotSource(
            _sourceDir, reposiliteLayout: false, repositoryName: null, appRootPvcMode: false,
            NullLogger<PvcSnapshotSource>.Instance);

        var artifacts = new List<ArtifactDescriptor>();
        await foreach (var a in source.CrawlAsync(new ImportFilters(), CancellationToken.None))
            artifacts.Add(a);

        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldBe("com/example/lib/1.0/lib.jar");
    }

    [Fact]
    public async Task CrawlAsync_SkipsIndexAndCacheDirectories()
    {
        SeedFile("releases/.index/catalog");
        SeedFile("releases/.cache/data");
        SeedFile("releases/com/example/lib/1.0/lib.jar");

        var source = new PvcSnapshotSource(
            _sourceDir, reposiliteLayout: true, repositoryName: "releases", appRootPvcMode: false,
            NullLogger<PvcSnapshotSource>.Instance);

        var artifacts = new List<ArtifactDescriptor>();
        await foreach (var a in source.CrawlAsync(new ImportFilters(), CancellationToken.None))
            artifacts.Add(a);

        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldBe("com/example/lib/1.0/lib.jar");
    }

    [Fact]
    public async Task CrawlAsync_SinceTimestamp_SkipsOlderFiles()
    {
        var oldFile = Path.Combine(_sourceDir, "releases", "com", "example", "old", "1.0", "old.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
        File.WriteAllText(oldFile, "old");
        File.SetLastWriteTime(oldFile, new DateTime(2020, 1, 1));

        SeedFile("releases/com/example/new/1.0/new.jar");

        var since  = new DateTime(2025, 1, 1).ToString("O");
        var source = new PvcSnapshotSource(
            _sourceDir, reposiliteLayout: true, repositoryName: "releases", appRootPvcMode: false,
            NullLogger<PvcSnapshotSource>.Instance);

        var artifacts = new List<ArtifactDescriptor>();
        await foreach (var a in source.CrawlAsync(new ImportFilters { SinceTimestamp = since }, CancellationToken.None))
            artifacts.Add(a);

        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldContain("new");
    }

    [Fact]
    public async Task CrawlAsync_GroupFilter_OnlyIncludesMatchingGroups()
    {
        SeedFile("releases/com/example/lib/1.0/lib.jar");
        SeedFile("releases/org/junit/junit/4.13/junit.jar");

        var source = new PvcSnapshotSource(
            _sourceDir, reposiliteLayout: true, repositoryName: "releases", appRootPvcMode: false,
            NullLogger<PvcSnapshotSource>.Instance);

        var filters   = new ImportFilters { IncludeGroups = ["com.example"] };
        var artifacts = new List<ArtifactDescriptor>();
        await foreach (var a in source.CrawlAsync(filters, CancellationToken.None))
            artifacts.Add(a);

        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldStartWith("com/example");
    }

    [Fact]
    public async Task CrawlAsync_SetsFilePath_ForPvcArtifacts()
    {
        SeedFile("releases/com/example/lib/1.0/lib.jar");

        var source = new PvcSnapshotSource(
            _sourceDir, reposiliteLayout: true, repositoryName: "releases", appRootPvcMode: false,
            NullLogger<PvcSnapshotSource>.Instance);

        var artifacts = new List<ArtifactDescriptor>();
        await foreach (var a in source.CrawlAsync(new ImportFilters(), CancellationToken.None))
            artifacts.Add(a);

        artifacts[0].FilePath.ShouldNotBeNull();
        File.Exists(artifacts[0].FilePath).ShouldBeTrue();
    }

    [Fact]
    public async Task CrawlAsync_AppRootPvcMode_OnlyCrawlsTargetRepository()
    {
        // Simulate approot layout with multiple repositories.
        // Only artifacts under "app/repositories/libs-release/" should be crawled.
        SeedFile("repositories/libs-release/com/example/lib/1.0/lib.jar", repoPrefix: false);
        SeedFile("repositories/libs-snapshot/org/snap/artifact/2.0/artifact.jar", repoPrefix: false); // different repo — should NOT be included
        SeedFile("config/settings.xml", repoPrefix: false); // approot file — should NOT be included

        var source = new PvcSnapshotSource(
            _sourceDir, reposiliteLayout: true, repositoryName: "libs-release", appRootPvcMode: true,
            NullLogger<PvcSnapshotSource>.Instance);

        var artifacts = new List<ArtifactDescriptor>();
        await foreach (var a in source.CrawlAsync(new ImportFilters(), CancellationToken.None))
            artifacts.Add(a);

        // Only the libs-release artifact should be found.
        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldBe("com/example/lib/1.0/lib.jar");
    }

    [Fact]
    public async Task CrawlAsync_AppRootPvcMode_WithDifferentApprootName()
    {
        // Simulate approot layout with "data" as the approot directory name.
        SeedFile("repositories/releases/com/example/lib/1.0/lib.jar", repoPrefix: false);

        var source = new PvcSnapshotSource(
            _sourceDir, reposiliteLayout: true, repositoryName: "releases", appRootPvcMode: true,
            NullLogger<PvcSnapshotSource>.Instance);

        var artifacts = new List<ArtifactDescriptor>();
        await foreach (var a in source.CrawlAsync(new ImportFilters(), CancellationToken.None))
            artifacts.Add(a);

        artifacts.ShouldHaveSingleItem();
        artifacts[0].RelativePath.ShouldBe("com/example/lib/1.0/lib.jar");
    }

    [Fact]
    public void Constructor_AppRootPvcMode_WithoutRepositoryName_Throws()
    {
        Should.Throw<ArgumentException>(() => new PvcSnapshotSource(
            _sourceDir, reposiliteLayout: true, repositoryName: null!, appRootPvcMode: true,
            NullLogger<PvcSnapshotSource>.Instance));
    }

    [Fact]
    public void Constructor_AppRootPvcMode_RepositoryNotFound_Throws()
    {
        // No "repositories/libs-release" exists under any top-level directory.
        SeedFile("app/config/settings.xml", repoPrefix: false);

        Should.Throw<DirectoryNotFoundException>(() => new PvcSnapshotSource(
            _sourceDir, reposiliteLayout: true, repositoryName: "libs-release", appRootPvcMode: true,
            NullLogger<PvcSnapshotSource>.Instance));
    }
}

