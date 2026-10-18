using k8s.Models;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using MavenOperator.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace MavenOperator.Tests.Integration.Proxy;

/// <summary>
/// Integration tests for replica count and topology spread constraint on ProxyRepositoryReconciler.
/// Run with: INTEGRATION_TESTS=true dotnet test --filter Category=Integration
/// </summary>
[Collection(ClusterCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class ProxyRepositoryReplicaTests(ClusterFixture cluster)
{
    private ProxyRepositoryReconciler BuildReconciler() =>
        new(
            cluster.Client,
            new KubernetesResourceManager(cluster.Client, NullLogger<KubernetesResourceManager>.Instance),
            new HtpasswdService(),
            new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(),
            new NginxConfigRenderer(),
            NSubstitute.Substitute.For<MavenOperator.Services.IKubernetesEventService>(),
            NullLogger<ProxyRepositoryReconciler>.Instance
        );

    private async Task<MavenRepositoryV1Alpha1> BuildEntityAsync(
        string name,
        string upstreamUrl = "https://repo1.maven.org/maven2",
        AuthPolicy download = AuthPolicy.Anonymous,
        string? upstreamSecretRef = null)
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
                Type     = RepositoryType.Proxy,
                Upstream = new()
                {
                    Url      = upstreamUrl,
                    CacheTtl = "1d",
                    Auth     = upstreamSecretRef is null ? null : new() { SecretRef = upstreamSecretRef },
                },
                Auth = new()
                {
                    Download = new() { Policy = download },
                    Upload   = new() { Policy = AuthPolicy.Anonymous },
                },
            },
        };
        return await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(entity, CancellationToken.None);
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesDeployment_WithSpecifiedReplicas()
    {
        var name = $"int-proxrep-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name);

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
        var name = $"int-proxdef-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        dep.ShouldNotBeNull();
        dep!.Spec!.Replicas.ShouldBe(1);
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesDeployment_WithTopologySpreadConstraint()
    {
        var name = $"int-proxtopo-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name);

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
        var name = $"int-proxnotopo-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        dep.ShouldNotBeNull();
        (dep!.Spec!.Template!.Spec!.TopologySpreadConstraints ?? []).ShouldBeEmpty();
    }
}
