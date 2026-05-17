using MavenOperator.ImportJob.Models;
using MavenOperator.ImportJob.Sinks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace MavenOperator.Tests.Unit.ImportJob.Sinks;

public sealed class DirectPvcSinkTests : IDisposable
{
    private readonly string _targetDir;
    private readonly DirectPvcSink _sut;

    public DirectPvcSinkTests()
    {
        _targetDir = Path.Combine(Path.GetTempPath(), "maven-import-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_targetDir);
        _sut = new DirectPvcSink(_targetDir, dryRun: false, NullLogger<DirectPvcSink>.Instance);
    }

    public void Dispose() => Directory.Delete(_targetDir, recursive: true);

    [Fact]
    public async Task WriteAsync_StreamContent_WritesFileToDisk()
    {
        var content  = new MemoryStream("hello-artifact"u8.ToArray());
        var artifact = new ArtifactDescriptor { RelativePath = "com/example/lib/1.0/lib.jar" };

        var bytes = await _sut.WriteAsync(artifact, content, CancellationToken.None);

        bytes.ShouldBeGreaterThan(0);
        var dest = Path.Combine(_targetDir, "com", "example", "lib", "1.0", "lib.jar");
        File.Exists(dest).ShouldBeTrue();
        File.ReadAllText(dest).ShouldBe("hello-artifact");
    }

    [Fact]
    public async Task WriteAsync_CreatesIntermediateDirectories()
    {
        var content  = new MemoryStream("data"u8.ToArray());
        var artifact = new ArtifactDescriptor { RelativePath = "a/b/c/d/e/file.jar" };

        await _sut.WriteAsync(artifact, content, CancellationToken.None);

        Directory.Exists(Path.Combine(_targetDir, "a", "b", "c", "d", "e")).ShouldBeTrue();
    }

    [Fact]
    public async Task WriteAsync_FilePath_CopiesFromDisk()
    {
        // Create a source file
        var srcFile = Path.Combine(_targetDir, "source.jar");
        File.WriteAllText(srcFile, "source-content");

        var artifact = new ArtifactDescriptor
        {
            RelativePath = "com/example/lib/1.0/lib.jar",
            FilePath     = srcFile,
        };

        var bytes = await _sut.WriteAsync(artifact, content: null, CancellationToken.None);

        bytes.ShouldBeGreaterThan(0);
        var dest = Path.Combine(_targetDir, "com", "example", "lib", "1.0", "lib.jar");
        File.ReadAllText(dest).ShouldBe("source-content");
    }

    [Fact]
    public async Task WriteAsync_DryRun_WritesNothing()
    {
        var dryRunSink = new DirectPvcSink(_targetDir, dryRun: true, NullLogger<DirectPvcSink>.Instance);
        var content    = new MemoryStream("data"u8.ToArray());
        var artifact   = new ArtifactDescriptor { RelativePath = "com/example/lib/1.0/lib.jar" };

        var bytes = await dryRunSink.WriteAsync(artifact, content, CancellationToken.None);

        bytes.ShouldBe(0);
        File.Exists(Path.Combine(_targetDir, "com", "example", "lib", "1.0", "lib.jar")).ShouldBeFalse();
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrue_WhenFileExists()
    {
        var dest = Path.Combine(_targetDir, "com", "example", "lib.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "existing");

        var artifact = new ArtifactDescriptor { RelativePath = "com/example/lib.jar" };

        (await _sut.ExistsAsync(artifact, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalse_WhenFileAbsent()
    {
        var artifact = new ArtifactDescriptor { RelativePath = "com/example/missing.jar" };
        (await _sut.ExistsAsync(artifact, CancellationToken.None)).ShouldBeFalse();
    }
}

