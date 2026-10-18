using k8s.Models;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using MavenOperator.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace MavenOperator.Tests.Integration.Hosted;

/// <summary>
/// Integration tests for replica count and topology spread constraint on HostedRepositoryReconciler.
/// Run with: INTEGRATION_TESTS=true dotnet test --filter Category=Integration
/// </summary>
[Collection(ClusterCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class HostedRepositoryReplicaTests(ClusterFixture cluster)
{
    private HostedRepositoryReconciler BuildReconciler() =>
        new(
            cluster.Client,
            new KubernetesResourceManager(cluster.Client, NullLogger<KubernetesResourceManager>.Instance),
            new HtpasswdService(),
            new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(),
            new NginxConfigRenderer(),
            NSubstitute.Substitute.For<MavenOperator.Services.IKubernetesEventService>(),
            NullLogger<HostedRepositoryReconciler>.Instance
        );

    private async Task<MavenRepositoryV1Alpha1> BuildEntityAsync(
        string name,
        AuthPolicy download = AuthPolicy.Anonymous,
        AuthPolicy upload = AuthPolicy.Authenticated,
        List<string>? uploadRefs = null)
    {
        var entity = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata = new()
            {
                Name              = name,
                NamespaceProperty = cluster.Namespace,
            },
            Spec = new()
            {
                Type    = RepositoryType.Hosted,
                Storage = new() { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth    = new()
                {
                    Download = new()
                    {
                        Policy = download,
                        Users = (uploadRefs ?? []).Select(s => new UserRef { SecretRef = s, Role = UserRole.Reader }).ToList(),
                    },
                    Upload   = new()
                    {
                        Policy = upload,
                        Users = (uploadRefs ?? []).Select(s => new UserRef { SecretRef = s, Role = UserRole.Deployer }).ToList(),
                    },
                },
            },
        };
        return await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(entity, CancellationToken.None);
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesDeployment_WithSpecifiedReplicas()
    {
        var name = $"int-rep-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);

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
        var name = $"int-defaultrep-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        dep.ShouldNotBeNull();
        dep!.Spec!.Replicas.ShouldBe(1);
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesDeployment_WithTopologySpreadConstraint()
    {
        var name = $"int-topo-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);

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
    public async Task Reconcile_CreatesDeployment_WithCustomTopologyKey()
    {
        var name = $"int-topozone-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);

        entity.Spec.TopologySpreadConstraint = new TopologySpreadConstraintConfig
        {
            TopologyKey = "topology.kubernetes.io/zone",
            MaxSkew = 1,
            WhenUnsatisfiable = TopologySpreadConstraintPolicy.DoNotSchedule,
        };

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        dep.ShouldNotBeNull();
        dep!.Spec!.Template!.Spec!.TopologySpreadConstraints.ShouldNotBeNull();
        dep.Spec.Template.Spec.TopologySpreadConstraints.Count.ShouldBe(1);
        dep.Spec.Template.Spec.TopologySpreadConstraints[0].TopologyKey.ShouldBe("topology.kubernetes.io/zone");
        dep.Spec.Template.Spec.TopologySpreadConstraints[0].WhenUnsatisfiable.ShouldBe("DoNotSchedule");
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesDeployment_NoTopologySpreadConstraint_WhenNotConfigured()
    {
        var name = $"int-notopo-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        dep.ShouldNotBeNull();
        (dep!.Spec!.Template!.Spec!.TopologySpreadConstraints ?? []).ShouldBeEmpty();
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesDeployment_WithZeroReplicas()
    {
        var name = $"int-zrep-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);

        entity.Spec.Replicas = 0;

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        dep.ShouldNotBeNull();
        dep!.Spec!.Replicas.ShouldBe(0);
    }
}
