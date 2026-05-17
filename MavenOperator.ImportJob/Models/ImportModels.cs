namespace MavenOperator.ImportJob.Models;

/// <summary>
/// Describes a single artifact (JAR, POM, checksum, etc.) to be migrated.
/// </summary>
public sealed class ArtifactDescriptor
{
    /// <summary>Maven-layout relative path, e.g. com/example/my-lib/1.0/my-lib-1.0.jar</summary>
    public required string RelativePath { get; init; }

    /// <summary>Optional: full local filesystem path (PVC modes — no HTTP download needed).</summary>
    public string? FilePath { get; init; }

    /// <summary>File size in bytes, if known.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>SHA-256 hash of the artifact, if known from the source listing.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Last-modified time reported by the source.</summary>
    public DateTimeOffset? LastModified { get; init; }
}

/// <summary>
/// Tracks the outcome of a single artifact transfer.
/// </summary>
public sealed class ArtifactResult
{
    public required string RelativePath { get; init; }
    public bool Success { get; init; }
    public string? FailureReason { get; init; }
    public long BytesTransferred { get; init; }
}

/// <summary>
/// Final summary of the import job run.
/// </summary>
public sealed class ImportResult
{
    public long ArtifactsDiscovered { get; set; }
    public long ArtifactsCopied { get; set; }
    public long ArtifactsFailed { get; set; }
    public long BytesTransferred { get; set; }
    public List<ArtifactResult> FailedArtifacts { get; set; } = [];
    public bool IsSuccess => ArtifactsFailed == 0;
    public bool IsPartial => ArtifactsFailed > 0 && ArtifactsCopied > 0;
}

/// <summary>
/// Resolved transfer mode passed via environment variable.
/// </summary>
public enum TransferMode { DirectWrite, Http }

/// <summary>
/// Import mode identifying source type.
/// </summary>
public enum ImportMode
{
    ApiReposilite,
    ApiJFrog,
    PvcSnapshot,
    PvcLive,
}

/// <summary>
/// Deserialized ImportOptionsSpec from environment.
/// </summary>
public sealed class ImportOptions
{
    public int Parallelism { get; set; } = 8;
    public bool ChecksumValidation { get; set; } = true;
    public bool DryRun { get; set; } = false;
    public bool OverwriteExisting { get; set; } = false;
}

/// <summary>
/// Deserialized ImportFilterSpec from environment.
/// </summary>
public sealed class ImportFilters
{
    public List<string> IncludeGroups { get; set; } = [];
    public List<string> ExcludeGroups { get; set; } = [];
    public List<string> IncludeVersions { get; set; } = [];
    public string? SinceTimestamp { get; set; }
}

