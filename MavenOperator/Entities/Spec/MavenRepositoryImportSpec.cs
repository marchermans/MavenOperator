namespace MavenOperator.Entities.Spec;

/// <summary>
/// Full spec for a MavenRepositoryImport CRD instance.
/// Exactly one of Source.Api, Source.PvcSnapshot, or Source.PvcLive must be set.
/// </summary>
public sealed class MavenRepositoryImportSpec
{
    /// <summary>Name of the target Hosted MavenRepository in the same namespace.</summary>
    public string TargetRepository { get; set; } = string.Empty;

    /// <summary>Source configuration — exactly one sub-field must be populated.</summary>
    public ImportSourceSpec Source { get; set; } = new();

    /// <summary>Optional artifact filters applied across all import modes.</summary>
    public ImportFilterSpec Filters { get; set; } = new();

    /// <summary>Tuning options for the import Job.</summary>
    public ImportOptionsSpec Options { get; set; } = new();
}

/// <summary>
/// Discriminated union for the import source.
/// Exactly one of Api, PvcSnapshot, PvcLive must be non-null.
/// </summary>
public sealed class ImportSourceSpec
{
    /// <summary>REST API crawl — Reposilite or JFrog Artifactory Cloud.</summary>
    public ApiSourceSpec? Api { get; set; }

    /// <summary>Disk-to-disk clone from a snapshot or backup PVC.</summary>
    public PvcSnapshotSourceSpec? PvcSnapshot { get; set; }

    /// <summary>Disk-to-disk clone from the live Reposilite PVC (with optional scale-down).</summary>
    public PvcLiveSourceSpec? PvcLive { get; set; }
}

/// <summary>REST API crawl source (Reposilite or JFrog Cloud).</summary>
public sealed class ApiSourceSpec
{
    public ApiSourceType Type { get; set; } = ApiSourceType.Reposilite;
    public string Url { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    /// <summary>Secret with keys: username+password (Reposilite) OR token (JFrog Cloud).</summary>
    public string? CredentialsSecret { get; set; }
    /// <summary>Include .asc GPG signature files. Default false.</summary>
    public bool IncludeSignatures { get; set; } = false;
}

public enum ApiSourceType
{
    Reposilite,
    JFrogCloud,
}

/// <summary>Clone from an external / snapshot PVC (no live server required).</summary>
public sealed class PvcSnapshotSourceSpec
{
    public string ClaimName { get; set; } = string.Empty;
    /// <summary>Optional sub-directory within the PVC to use as the root.</summary>
    public string? SubPath { get; set; }
    /// <summary>
    /// When true, strips the leading /&lt;repository&gt;/ path segment (Reposilite on-disk layout).
    /// When false, treats the source as a raw Maven layout PVC.
    /// </summary>
    public bool ReposiliteLayout { get; set; } = true;
}

/// <summary>Clone from a live Reposilite PVC running in the same cluster.</summary>
public sealed class PvcLiveSourceSpec
{
    public string ClaimName { get; set; } = string.Empty;
    /// <summary>
    /// Name of the Reposilite Deployment to scale to 0 before mounting.
    /// Leave null to skip scale-down (requires RWX PVC).
    /// </summary>
    public string? ReposiliteDeployment { get; set; }
    /// <summary>
    /// How long to wait for Reposilite pods to stop before starting the Job.
    /// Use "0s" to skip scale-down (concurrent import, RWX required).
    /// </summary>
    public string ScaleDownDuration { get; set; } = "60s";
    public string? SubPath { get; set; }
}

/// <summary>Artifact filters applied during crawl/copy.</summary>
public sealed class ImportFilterSpec
{
    /// <summary>Glob list of Maven groupIds to include, e.g. ["com.example.*"].</summary>
    public List<string> IncludeGroups { get; set; } = [];
    /// <summary>Glob list of Maven groupIds to exclude.</summary>
    public List<string> ExcludeGroups { get; set; } = [];
    /// <summary>Glob list of Maven versions to include, e.g. ["1.*"].</summary>
    public List<string> IncludeVersions { get; set; } = [];
    /// <summary>Skip artifacts not modified after this RFC3339 timestamp.</summary>
    public string? SinceTimestamp { get; set; }
}

/// <summary>Tuning options for the import Job.</summary>
public sealed class ImportOptionsSpec
{
    /// <summary>Maximum concurrent artifact copy workers.</summary>
    public int Parallelism { get; set; } = 8;
    /// <summary>After copying each artifact, re-read and compare SHA-256.</summary>
    public bool ChecksumValidation { get; set; } = true;
    /// <summary>When true, discover artifacts but make no writes.</summary>
    public bool DryRun { get; set; } = false;
    /// <summary>When false, skip artifacts that already exist on the target.</summary>
    public bool OverwriteExisting { get; set; } = false;
    /// <summary>
    /// Transfer mode override.
    /// Auto = DirectWrite when target PVC is RWX-mountable, else Http fallback.
    /// </summary>
    public ImportTransferMode TransferMode { get; set; } = ImportTransferMode.Auto;
}

public enum ImportTransferMode
{
    /// <summary>Automatically pick DirectWrite if possible, Http otherwise.</summary>
    Auto,
    /// <summary>Write bytes directly to the target PVC filesystem (fastest).</summary>
    DirectWrite,
    /// <summary>Upload via HTTP PUT to the NGINX WebDAV endpoint.</summary>
    Http,
}

