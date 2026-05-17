using MavenOperator.ImportJob.Models;
using MavenOperator.ImportJob.Services;
using MavenOperator.ImportJob.Sinks;
using MavenOperator.ImportJob.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace MavenOperator.Tests.Unit.ImportJob.Services;

public sealed class ArtifactCrawlerTests : IDisposable
{
    private readonly string _sourceDir;
    private readonly string _targetDir;
    private readonly ArtifactCrawler _sut;

    public ArtifactCrawlerTests()
    {
        _sourceDir = Path.Combine(Path.GetTempPath(), "crawler-src-" + Guid.NewGuid());
        _targetDir = Path.Combine(Path.GetTempPath(), "crawler-dst-" + Guid.NewGuid());
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_targetDir);
        _sut = new ArtifactCrawler(NullLogger<ArtifactCrawler>.Instance);
    }

    public void Dispose()
    {
        Directory.Delete(_sourceDir, recursive: true);
        Directory.Delete(_targetDir, recursive: true);
    }

    private void SeedArtifact(string relativePath)
    {
        var dest = Path.Combine(_sourceDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, $"content-of-{Path.GetFileName(relativePath)}");
    }

    [Fact]
    public async Task RunAsync_DryRun_DiscoversThenWritesNothing()
    {
        SeedArtifact("com/example/lib/1.0/lib.jar");
        SeedArtifact("com/example/lib/1.0/lib.pom");

        var source  = new PvcSnapshotSource(_sourceDir, false, null, NullLogger<PvcSnapshotSource>.Instance);
        var sink    = new DirectPvcSink(_targetDir, dryRun: false, NullLogger<DirectPvcSink>.Instance);
        var options = new ImportOptions { DryRun = true };

        var result = await _sut.RunAsync(source, sink, null, options, new ImportFilters(), null, CancellationToken.None);

        result.ArtifactsDiscovered.ShouldBe(2);
        result.ArtifactsCopied.ShouldBe(0);
        Directory.GetFiles(_targetDir, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_CopiesAllArtifacts()
    {
        SeedArtifact("com/example/lib/1.0/lib.jar");
        SeedArtifact("com/example/lib/1.0/lib.pom");
        SeedArtifact("com/example/lib/1.0/lib.jar.sha1");

        var source  = new PvcSnapshotSource(_sourceDir, false, null, NullLogger<PvcSnapshotSource>.Instance);
        var sink    = new DirectPvcSink(_targetDir, dryRun: false, NullLogger<DirectPvcSink>.Instance);
        var options = new ImportOptions { Parallelism = 2 };

        var result = await _sut.RunAsync(source, sink, null, options, new ImportFilters(), null, CancellationToken.None);

        result.ArtifactsDiscovered.ShouldBe(3);
        result.ArtifactsCopied.ShouldBe(3);
        result.ArtifactsFailed.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_OverwriteExistingFalse_SkipsExistingFiles()
    {
        SeedArtifact("com/example/lib/1.0/lib.jar");

        // Pre-seed the target
        var destPath = Path.Combine(_targetDir, "com", "example", "lib", "1.0", "lib.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.WriteAllText(destPath, "original-content");

        var source  = new PvcSnapshotSource(_sourceDir, false, null, NullLogger<PvcSnapshotSource>.Instance);
        var sink    = new DirectPvcSink(_targetDir, dryRun: false, NullLogger<DirectPvcSink>.Instance);
        var options = new ImportOptions { OverwriteExisting = false };

        var result = await _sut.RunAsync(source, sink, null, options, new ImportFilters(), null, CancellationToken.None);

        result.ArtifactsDiscovered.ShouldBe(1);
        result.ArtifactsCopied.ShouldBe(0); // skipped

        // Original content should be preserved
        File.ReadAllText(destPath).ShouldBe("original-content");
    }

    [Fact]
    public async Task RunAsync_ErrorOnOneArtifact_DoesNotAbortOthers()
    {
        SeedArtifact("com/example/lib/1.0/lib.jar");
        SeedArtifact("com/example/lib/1.0/lib.pom");

        var source      = new PvcSnapshotSource(_sourceDir, false, null, NullLogger<PvcSnapshotSource>.Instance);
        var failingSink = new FaultingOnFirstWriteSink(_targetDir);
        var options     = new ImportOptions { Parallelism = 1, OverwriteExisting = true };

        var result = await _sut.RunAsync(source, failingSink, null, options, new ImportFilters(), null, CancellationToken.None);

        // 2 discovered, 1 failed, 1 copied
        result.ArtifactsDiscovered.ShouldBe(2);
        result.ArtifactsFailed.ShouldBe(1);
        result.ArtifactsCopied.ShouldBe(1);
        result.FailedArtifacts.ShouldHaveSingleItem();
    }
}

/// <summary>A sink that fails on the very first WriteAsync call.</summary>
file sealed class FaultingOnFirstWriteSink : IRepositorySink
{
    private int _callCount;
    private readonly string _targetDir;

    public FaultingOnFirstWriteSink(string targetDir) => _targetDir = targetDir;

    public async Task<long> WriteAsync(ArtifactDescriptor artifact, Stream? content, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _callCount) == 1)
            throw new IOException("Simulated write failure");

        var dest = Path.Combine(_targetDir, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (content is not null) await content.CopyToAsync(File.Create(dest), ct);
        return content?.Length ?? 0;
    }

    public Task<bool> ExistsAsync(ArtifactDescriptor artifact, CancellationToken ct) =>
        Task.FromResult(false);
}


