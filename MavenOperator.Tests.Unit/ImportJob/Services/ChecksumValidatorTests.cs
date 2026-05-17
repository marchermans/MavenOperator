using MavenOperator.ImportJob.Models;
using MavenOperator.ImportJob.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace MavenOperator.Tests.Unit.ImportJob.Services;

public sealed class ChecksumValidatorTests : IDisposable
{
    private readonly string _tempDir;

    public ChecksumValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "checksum-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ValidateAsync_NoExpectedHash_ReturnsTrue()
    {
        var path = WriteFile("lib.jar", "data");
        var sut  = new ChecksumValidator(NullLogger<ChecksumValidator>.Instance);

        (await sut.ValidateAsync(path, null, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAsync_CorrectHash_ReturnsTrue()
    {
        const string content = "hello-artifact";
        var path    = WriteFile("lib.jar", content);

        // Compute expected SHA-256
        var expected = await ChecksumValidator.ComputeSha256Async(path, CancellationToken.None);
        var sut = new ChecksumValidator(NullLogger<ChecksumValidator>.Instance);

        (await sut.ValidateAsync(path, expected, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAsync_WrongHash_ReturnsFalse()
    {
        var path = WriteFile("lib.jar", "hello-artifact");
        var sut  = new ChecksumValidator(NullLogger<ChecksumValidator>.Instance);

        (await sut.ValidateAsync(path, "0000000000000000000000000000000000000000000000000000000000000000",
            CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateAsync_MissingFile_ReturnsFalse()
    {
        var sut = new ChecksumValidator(NullLogger<ChecksumValidator>.Instance);
        (await sut.ValidateAsync("/tmp/nonexistent-file.jar",
            "abc123", CancellationToken.None)).ShouldBeFalse();
    }
}

