using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace MavenOperator.Tests.Unit.Services;

/// <summary>
/// Unit tests for the Phase 4 hardening features in the reconcilers:
/// proxy cache PVC option, ingress URL population, and condition setting.
/// </summary>
public sealed class Phase4ReconcilerHardeningTests
{
    // ── Proxy cache PVC ──────────────────────────────────────────────────────

    [Fact]
    public async Task ProxyReconciler_SetsCacheReadyCondition_EmptyDir_WhenNoCachePvcSize()
    {
        var resources = Substitute.For<IKubernetesResourceManager>();
        var k8s       = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events    = Substitute.For<IKubernetesEventService>();

        // Credential secret fetch (no upload/download creds needed — Anonymous)
        // NginxConfigRenderer returns a string
        var nginx = new NginxConfigRenderer();

        var reconciler = new ProxyRepositoryReconciler(
            k8s, resources, new HtpasswdService(), nginx, events,
            NullLogger<ProxyRepositoryReconciler>.Instance);

        var entity = BuildProxyEntity("proxy-repo", "ns", cachePvcSize: null);

        // Wire up mocks to return dummy values
        resources.EnsureSecretAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1Secret());
        resources.EnsureConfigMapAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1ConfigMap());
        resources.EnsureDeploymentAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<k8s.Models.V1PodSpec>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1Deployment());
        resources.EnsureServiceAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1Service());

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        // emptyDir path: no PVC should be requested
        await resources.DidNotReceive().EnsurePvcAsync(
            Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        // CacheReady condition should be set with EmptyDirCache reason
        var cacheCondition = entity.Status.Conditions.FirstOrDefault(c => c.Type == "CacheReady");
        cacheCondition.ShouldNotBeNull();
        cacheCondition!.Status.ShouldBe("True");
        cacheCondition.Reason.ShouldBe("EmptyDirCache");
    }

    [Fact]
    public async Task ProxyReconciler_CreatesCachePvc_WhenCachePvcSizeIsSet()
    {
        var resources = Substitute.For<IKubernetesResourceManager>();
        var k8s       = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events    = Substitute.For<IKubernetesEventService>();
        var nginx     = new NginxConfigRenderer();

        var reconciler = new ProxyRepositoryReconciler(
            k8s, resources, new HtpasswdService(), nginx, events,
            NullLogger<ProxyRepositoryReconciler>.Instance);

        const string cacheSize = "5Gi";
        var entity = BuildProxyEntity("proxy-repo", "ns", cachePvcSize: cacheSize);

        resources.EnsurePvcAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1PersistentVolumeClaim());
        resources.EnsureSecretAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1Secret());
        resources.EnsureConfigMapAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1ConfigMap());
        resources.EnsureDeploymentAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<k8s.Models.V1PodSpec>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1Deployment());
        resources.EnsureServiceAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1Service());

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        // Cache PVC should have been requested
        await resources.Received(1).EnsurePvcAsync(
            entity,
            $"proxy-repo-cache-pvc",
            cacheSize,
            null,
            true /* setOwnerReference */,
            Arg.Any<CancellationToken>());

        var cacheCondition = entity.Status.Conditions.FirstOrDefault(c => c.Type == "CacheReady");
        cacheCondition.ShouldNotBeNull();
        cacheCondition!.Status.ShouldBe("True");
        cacheCondition.Reason.ShouldBe("PvcCacheEnsured");
    }

    // ── Status URL population ────────────────────────────────────────────────

    [Fact]
    public async Task HostedReconciler_SetsInternalUrl_WhenIngressDisabled()
    {
        var resources = Substitute.For<IKubernetesResourceManager>();
        var k8s       = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events    = Substitute.For<IKubernetesEventService>();

        var reconciler = new HostedRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new NginxConfigRenderer(), events,
            NullLogger<HostedRepositoryReconciler>.Instance);

        var entity = BuildHostedEntity("my-repo", "ns", ingressEnabled: false);

        WireUpHostedMocks(resources);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        entity.Status.Url.ShouldBe("http://my-repo-svc/repository/my-repo");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MavenRepositoryV1Alpha1 BuildProxyEntity(
        string name, string ns, string? cachePvcSize) =>
        new()
        {
            Metadata = new k8s.Models.V1ObjectMeta { Name = name, NamespaceProperty = ns },
            Spec = new MavenRepositorySpec
            {
                Type     = RepositoryType.Proxy,
                Upstream = new UpstreamSpec
                {
                    Url          = "https://repo1.maven.org/maven2",
                    CachePvcSize = cachePvcSize,
                },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
            },
        };

    private static MavenRepositoryV1Alpha1 BuildHostedEntity(
        string name, string ns, bool ingressEnabled) =>
        new()
        {
            Metadata = new k8s.Models.V1ObjectMeta { Name = name, NamespaceProperty = ns },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi" },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Ingress = new IngressSpec { Enabled = ingressEnabled },
            },
        };

    private static void WireUpHostedMocks(IKubernetesResourceManager resources)
    {
        resources.EnsurePvcAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1PersistentVolumeClaim());
        resources.EnsureSecretAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1Secret());
        resources.EnsureConfigMapAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1ConfigMap());
        resources.EnsureDeploymentAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<k8s.Models.V1PodSpec>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1Deployment());
        resources.EnsureServiceAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new k8s.Models.V1Service());
    }
}

