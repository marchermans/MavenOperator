namespace MavenOperator.Entities.Spec;

/// <summary>
/// Storage configuration for Hosted repositories.
/// </summary>
public sealed class StorageSpec
{
    /// <summary>
    /// Requested storage size, e.g. "50Gi".
    /// </summary>
    public string Size { get; set; } = "10Gi";

    /// <summary>
    /// StorageClass to use for the PVC. Null means use the cluster default.
    /// </summary>
    public string? StorageClassName { get; set; }

    /// <summary>
    /// Controls whether the PVC is deleted when the MavenRepository CRD is deleted.
    /// Default is Retain — artifacts are never deleted automatically.
    /// </summary>
    public DeletionPolicy DeletionPolicy { get; set; } = DeletionPolicy.Retain;
}

public enum DeletionPolicy
{
    /// <summary>Keep the PVC (and its data) when the CRD is deleted.</summary>
    Retain,

    /// <summary>Delete the PVC (and its data) when the CRD is deleted.</summary>
    Delete,
}

