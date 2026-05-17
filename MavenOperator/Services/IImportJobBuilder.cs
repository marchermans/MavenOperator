using k8s.Models;
using MavenOperator.Entities;
using MavenOperator.Entities.Status;

namespace MavenOperator.Services;

/// <summary>
/// Builds the Kubernetes Job spec for a MavenRepositoryImport.
/// The Job runs the MavenOperator.ImportJob console binary with volumes and
/// environment variables matched to the import mode and transfer mode.
/// </summary>
public interface IImportJobBuilder
{
    /// <summary>
    /// Constructs a V1Job that, when created in the cluster, will execute
    /// the import according to the import CR spec and the resolved transfer mode.
    /// </summary>
    Task<V1Job> BuildJobAsync(
        MavenRepositoryImportV1Alpha1 import,
        MavenRepositoryV1Alpha1 target,
        ResolvedTransferMode transferMode,
        string importJobImage,
        CancellationToken ct);
}

