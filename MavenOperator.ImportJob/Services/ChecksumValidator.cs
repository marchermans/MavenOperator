using System.Security.Cryptography;
using MavenOperator.ImportJob.Models;

namespace MavenOperator.ImportJob.Services;

/// <summary>
/// Validates artifact integrity after writing to the sink.
/// Computes SHA-256 of the written file and compares against the known hash.
/// </summary>
public sealed class ChecksumValidator
{
    private readonly ILogger<ChecksumValidator> _logger;

    public ChecksumValidator(ILogger<ChecksumValidator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates the SHA-256 of the file at <paramref name="filePath"/>.
    /// Returns true when validation passes or when no expected hash is available.
    /// Returns false when hashes do not match.
    /// </summary>
    public async Task<bool> ValidateAsync(string filePath, string? expectedSha256, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedSha256))
            return true; // Nothing to compare — pass

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Checksum validation: file not found: {FilePath}", filePath);
            return false;
        }

        var actual = await ComputeSha256Async(filePath, ct);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Checksum MISMATCH for {FilePath}: expected={Expected} actual={Actual}",
                filePath, expectedSha256, actual);
            return false;
        }

        _logger.LogDebug("Checksum OK for {FilePath}", filePath);
        return true;
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash);
    }
}

