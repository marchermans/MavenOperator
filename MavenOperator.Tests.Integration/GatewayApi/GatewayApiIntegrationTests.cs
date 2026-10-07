using System.Text.Json;
using k8s;
using k8s.Models;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using MavenOperator.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace MavenOperator.Tests.Integration.GatewayApi;

/// <summary>
/// Integration tests for Gateway API (HTTPRoute) support against a live cluster.
/// Requires the Gateway API v1 CRDs to be installed (cluster_apply_gateway_crds).
/// Reconcilers are invoked in-process with a KubernetesResourceManager that has a
/// real raw k8s client, so HTTPRoutes are actually created/deleted in-cluster.
/// </summary>
[Collection(ClusterCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class GatewayApiIntegrationTests(ClusterFixture cluster)
{
    private const string HttpRouteGroup = "gateway.networking.k8s.io";

    // ── Setup helpers ────────────────────────────────────────────────────────

    /// <summary>Builds a hosted reconciler whose resource manager can talk to raw CustomObjects.</summary>
    private HostedRepositoryReconciler BuildReconciler() =>
        new(
            cluster.Client,
            new KubernetesResourceManager(cluster.Client, cluster.Raw, NullLogger<KubernetesResourceManager>.Instance),
            new HtpasswdService(),
            new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(),
            new NginxConfigRenderer(),
            Substitute.For<IKubernetesEventService>(),
            NullLogger<HostedRepositoryReconciler>.Instance);

    private async Task<MavenRepositoryV1Alpha1> CreateGatewayHostedRepoAsync(
        string name,
        string hostname,
        string path)
    {
        var entity = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta
            {
                Name              = name,
                NamespaceProperty = cluster.Namespace,
            },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete, AccessMode = "ReadWriteOnce" },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Gateway = new GatewaySpec
                {
                    Enabled  = true,
                    Hostname = hostname,
                    Path     = path,
                    GatewayRef = new GatewayRefSpec { Name = "test-gateway" },
                },
            },
        };

        return await cluster.Client.CreateAsync(entity, CancellationToken.None);
    }

    /// <summary>Fetches an HTTPRoute body as JSON; returns null when it does not exist.</summary>
    private async Task<JsonElement?> GetHttpRouteAsync(string routeName)
    {
        try
        {
            return await cluster.Raw.CustomObjects.GetNamespacedCustomObjectAsync<JsonElement>(
                HttpRouteGroup, "v1", cluster.Namespace, "httproutes", routeName, CancellationToken.None);
        }
        catch (KubernetesException ex) when (ex.Status?.Code == 404)
        {
            return null;
        }
        catch (k8s.Autorest.HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 404)
        {
            return null;
        }
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [IntegrationFact(DisplayName = "Hosted reconcile with gateway.enabled creates HTTPRoute and sets status.url")]
    public async Task GatewayEnabled_CreatesHttpRoute()
    {
        const string repoName   = "gw-create-test";
        const string hostname   = "gw-create.example.com";
        const string path       = "/repository/gw-create";

        var entity     = await CreateGatewayHostedRepoAsync(repoName, hostname, path);
        var reconciler = BuildReconciler();

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        // HostedRepositoryReconciler mutates entity.Status in memory only (the operator
        // framework persists it in production); persist manually for the assertions below.
        await cluster.Client.UpdateStatusAsync(entity, CancellationToken.None);

        // The HTTPRoute must exist in the cluster with the expected shape.
        var route = await GetHttpRouteAsync($"{repoName}-route");
        route.ShouldNotBeNull();

        var spec      = route!.Value.GetProperty("spec");
        var parentRef = spec.GetProperty("parentRefs")[0];
        parentRef.GetProperty("name").GetString().ShouldBe("test-gateway");
        spec.GetProperty("hostnames")[0].GetString().ShouldBe(hostname);

        var rule    = spec.GetProperty("rules")[0];
        var match   = rule.GetProperty("matches")[0];
        var backend = rule.GetProperty("backendRefs")[0];

        match.GetProperty("path").GetProperty("value").GetString().ShouldBe(path);
        backend.GetProperty("name").GetString().ShouldBe($"{repoName}-svc");
        backend.GetProperty("port").GetInt32().ShouldBe(80);

        // Route must be owned by the MavenRepository (garbage collection on delete).
        var ownerRef = route!.Value.GetProperty("metadata").GetProperty("ownerReferences")[0];
        ownerRef.GetProperty("kind").GetString().ShouldBe("MavenRepository");
        ownerRef.GetProperty("name").GetString().ShouldBe(repoName);

        // Status must reflect the gateway URL.
        var after = await cluster.Client.GetAsync<MavenRepositoryV1Alpha1>(repoName, cluster.Namespace, CancellationToken.None)!;
        after.Status.Url.ShouldBe($"http://{hostname}{path}");
        (after.Status.Conditions ?? new List<Entities.Status.RepositoryCondition>())
            .Any(c => c.Type == "GatewayReady" && c.Status == "True").ShouldBeTrue();
    }

    [IntegrationFact(DisplayName = "Disabling gateway removes the HTTPRoute")]
    public async Task GatewayDisabled_DeletesHttpRoute()
    {
        const string repoName   = "gw-delete-test";
        const string hostname   = "gw-delete.example.com";
        const string path       = "/repository/gw-delete";

        var entity     = await CreateGatewayHostedRepoAsync(repoName, hostname, path);
        var reconciler = BuildReconciler();

        await reconciler.ReconcileAsync(entity, CancellationToken.None);
        (await GetHttpRouteAsync($"{repoName}-route")).ShouldNotBeNull();

        // Toggle the gateway off and reconcile again.
        entity.Spec.Gateway!.Enabled = false;
        await cluster.Client.UpdateAsync(entity, CancellationToken.None);
        entity = await cluster.Client.GetAsync<MavenRepositoryV1Alpha1>(repoName, cluster.Namespace, CancellationToken.None)!;

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        (await GetHttpRouteAsync($"{repoName}-route")).ShouldBeNull();
    }

    [IntegrationFact(DisplayName = "CRD CEL rule rejects enabling ingress and gateway at the same time")]
    public async Task IngressAndGatewayMutualExclusion_EnforcedByApiServer()
    {
        var entity = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta
            {
                Name              = "gw-conflict-test",
                NamespaceProperty = cluster.Namespace,
            },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Ingress = new IngressSpec { Enabled = true, Host = "conflict.example.com" },
                Gateway = new GatewaySpec
                {
                    Enabled    = true,
                    GatewayRef = new GatewayRefSpec { Name = "test-gateway" },
                },
            },
        };

        var ex = await Should.ThrowAsync<Exception>(async () =>
            await cluster.Client.CreateAsync(entity, CancellationToken.None));

        // The API server rejects the object with the CEL validation message.
        (ex.Message + (ex.InnerException?.Message ?? "")).ShouldContain("gateway");
    }
}
