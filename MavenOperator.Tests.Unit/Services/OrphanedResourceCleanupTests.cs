using k8s.Models;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MavenOperator.Tests.Unit.Services;

/// <summary>
/// Unit tests that verify obsolete Kubernetes resources are cleaned up when
/// the MavenRepository spec is updated to no longer require them.
///
/// Scenarios covered:
///   - Ingress removed  → old Ingress + Certificate deleted
///   - Gateway removed  → old HTTPRoute + Certificate deleted
///   - Ingress → Gateway switch  → old Ingress cleaned up
///   - Gateway → Ingress switch  → old HTTPRoute cleaned up
///   - Metrics disabled  → PodMonitor + mtail ConfigMap deleted
///   - Auth proxy no longer needed  → auth-proxy ConfigMap deleted
///   - Proxy cache PVC removed from spec  → cache PVC deleted
/// </summary>
public sealed class OrphanedResourceCleanupTests
{
    // ── Shared setup ─────────────────────────────────────────────────────────

    private static IKubernetesResourceManager CreateResourcesMock()
    {
        var resources = Substitute.For<IKubernetesResourceManager>();
        resources.EnsurePvcAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(new V1PersistentVolumeClaim());
        resources.EnsureSecretAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(new V1Secret());
        resources.EnsureConfigMapAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(new V1ConfigMap());
        resources.EnsureDeploymentAsync(default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(new V1Deployment());
        resources.EnsureServiceAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(new V1Service());
        resources.EnsureServiceWithPortsAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new V1Service());
        resources.EnsureIngressAsync(default!, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new V1Ingress());
        resources.EnsureHttpRouteAsync(default!, default!, default!, default, default!, default!, default)
            .ReturnsForAnyArgs(false);
        resources.EnsureCertificateAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(false);
        resources.EnsurePodMonitorAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(false);
        return resources;
    }

    private static MavenRepositoryV1Alpha1 BuildHostedEntity(
        string name,
        string ns,
        bool ingressEnabled = false,
        bool gatewayEnabled = false,
        bool metricsEnabled = false) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi" },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Ingress = new IngressSpec { Enabled = ingressEnabled, Host = ingressEnabled ? "repo.example.com" : null },
                Gateway = new GatewaySpec { Enabled = gatewayEnabled, Hostname = gatewayEnabled ? "gw.example.com" : null },
                Metrics = new MetricsSpec { Enabled = metricsEnabled },
            },
        };

    private static MavenRepositoryV1Alpha1 BuildProxyEntity(
        string name,
        string ns,
        bool ingressEnabled = false,
        bool gatewayEnabled = false,
        bool metricsEnabled = false,
        string? cachePvcSize = null) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
            Spec = new MavenRepositorySpec
            {
                Type = RepositoryType.Proxy,
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
                Ingress = new IngressSpec { Enabled = ingressEnabled, Host = ingressEnabled ? "proxy.example.com" : null },
                Gateway = new GatewaySpec { Enabled = gatewayEnabled, Hostname = gatewayEnabled ? "gw-proxy.example.com" : null },
                Metrics = new MetricsSpec { Enabled = metricsEnabled },
            },
        };

    private static MavenRepositoryV1Alpha1 BuildVirtualEntity(
        string name,
        string ns,
        bool ingressEnabled = false,
        bool gatewayEnabled = false) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Virtual,
                Virtual = new VirtualSpec { Members = ["member-a", "member-b"] },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Ingress = new IngressSpec { Enabled = ingressEnabled, Host = ingressEnabled ? "virtual.example.com" : null },
                Gateway = new GatewaySpec { Enabled = gatewayEnabled, Hostname = gatewayEnabled ? "gw-virtual.example.com" : null },
            },
        };

    // ── Hosted reconciler ─────────────────────────────────────────────────────

    [Fact]
    public async Task HostedReconciler_DeletesIngress_WhenIngressDisabled()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new HostedRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(), new NginxConfigRenderer(), events,
            NullLogger<HostedRepositoryReconciler>.Instance);

        var entity = BuildHostedEntity("my-repo", "ns", ingressEnabled: false);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.Received(1).DeleteResourceIfExistsAsync<V1Ingress>("my-repo-ingress", "ns", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HostedReconciler_DeletesHttpRoute_WhenGatewayDisabled()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new HostedRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(), new NginxConfigRenderer(), events,
            NullLogger<HostedRepositoryReconciler>.Instance);

        var entity = BuildHostedEntity("my-repo", "ns", gatewayEnabled: false);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.Received(1).DeleteCustomResourceIfExistsAsync(
            "gateway.networking.k8s.io", "v1", "httproutes", "my-repo-route", "ns", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HostedReconciler_DeletesPodMonitorAndMtailCm_WhenMetricsDisabled()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new HostedRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(), new NginxConfigRenderer(), events,
            NullLogger<HostedRepositoryReconciler>.Instance);

        var entity = BuildHostedEntity("my-repo", "ns", metricsEnabled: false);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.Received(1).DeleteCustomResourceIfExistsAsync(
            "monitoring.coreos.com", "v1", "podmonitors", "my-repo-metrics", "ns", Arg.Any<CancellationToken>());
        await resources.Received(1).DeleteResourceIfExistsAsync<V1ConfigMap>(
            "my-repo-mtail-cm", "ns", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HostedReconciler_DeletesAuthProxyCm_WhenNoAuthProxyNeeded()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new HostedRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(), new NginxConfigRenderer(), events,
            NullLogger<HostedRepositoryReconciler>.Instance);

        // No CI trust / ACLs → useAuthProxy = false → auth-proxy-cm should be deleted
        var entity = BuildHostedEntity("my-repo", "ns");

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.Received(1).DeleteResourceIfExistsAsync<V1ConfigMap>(
            "my-repo-auth-proxy-cm", "ns", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HostedReconciler_DoesNotDeleteIngress_WhenIngressEnabled()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new HostedRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(), new NginxConfigRenderer(), events,
            NullLogger<HostedRepositoryReconciler>.Instance);

        var entity = BuildHostedEntity("my-repo", "ns", ingressEnabled: true);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.DidNotReceive().DeleteResourceIfExistsAsync<V1Ingress>(
            "my-repo-ingress", "ns", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HostedReconciler_DoesNotDeletePodMonitor_WhenMetricsEnabled()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new HostedRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(), new NginxConfigRenderer(), events,
            NullLogger<HostedRepositoryReconciler>.Instance);

        var entity = BuildHostedEntity("my-repo", "ns", metricsEnabled: true);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.DidNotReceive().DeleteCustomResourceIfExistsAsync(
            "monitoring.coreos.com", "v1", "podmonitors", "my-repo-metrics", "ns", Arg.Any<CancellationToken>());
        await resources.DidNotReceive().DeleteResourceIfExistsAsync<V1ConfigMap>(
            "my-repo-mtail-cm", "ns", Arg.Any<CancellationToken>());
    }

    // ── Proxy reconciler ─────────────────────────────────────────────────────

    [Fact]
    public async Task ProxyReconciler_DeletesIngress_WhenIngressDisabled()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new ProxyRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(), new NginxConfigRenderer(), events,
            NullLogger<ProxyRepositoryReconciler>.Instance);

        var entity = BuildProxyEntity("proxy-repo", "ns", ingressEnabled: false);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.Received(1).DeleteResourceIfExistsAsync<V1Ingress>(
            "proxy-repo-ingress", "ns", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProxyReconciler_DeletesCachePvc_WhenCachePvcSizeRemovedFromSpec()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new ProxyRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(), new NginxConfigRenderer(), events,
            NullLogger<ProxyRepositoryReconciler>.Instance);

        // No cachePvcSize → usePvcCache = false → cache PVC should be deleted
        var entity = BuildProxyEntity("proxy-repo", "ns", cachePvcSize: null);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.Received(1).DeletePvcIfExistsAsync("proxy-repo-cache-pvc", "ns", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProxyReconciler_DoesNotDeleteCachePvc_WhenCachePvcSizeIsSet()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new ProxyRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(), new NginxConfigRenderer(), events,
            NullLogger<ProxyRepositoryReconciler>.Instance);

        var entity = BuildProxyEntity("proxy-repo", "ns", cachePvcSize: "5Gi");

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.DidNotReceive().DeletePvcIfExistsAsync(
            "proxy-repo-cache-pvc", "ns", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProxyReconciler_DeletesPodMonitorAndMtailCm_WhenMetricsDisabled()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new ProxyRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(), new NginxConfigRenderer(), events,
            NullLogger<ProxyRepositoryReconciler>.Instance);

        var entity = BuildProxyEntity("proxy-repo", "ns", metricsEnabled: false);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.Received(1).DeleteCustomResourceIfExistsAsync(
            "monitoring.coreos.com", "v1", "podmonitors", "proxy-repo-metrics", "ns", Arg.Any<CancellationToken>());
        await resources.Received(1).DeleteResourceIfExistsAsync<V1ConfigMap>(
            "proxy-repo-mtail-cm", "ns", Arg.Any<CancellationToken>());
    }

    // ── Virtual reconciler ────────────────────────────────────────────────────

    [Fact]
    public async Task VirtualReconciler_DeletesIngress_WhenIngressDisabled()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new VirtualRepositoryReconciler(
            k8s, resources, new HtpasswdService(), events,
            NullLogger<VirtualRepositoryReconciler>.Instance);

        var entity = BuildVirtualEntity("virt-repo", "ns", ingressEnabled: false);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.Received(1).DeleteResourceIfExistsAsync<V1Ingress>(
            "virt-repo-ingress", "ns", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VirtualReconciler_DeletesHttpRoute_WhenGatewayDisabled()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new VirtualRepositoryReconciler(
            k8s, resources, new HtpasswdService(), events,
            NullLogger<VirtualRepositoryReconciler>.Instance);

        var entity = BuildVirtualEntity("virt-repo", "ns", gatewayEnabled: false);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.Received(1).DeleteCustomResourceIfExistsAsync(
            "gateway.networking.k8s.io", "v1", "httproutes", "virt-repo-route", "ns", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VirtualReconciler_DoesNotDeleteIngress_WhenIngressEnabled()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new VirtualRepositoryReconciler(
            k8s, resources, new HtpasswdService(), events,
            NullLogger<VirtualRepositoryReconciler>.Instance);

        var entity = BuildVirtualEntity("virt-repo", "ns", ingressEnabled: true);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.DidNotReceive().DeleteResourceIfExistsAsync<V1Ingress>(
            "virt-repo-ingress", "ns", Arg.Any<CancellationToken>());
    }

    // ── Ingress → Gateway transition ──────────────────────────────────────────

    [Fact]
    public async Task HostedReconciler_CleansUpIngressWhenSwitchedToGateway()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new HostedRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(), new NginxConfigRenderer(), events,
            NullLogger<HostedRepositoryReconciler>.Instance);

        // spec now has Gateway enabled (user switched from Ingress to Gateway)
        var entity = BuildHostedEntity("my-repo", "ns", gatewayEnabled: true);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        // Old ingress must be cleaned up
        await resources.Received(1).DeleteResourceIfExistsAsync<V1Ingress>(
            "my-repo-ingress", "ns", Arg.Any<CancellationToken>());

        // HTTPRoute must be ensured
        await resources.Received(1).EnsureHttpRouteAsync(
            entity, "my-repo-route", "my-repo-svc", 80,
            Arg.Any<GatewaySpec>(), "my-repo", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HostedReconciler_CleansUpHttpRouteWhenSwitchedToIngress()
    {
        var resources  = CreateResourcesMock();
        var k8s        = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events     = Substitute.For<IKubernetesEventService>();
        var reconciler = new HostedRepositoryReconciler(
            k8s, resources, new HtpasswdService(), new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(), new NginxConfigRenderer(), events,
            NullLogger<HostedRepositoryReconciler>.Instance);

        // spec now has Ingress enabled (user switched from Gateway to Ingress)
        var entity = BuildHostedEntity("my-repo", "ns", ingressEnabled: true);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        // Old HTTPRoute must be cleaned up
        await resources.Received(1).DeleteCustomResourceIfExistsAsync(
            "gateway.networking.k8s.io", "v1", "httproutes", "my-repo-route", "ns", Arg.Any<CancellationToken>());

        // Ingress must be ensured
        await resources.Received(1).EnsureIngressAsync(
            entity, "my-repo-ingress", "my-repo-svc",
            Arg.Any<IngressSpec>(), "my-repo", Arg.Any<CancellationToken>());
    }
}

