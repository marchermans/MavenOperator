using k8s.Models;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using MavenOperator.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace MavenOperator.Tests.Integration.Virtual;

/// <summary>
/// Integration tests for replica count and topology spread constraint on VirtualRepositoryReconciler.
/// Run with: INTEGRATION_TESTS=true dotnet test --filter Category=Integration
/// </summary>
[Collection(ClusterCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class VirtualRepositoryReplicaTests(ClusterFixture cluster)
{
    private VirtualRepositoryReconciler BuildReconciler() =>
        new(
            cluster.Client,
            new KubernetesResourceManager(cluster.Client, NullLogger<KubernetesResourceManager>.Instance),
            new HtpasswdService(),
            new NginxConfigRenderer(),
            NSubstitute.Substitute.For<MavenOperator.Services.IKubernetesEventService>(),
            NullLogger<VirtualRepositoryReconciler>.Instance
        );

    private async Task<MavenRepositoryV1Alpha1> BuildEntityAsync(
        string name,
        List<string> members,
        AuthPolicy downloadPolicy = AuthPolicy.Anonymous,
        List<string>? downloadSecrets = null,
        string? pathPrefix = null)
    {
        var entity = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new()
            {
                Name              = name,
                NamespaceProperty = cluster.Namespace,
            },
            Spec = new()
            {
                Type     = RepositoryType.Virtual,
                PathPrefix = pathPrefix,
                Virtual  = new() { Members = members, MetadataCacheTtlSeconds = 60 },
                Auth     = new()
                {
                    Download = new()
                    {
                        Policy  = downloadPolicy,
                        Users = (downloadSecrets ?? []).Select(s => new UserRef { SecretRef = s, Role = UserRole.Reader }).ToList(),
                    },
                    Upload = new() { Policy = AuthPolicy.Anonymous },
                },
            },
        };
        return await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(entity, CancellationToken.None);
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesDeployment_WithSpecifiedReplicas()
    {
        var name = $"int-vrep-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);

        entity.Spec.Replicas = 3;

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        dep.ShouldNotBeNull();
        dep!.Spec!.Replicas.ShouldNotBeNull();
        dep.Spec.Replicas.ShouldBe(3);
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesDeployment_WithDefaultReplica_WhenNotSpecified()
    {
        var name = $"int-vdef-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        dep.ShouldNotBeNull();
        dep!.Spec!.Replicas.ShouldBe(1);
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesDeployment_WithTopologySpreadConstraint()
    {
        var name = $"int-vtopo-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);

        entity.Spec.TopologySpreadConstraint = new TopologySpreadConstraintConfig
        {
            TopologyKey = "kubernetes.io/hostname",
            MaxSkew = 1,
            WhenUnsatisfiable = TopologySpreadConstraintPolicy.ScheduleAnyway,
        };

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        dep.ShouldNotBeNull();
        dep!.Spec!.Template!.Spec!.TopologySpreadConstraints.ShouldNotBeNull();
        dep.Spec.Template.Spec.TopologySpreadConstraints.Count.ShouldBe(1);
        dep.Spec.Template.Spec.TopologySpreadConstraints[0].TopologyKey.ShouldBe("kubernetes.io/hostname");
        dep.Spec.Template.Spec.TopologySpreadConstraints[0].MaxSkew.ShouldBe(1);
        dep.Spec.Template.Spec.TopologySpreadConstraints[0].WhenUnsatisfiable.ShouldBe("ScheduleAnyway");
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesDeployment_NoTopologySpreadConstraint_WhenNotConfigured()
    {
        var name = $"int-vnotopo-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        dep.ShouldNotBeNull();
        (dep!.Spec!.Template!.Spec!.TopologySpreadConstraints ?? []).ShouldBeEmpty();
    }
}
