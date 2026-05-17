using k8s.Models;
using KubeOps.Abstractions.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;

namespace MavenOperator.Entities;

/// <summary>
/// MavenRepositoryImport triggers a one-shot Kubernetes Job that migrates
/// artifacts from an external Maven repository into an operator-managed Hosted repository.
/// Three import modes are supported: REST API crawl, PVC snapshot clone, and live PVC clone.
/// </summary>
[KubernetesEntity(
    Group      = "maven.operator.io",
    ApiVersion = "v1alpha1",
    Kind       = "MavenRepositoryImport",
    PluralName = "mavenrepositoryimports")]
public sealed class MavenRepositoryImportV1Alpha1
    : CustomKubernetesEntity<MavenRepositoryImportSpec, MavenRepositoryImportStatus>
{
}


