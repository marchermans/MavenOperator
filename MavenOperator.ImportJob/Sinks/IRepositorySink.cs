using MavenOperator.ImportJob.Models;

namespace MavenOperator.ImportJob.Sinks;

/// <summary>
/// Writes a single artifact to a destination.
/// </summary>
public interface IRepositorySink
{
    /// <summary>
    /// Writes the artifact stream to the destination.
    /// </summary>
    /// <param name="artifact">Artifact descriptor (path, size, etc.).</param>
    /// <param name="content">Artifact byte stream (may be null for dry-run).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Bytes written (0 for dry-run or skip).</returns>
    Task<long> WriteAsync(ArtifactDescriptor artifact, Stream? content, CancellationToken ct);

    /// <summary>
    /// Returns true if the artifact already exists at the destination.
    /// Used when overwriteExisting=false.
    /// </summary>
    Task<bool> ExistsAsync(ArtifactDescriptor artifact, CancellationToken ct);
}

