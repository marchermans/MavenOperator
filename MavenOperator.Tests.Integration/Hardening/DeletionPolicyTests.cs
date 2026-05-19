using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Controllers;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using MavenOperator.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace MavenOperator.Tests.Integration.Hardening;

/// <summary>
/// Integration tests validating that the <c>spec.storage.deletionPolicy</c> field
/// controls whether the PVC is deleted when a MavenRepository is deleted.
///
/// Run with: INTEGRATION_TESTS=true dotnet test MavenOperator.Tests.Integration
/// </summary>
[Collection(ClusterCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class DeletionPolicyTests(ClusterFixture cluster)
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private MavenRepositoryController BuildController(
        HostedRepositoryReconciler hostedReconciler) =>
        new(
            hostedReconciler,
            Substitute.For<IProxyRepositoryReconciler>(),
            Substitute.For<IVirtualRepositoryReconciler>(),
            Substitute.For<IKubernetesEventService>(),
            new KubernetesResourceManager(cluster.Client, NullLogger<KubernetesResourceManager>.Instance),
            NullLogger<MavenRepositoryController>.Instance);

    private HostedRepositoryReconciler BuildHostedReconciler() =>
        new(
            cluster.Client,
            new KubernetesResourceManager(cluster.Client, NullLogger<KubernetesResourceManager>.Instance),
            new HtpasswdService(),
            new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(),
            new NginxConfigRenderer(),
            Substitute.For<IKubernetesEventService>(),
            NullLogger<HostedRepositoryReconciler>.Instance);

    private async Task<MavenRepositoryV1Alpha1> CreateRepoAsync(
        string name, DeletionPolicy policy)
    {
        var repo = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = cluster.Namespace },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = policy },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
            },
        };
        return await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(repo, CancellationToken.None);
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task DeletionPolicy_Delete_DeletesPvc_WhenControllerDeletedAsyncCalled()
    {
        var name = "deletion-delete-test";
        var entity = await CreateRepoAsync(name, DeletionPolicy.Delete);

        // Reconcile so the PVC is created.
        var hosted = BuildHostedReconciler();
        var controller = BuildController(hosted);
        await hosted.ReconcileAsync(entity, CancellationToken.None);

        // Verify PVC exists.
        var pvcBefore = await cluster.Client.GetAsync<V1PersistentVolumeClaim>(
            $"{name}-pvc", cluster.Namespace, CancellationToken.None);
        pvcBefore.ShouldNotBeNull();

        // Trigger deletion handling.
        await controller.DeletedAsync(entity, CancellationToken.None);

        // PVC should now be gone.
        await ClusterFixture.WaitUntilAsync(
            async () =>
            {
                var pvc = await cluster.Client.GetAsync<V1PersistentVolumeClaim>(
                    $"{name}-pvc", cluster.Namespace, CancellationToken.None);
                return pvc is null;
            },
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromSeconds(2),
            description: $"PVC {name}-pvc to be deleted");
    }

    [IntegrationFact]
    public async Task DeletionPolicy_Retain_KeepsPvc_AfterControllerDeletedAsyncCalled()
    {
        var name = "deletion-retain-test";
        var entity = await CreateRepoAsync(name, DeletionPolicy.Retain);

        var hosted = BuildHostedReconciler();
        var controller = BuildController(hosted);
        await hosted.ReconcileAsync(entity, CancellationToken.None);

        // Verify PVC exists.
        var pvcBefore = await cluster.Client.GetAsync<V1PersistentVolumeClaim>(
            $"{name}-pvc", cluster.Namespace, CancellationToken.None);
        pvcBefore.ShouldNotBeNull();

        // Trigger deletion handling — PVC should be left intact.
        await controller.DeletedAsync(entity, CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(3));

        var pvcAfter = await cluster.Client.GetAsync<V1PersistentVolumeClaim>(
            $"{name}-pvc", cluster.Namespace, CancellationToken.None);
        pvcAfter.ShouldNotBeNull("PVC should be retained when DeletionPolicy=Retain");
    }
}

