using MavenOperator.Entities.Spec;

namespace MavenOperator.Entities.Status;

/// <summary>
/// Observed phase of a MavenRepository.
/// </summary>
public enum RepositoryPhase
{
    /// <summary>CRD was just created; no resources have been provisioned yet.</summary>
    Pending,

    /// <summary>The operator is actively creating or updating child resources.</summary>
    Provisioning,

    /// <summary>All child resources are healthy and the repository is serving traffic.</summary>
    Ready,

    /// <summary>The repository is partially available but some components have issues.</summary>
    Degraded,

    /// <summary>Reconciliation failed; see status.conditions for details.</summary>
    Failed,
}

/// <summary>
/// A single condition in the status.conditions list, following the Kubernetes conventions.
/// </summary>
public sealed class RepositoryCondition
{
    /// <summary>Short camel-case type name, e.g. "Available", "StorageBound", "AuthReady".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>"True", "False", or "Unknown".</summary>
    public string Status { get; set; } = "Unknown";

    /// <summary>Machine-readable reason code in CamelCase.</summary>
    public string? Reason { get; set; }

    /// <summary>Human-readable description of the current state.</summary>
    public string? Message { get; set; }

    /// <summary>When the condition last transitioned.</summary>
    public DateTime LastTransitionTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Upload status for Proxy repositories with upload forwarding enabled.
/// </summary>
public sealed class ProxyUploadStatus
{
    /// <summary>Whether upload forwarding is currently active.</summary>
    public bool Enabled { get; set; }

    /// <summary>The configured client auth mode.</summary>
    public ProxyUploadMode Mode { get; set; }

    /// <summary>True if upstream credentials Secret was successfully loaded.</summary>
    public bool UpstreamCredentialsConfigured { get; set; }

    /// <summary>Error message if credentials could not be loaded, otherwise null.</summary>
    public string? LastSyncError { get; set; }
}

/// <summary>
/// Per-target upload status for Virtual repositories.
/// </summary>
public sealed class VirtualUploadTargetStatus
{
    /// <summary>Name of the target member repository.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>True if the target is reachable (via periodic HEAD probe).</summary>
    public bool Reachable { get; set; }

    /// <summary>Error message from last failed probe or upload, otherwise null.</summary>
    public string? LastError { get; set; }

    /// <summary>Timestamp of the last successful upload to this target.</summary>
    public DateTime? LastUploadSuccess { get; set; }
}

/// <summary>
/// Upload status for Virtual repositories with upload fan-out enabled.
/// </summary>
public sealed class VirtualUploadStatus
{
    /// <summary>True if uploadTargets is non-empty and uploads are active.</summary>
    public bool Enabled { get; set; }

    /// <summary>Per-target reachability and health status.</summary>
    public List<VirtualUploadTargetStatus> Targets { get; set; } = [];
}

/// <summary>
/// Upload-specific status, populated only for Proxy/Virtual repos with uploads enabled.
/// </summary>
public sealed class UploadStatus
{
    /// <summary>Status for Proxy repositories (only when type == Proxy).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("proxy")]
    public ProxyUploadStatus? Proxy { get; set; }

    /// <summary>Status for Virtual repositories (only when type == Virtual).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("virtual")]
    public VirtualUploadStatus? Virtual { get; set; }
}

/// <summary>
/// Status sub-resource for a MavenRepository CRD.
/// </summary>
public sealed class MavenRepositoryStatus
{
    /// <summary>High-level phase of the repository.</summary>
    public RepositoryPhase Phase { get; set; } = RepositoryPhase.Pending;

    /// <summary>
    /// The URL at which the repository is accessible, populated once Ready.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>Detailed conditions list following Kubernetes conventions.</summary>
    public List<RepositoryCondition> Conditions { get; set; } = [];

    /// <summary>
    /// The generation of the CRD spec that was last successfully reconciled.
    /// </summary>
    public long ObservedGeneration { get; set; }

    /// <summary>
    /// Upload-specific status (only populated for Proxy/Virtual with uploads enabled).
    /// </summary>
    public UploadStatus? Upload { get; set; }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets or replaces a condition by type, only if the status actually changed
    /// (to avoid spurious updates that bump resourceVersion).
    /// </summary>
    public void SetCondition(string type, bool isTrue, string reason, string message)
    {
        var statusStr = isTrue ? "True" : "False";
        var existing = Conditions.FirstOrDefault(c => c.Type == type);

        if (existing is not null)
        {
            if (existing.Status == statusStr && existing.Reason == reason && existing.Message == message)
                return; // no change

            existing.Status = statusStr;
            existing.Reason = reason;
            existing.Message = message;
            existing.LastTransitionTime = DateTime.UtcNow;
        }
        else
        {
            Conditions.Add(new RepositoryCondition
            {
                Type = type,
                Status = statusStr,
                Reason = reason,
                Message = message,
                LastTransitionTime = DateTime.UtcNow,
            });
        }
    }
}

