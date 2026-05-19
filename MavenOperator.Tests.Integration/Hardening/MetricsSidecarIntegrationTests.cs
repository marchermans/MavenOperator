using k8s.Models;
using KubeOps.KubernetesClient;
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
/// Integration tests for Phase 6A — Deep Observability sidecar injection.
///
/// Validates that the HostedRepositoryReconciler and ProxyRepositoryReconciler
/// inject the correct sidecar containers, volumes, and Service ports into
/// child resources when <c>spec.metrics.enabled: true</c>.
///
/// These tests run against a real Kubernetes API but do NOT wait for pods to
/// become Running — they verify the Kubernetes object model only.
///
/// Run with: INTEGRATION_TESTS=true dotnet test --filter Category=Integration
/// </summary>
[Collection(ClusterCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class MetricsSidecarIntegrationTests(ClusterFixture cluster)
{
    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private ProxyRepositoryReconciler BuildProxyReconciler() =>
        new(
            cluster.Client,
            new KubernetesResourceManager(cluster.Client, NullLogger<KubernetesResourceManager>.Instance),
            new HtpasswdService(),
            new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(),
            new NginxConfigRenderer(),
            Substitute.For<IKubernetesEventService>(),
            NullLogger<ProxyRepositoryReconciler>.Instance);

    private async Task<MavenRepositoryV1Alpha1> CreateHostedEntityAsync(
        string name, bool metricsEnabled = true)
    {
        var entity = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta { Name = name, NamespaceProperty = cluster.Namespace },
            Spec       = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Metrics = new MetricsSpec { Enabled = metricsEnabled },
            },
        };
        return await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(entity, CancellationToken.None);
    }

    private async Task<MavenRepositoryV1Alpha1> CreateProxyEntityAsync(
        string name, bool metricsEnabled = true)
    {
        var entity = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta { Name = name, NamespaceProperty = cluster.Namespace },
            Spec      = new MavenRepositorySpec
            {
                Type     = RepositoryType.Proxy,
                Upstream = new UpstreamSpec
                {
                    Url      = "https://repo1.maven.org/maven2",
                    CacheTtl = "1d",
                },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },  // Proxy: no-op, but avoid CEL rejection
                },
                Metrics = new MetricsSpec { Enabled = metricsEnabled },
            },
        };
        return await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(entity, CancellationToken.None);
    }

    // ── Hosted: metrics enabled ──────────────────────────────────────────────

    [IntegrationFact]
    public async Task Hosted_MetricsEnabled_Deployment_HasThreeContainers()
    {
        var name = $"int-m6a-hd-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await CreateHostedEntityAsync(name, metricsEnabled: true);

        await BuildHostedReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);

        dep.ShouldNotBeNull();
        var containers = dep!.Spec.Template.Spec.Containers.ToList();
        containers.Count.ShouldBe(3,
            "Metrics-enabled Hosted deployment must have nginx + nginx-exporter + mtail containers");

        containers.ShouldContain(c => c.Name == "nginx",          "nginx container required");
        containers.ShouldContain(c => c.Name == "nginx-exporter", "nginx-prometheus-exporter sidecar required");
        containers.ShouldContain(c => c.Name == "mtail",          "mtail sidecar required");
    }

    [IntegrationFact]
    public async Task Hosted_MetricsEnabled_NginxExporterContainer_HasCorrectImage()
    {
        var name   = $"int-m6a-img-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await CreateHostedEntityAsync(name, metricsEnabled: true);

        await BuildHostedReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep    = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        var exporter = dep!.Spec.Template.Spec.Containers.Single(c => c.Name == "nginx-exporter");

        exporter.Image.ShouldContain("nginx-prometheus-exporter");
        exporter.Image.ShouldNotBeNullOrWhiteSpace("nginx-exporter must use the nginx/nginx-prometheus-exporter image");
    }

    [IntegrationFact]
    public async Task Hosted_MetricsEnabled_MtailContainer_HasCorrectImage()
    {
        var name   = $"int-m6a-mt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await CreateHostedEntityAsync(name, metricsEnabled: true);

        await BuildHostedReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep   = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        var mtail = dep!.Spec.Template.Spec.Containers.Single(c => c.Name == "mtail");

        mtail.Image.ShouldContain("mtail");
        mtail.Image.ShouldNotBeNullOrWhiteSpace("mtail container must use the ghcr.io/google/mtail image");
    }

    [IntegrationFact]
    public async Task Hosted_MetricsEnabled_MtailConfigMap_IsCreated()
    {
        var name   = $"int-m6a-cm-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await CreateHostedEntityAsync(name, metricsEnabled: true);

        await BuildHostedReconciler().ReconcileAsync(entity, CancellationToken.None);

        var cm = await cluster.Client.GetAsync<V1ConfigMap>(
            $"{name}-mtail-cm", cluster.Namespace, CancellationToken.None);

        cm.ShouldNotBeNull("mtail ConfigMap must be created when metrics are enabled");
        cm!.Data.ShouldContainKey("maven.mtail",
            "mtail ConfigMap must contain the maven.mtail program key");
        cm.Data["maven.mtail"].Length.ShouldBeGreaterThan(0,
            "maven.mtail program must not be empty");
    }

    [IntegrationFact]
    public async Task Hosted_MetricsEnabled_Service_HasMetricsPorts()
    {
        var name   = $"int-m6a-svc-{Guid.NewGuid().ToString("N")[..6]}";
        var spec   = new MetricsSpec { Enabled = true, ExporterPort = 9113, MtailPort = 3903 };
        var entity = await CreateHostedEntityAsync(name, metricsEnabled: true);

        await BuildHostedReconciler().ReconcileAsync(entity, CancellationToken.None);

        var svc = await cluster.Client.GetAsync<V1Service>(
            $"{name}-svc", cluster.Namespace, CancellationToken.None);

        svc.ShouldNotBeNull();
        var ports = svc!.Spec.Ports.ToList();
        ports.ShouldContain(p => p.Name == "nginx-metrics",
            "Service must expose the nginx-metrics port when metrics are enabled");
        ports.ShouldContain(p => p.Name == "mtail-metrics",
            "Service must expose the mtail-metrics port when metrics are enabled");
    }

    [IntegrationFact]
    public async Task Hosted_MetricsEnabled_Deployment_HasNginxLogsVolume()
    {
        var name   = $"int-m6a-vol-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await CreateHostedEntityAsync(name, metricsEnabled: true);

        await BuildHostedReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);

        var volumes = dep!.Spec.Template.Spec.Volumes.ToList();
        volumes.ShouldContain(v => v.Name == "nginx-logs",
            "nginx-logs emptyDir volume required to share NGINX access log with mtail");
        volumes.ShouldContain(v => v.Name == "mtail-config",
            "mtail-config volume (from ConfigMap) required to mount the mtail program");
    }

    // ── Hosted: metrics disabled ─────────────────────────────────────────────

    [IntegrationFact]
    public async Task Hosted_MetricsDisabled_Deployment_HasOnlyNginxContainer()
    {
        var name   = $"int-m6a-off-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await CreateHostedEntityAsync(name, metricsEnabled: false);

        await BuildHostedReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);

        dep!.Spec.Template.Spec.Containers.Count.ShouldBe(1,
            "Metrics-disabled Hosted deployment must have exactly one container (nginx)");
    }

    [IntegrationFact]
    public async Task Hosted_MetricsDisabled_Service_HasOnlyHttpPort()
    {
        var name   = $"int-m6a-svo-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await CreateHostedEntityAsync(name, metricsEnabled: false);

        await BuildHostedReconciler().ReconcileAsync(entity, CancellationToken.None);

        var svc = await cluster.Client.GetAsync<V1Service>(
            $"{name}-svc", cluster.Namespace, CancellationToken.None);

        var ports = svc!.Spec.Ports.ToList();
        ports.Count.ShouldBe(1, "Service with metrics disabled must expose only the http port");
        ports[0].Name.ShouldBe("http");
    }

    [IntegrationFact]
    public async Task Hosted_MetricsDisabled_MtailConfigMap_IsNotCreated()
    {
        var name   = $"int-m6a-nmt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await CreateHostedEntityAsync(name, metricsEnabled: false);

        await BuildHostedReconciler().ReconcileAsync(entity, CancellationToken.None);

        V1ConfigMap? cm = null;
        try
        {
            cm = await cluster.Client.GetAsync<V1ConfigMap>(
                $"{name}-mtail-cm", cluster.Namespace, CancellationToken.None);
        }
        catch { /* expected: not found */ }

        cm.ShouldBeNull("mtail ConfigMap must NOT be created when metrics are disabled");
    }

    // ── Proxy: metrics enabled ───────────────────────────────────────────────

    [IntegrationFact]
    public async Task Proxy_MetricsEnabled_Deployment_HasThreeContainers()
    {
        var name   = $"int-m6a-px-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await CreateProxyEntityAsync(name, metricsEnabled: true);

        await BuildProxyReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);

        dep.ShouldNotBeNull();
        var containers = dep!.Spec.Template.Spec.Containers.ToList();
        containers.Count.ShouldBe(3,
            "Metrics-enabled Proxy deployment must have nginx + nginx-exporter + mtail containers");
        containers.ShouldContain(c => c.Name == "nginx-exporter");
        containers.ShouldContain(c => c.Name == "mtail");
    }

    [IntegrationFact]
    public async Task Proxy_MetricsEnabled_MtailConfigMap_IsCreated()
    {
        var name   = $"int-m6a-pxm-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await CreateProxyEntityAsync(name, metricsEnabled: true);

        await BuildProxyReconciler().ReconcileAsync(entity, CancellationToken.None);

        var cm = await cluster.Client.GetAsync<V1ConfigMap>(
            $"{name}-mtail-cm", cluster.Namespace, CancellationToken.None);

        cm.ShouldNotBeNull("mtail ConfigMap must be created for Proxy repos with metrics enabled");
        cm!.Data.ShouldContainKey("maven.mtail");
    }

    // ── Custom sidecar image overrides ────────────────────────────────────────

    [IntegrationFact]
    public async Task Hosted_MetricsEnabled_CustomMtailImage_IsUsedInDeployment()
    {
        var name = $"int-m6a-cmt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta { Name = name, NamespaceProperty = cluster.Namespace },
            Spec       = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Metrics = new MetricsSpec
                {
                    Enabled    = true,
                    MtailImage = "ghcr.io/google/mtail:latest",
                },
            },
        };
        entity = await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(entity, CancellationToken.None);

        await BuildHostedReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep   = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        var mtail = dep!.Spec.Template.Spec.Containers.Single(c => c.Name == "mtail");

        mtail.Image.ShouldBe("ghcr.io/google/mtail:latest",
            "Custom mtail image from spec.metrics.mtailImage must be used in the Deployment");
    }

    // ── Idempotency ──────────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Hosted_MetricsEnabled_ReconcileIsIdempotent()
    {
        var name = $"int-m6a-idem-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await CreateHostedEntityAsync(name, metricsEnabled: true);
        var reconciler = BuildHostedReconciler();

        // Reconcile twice — must not throw and must produce same result
        await reconciler.ReconcileAsync(entity, CancellationToken.None);
        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);

        dep!.Spec.Template.Spec.Containers.Count.ShouldBe(3,
            "Idempotent second reconcile must not duplicate sidecar containers");
    }
}





