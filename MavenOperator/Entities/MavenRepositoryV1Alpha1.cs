using k8s.Models;
using KubeOps.Abstractions.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;

namespace MavenOperator.Entities;

/// <summary>
/// MavenRepository is the central CRD managed by this operator.
/// Applying one of these resources causes the operator to provision the
/// matching NGINX-based Maven repository infrastructure.
/// </summary>
[KubernetesEntity(
    Group = "maven.operator.io",
    ApiVersion = "v1alpha1",
    Kind = "MavenRepository",
    PluralName = "mavenrepositories")]
public sealed class MavenRepositoryV1Alpha1 : CustomKubernetesEntity<MavenRepositorySpec, MavenRepositoryStatus>
{
}
