using MavenOperator.Entities.Status;

namespace MavenOperator.Entities.Status;

public enum ImportPhase
{
    Pending,
    Running,
    Succeeded,
    Failed,
    PartiallyFailed,
}

public enum ResolvedTransferMode
{
    DirectWrite,
    Http,
}

public sealed class FailedArtifact
{
    public string Path { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Status sub-resource for a MavenRepositoryImport CRD.
/// </summary>
public sealed class MavenRepositoryImportStatus
{
    public ImportPhase Phase { get; set; } = ImportPhase.Pending;

    /// <summary>Resolved transfer mode — set when the Job is launched.</summary>
    public ResolvedTransferMode? TransferMode { get; set; }

    public long ArtifactsDiscovered { get; set; }
    public long ArtifactsCopied { get; set; }
    public long ArtifactsFailed { get; set; }
    public long BytesTransferred { get; set; }

    public DateTime? StartTime { get; set; }
    public DateTime? CompletionTime { get; set; }

    public List<RepositoryCondition> Conditions { get; set; } = [];

    /// <summary>Up to 100 failed artifact paths with failure reasons.</summary>
    public List<FailedArtifact> FailedArtifacts { get; set; } = [];

    public void SetCondition(string type, bool isTrue, string reason, string message)
    {
        var statusStr = isTrue ? "True" : "False";
        var existing = Conditions.FirstOrDefault(c => c.Type == type);
        if (existing is not null)
        {
            if (existing.Status == statusStr && existing.Reason == reason && existing.Message == message)
                return;
            existing.Status = statusStr;
            existing.Reason = reason;
            existing.Message = message;
            existing.LastTransitionTime = DateTime.UtcNow;
        }
        else
        {
            Conditions.Add(new RepositoryCondition
            {
                Type               = type,
                Status             = statusStr,
                Reason             = reason,
                Message            = message,
                LastTransitionTime = DateTime.UtcNow,
            });
        }
    }
}

