using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;

namespace MavenOperator.Services;

/// <summary>
/// Determines whether a target PVC can be mounted by the import Job for direct writing,
/// and whether source PVCs have conflicting RWO bindings.
/// </summary>
public interface IPvcAccessChecker
{
    /// <summary>
    /// Resolves the effective transfer mode for the import.
    /// - If options.TransferMode == DirectWrite: always DirectWrite (caller is responsible for ensuring PVC is mountable).
    /// - If options.TransferMode == Http: always Http.
    /// - If options.TransferMode == Auto: DirectWrite when the target PVC supports RWX and is mountable, else Http.
    /// </summary>
    Task<ResolvedTransferMode> ResolveTransferModeAsync(
        MavenRepositoryV1Alpha1 target,
        string namespaceName,
        ImportOptionsSpec options,
        CancellationToken ct);

    /// <summary>
    /// Returns true if the given PVC is ReadWriteOnce AND is currently bound to a running pod.
    /// This prevents mounting a second time, which would fail for RWO PVCs.
    /// </summary>
    Task<bool> IsPvcRwoBoundToRunningPodAsync(
        string pvcName,
        string namespaceName,
        CancellationToken ct);
}

