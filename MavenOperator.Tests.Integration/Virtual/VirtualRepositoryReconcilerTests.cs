using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using MavenOperator.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace MavenOperator.Tests.Integration.Virtual;

/// <summary>
/// Integration tests for VirtualRepositoryReconciler against a real Kubernetes API.
///
/// These tests validate that the reconciler creates/updates all expected child resources:
/// download-htpasswd Secret, proxy ConfigMap, NGINX ConfigMap, single merged
/// Deployment (nginx main container + C# proxy sidecar), proxy status Service,
/// and external Service.
///
/// Run with: INTEGRATION_TESTS=true dotnet test --filter Category=Integration
/// </summary>
[Collection(ClusterCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class VirtualRepositoryReconcilerTests(ClusterFixture cluster)
{
    // ── Setup helpers ──────────────────────────────────────────────────────────

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
        AuthPolicy downloadPolicy      = AuthPolicy.Anonymous,
        List<string>? downloadSecrets  = null,
        string? pathPrefix = null,
        bool metricsEnabled = false)
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
                Type    = RepositoryType.Virtual,
                PathPrefix = pathPrefix,
                Virtual = new() { Members = members, MetadataCacheTtlSeconds = 60 },
                Auth    = new()
                {
                    Download = new()
                    {
                        Policy     = downloadPolicy,
                        Users = (downloadSecrets ?? []).Select(s => new UserRef { SecretRef = s, Role = UserRole.Reader }).ToList(),
                    },
                    Upload = new() { Policy = AuthPolicy.Anonymous },
                },
                Metrics = new() { Enabled = metricsEnabled },
            },
        };

        return await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(entity, CancellationToken.None);
    }

    // ── Download htpasswd Secret ───────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_Anonymous_CreatesEmptyDownloadHtpasswdSecret()
    {
        var name   = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var secret = await cluster.Client.GetAsync<V1Secret>(
            $"{name}-download-htpasswd", cluster.Namespace, CancellationToken.None);

        secret.ShouldNotBeNull();
        // Anonymous → empty htpasswd
        var content = System.Text.Encoding.UTF8.GetString(secret.Data!["download.htpasswd"]);
        content.ShouldBeEmpty();
    }

    [IntegrationFact]
    public async Task Reconcile_AuthenticatedDownload_CreatesHtpasswdSecretWithUsers()
    {
        var name = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-dl", "viewer", "pass1");

        var entity = await BuildEntityAsync(
            name,
            members: ["releases"],
            downloadPolicy: AuthPolicy.Authenticated,
            downloadSecrets: [$"{name}-dl"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var secret = await cluster.Client.GetAsync<V1Secret>(
            $"{name}-download-htpasswd", cluster.Namespace, CancellationToken.None);

        secret.ShouldNotBeNull();
        var content = System.Text.Encoding.UTF8.GetString(secret.Data!["download.htpasswd"]);
        content.ShouldContain("viewer:");
    }

    // ── Proxy ConfigMap ───────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_CreatesProxyConfigMap_WithMemberUrls()
    {
        var name   = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases", "snapshots"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var cm = await cluster.Client.GetAsync<V1ConfigMap>(
            $"{name}-proxy-cm", cluster.Namespace, CancellationToken.None);

        cm.ShouldNotBeNull();
        cm.Data.ShouldContainKey("appsettings.json");
        var json = cm.Data["appsettings.json"];
        json.ShouldContain("releases");
        json.ShouldContain("snapshots");
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesProxyConfigMap_UsingMemberPathPrefixes()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var memberRoot = $"member-root-{suffix}";
        var memberNested = $"member-nested-{suffix}";
        var name = $"int-virt-{suffix}";

        await cluster.Client.CreateAsync(new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind = "MavenRepository",
            Metadata = new V1ObjectMeta { Name = memberRoot, NamespaceProperty = cluster.Namespace },
            Spec = new MavenRepositorySpec
            {
                Type = RepositoryType.Hosted,
                PathPrefix = "/",
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
            },
        }, CancellationToken.None);

        await cluster.Client.CreateAsync(new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind = "MavenRepository",
            Metadata = new V1ObjectMeta { Name = memberNested, NamespaceProperty = cluster.Namespace },
            Spec = new MavenRepositorySpec
            {
                Type = RepositoryType.Hosted,
                PathPrefix = "/maven/member-two",
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
            },
        }, CancellationToken.None);

        var entity = await BuildEntityAsync(name, members: [memberRoot, memberNested]);
        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var cm = await cluster.Client.GetAsync<V1ConfigMap>($"{name}-proxy-cm", cluster.Namespace, CancellationToken.None);
        cm.ShouldNotBeNull();
        var json = cm.Data!["appsettings.json"];
        json.ShouldContain($"\"baseUrl\": \"http://{memberRoot}-svc\"");
        json.ShouldContain($"\"baseUrl\": \"http://{memberNested}-svc/maven/member-two\"");
    }

    [IntegrationFact]
    public async Task Reconcile_UsesVirtualPathPrefix_ForNginxConfigAndStatusUrl()
    {
        var name = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"], pathPrefix: "/");

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var cm = await cluster.Client.GetAsync<V1ConfigMap>($"{name}-nginx-cm", cluster.Namespace, CancellationToken.None);
        cm.ShouldNotBeNull();
        var conf = cm.Data!["default.conf"];
        // Root path prefix renders as a plain `location /` block (the per-request
        // upstream URL is computed by the http-context map, not regex rewrites).
        conf.ShouldContain("location / {");
        conf.ShouldNotContain($"/repository/{name}/");
        entity.Status.Url.ShouldBe($"http://{name}-svc");
    }

    // ── Proxy and NGINX Deployments ───────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_CreatesSingleMergedDeployment_WithNginxAndProxySidecar()
    {
        var name   = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"], metricsEnabled: false);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        // Exactly one deployment: nginx (main) + proxy (sidecar), 2 containers when metrics disabled.
        var deploy = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);

        deploy.ShouldNotBeNull();
        var containers = deploy.Spec!.Template.Spec!.Containers!;
        containers.Count.ShouldBe(2, "Virtual pod without metrics: 2 containers (nginx + proxy)");

        var proxy = containers.Single(c => c.Name == "proxy");
        var expectedImage = Environment.GetEnvironmentVariable("VIRTUAL_PROXY_IMAGE")
            ?? "ghcr.io/marchermans/maven-virtual-proxy:0.3.0-pre.1";
        proxy.Image.ShouldBe(expectedImage);

        // Per-component hash annotations on the pod template (same pattern as hosted repos).
        var podAnnotations = deploy.Spec!.Template.Metadata.Annotations!;
        podAnnotations["maven.operator.io/nginx-hash"].ShouldMatch("^[0-9a-f]{16}$");
        podAnnotations["maven.operator.io/proxy-config-hash"].ShouldMatch("^[0-9a-f]{16}$");

        // The legacy standalone proxy Deployment must not be created.
        var legacy = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-proxy", cluster.Namespace, CancellationToken.None);
        legacy.ShouldBeNull();
    }

    [IntegrationFact]
    public async Task Reconcile_DeletesLegacyProxyDeployment_OnMigration()
    {
        var name   = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);

        // Simulate a pre-consolidation repo: the standalone proxy Deployment exists.
        var legacyDeploy = new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = $"{name}-proxy", NamespaceProperty = cluster.Namespace },
            Spec     = new V1DeploymentSpec
            {
                Replicas = 0,
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = $"{name}-proxy" } },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string> { ["app"] = $"{name}-proxy" } },
                    Spec     = new V1PodSpec { Containers = [new V1Container { Name = "legacy-proxy", Image = "nginx:alpine" }] },
                },
            },
        };
        await cluster.Client.CreateAsync(legacyDeploy, CancellationToken.None);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        // The obsolete standalone Deployment is deleted one-shot; the merged one exists.
        var legacy   = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-proxy", cluster.Namespace, CancellationToken.None);
        var merged   = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);

        legacy.ShouldBeNull();
        merged.ShouldNotBeNull();
    }

    // ── Metrics sidecars ──────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_MetricsEnabled_Deployment_HasFourContainers()
    {
        var name = $"int-virt-m6a-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"], metricsEnabled: true);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var deploy = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        deploy.ShouldNotBeNull();
        var containers = deploy.Spec!.Template.Spec!.Containers!.ToList();
        containers.Count.ShouldBe(4, "Metrics-enabled Virtual pod: 4 containers (nginx + proxy + nginx-exporter + mtail)");

        containers.ShouldContain(c => c.Name == "nginx", "nginx container required");
        containers.ShouldContain(c => c.Name == "proxy", "proxy sidecar required");
        containers.ShouldContain(c => c.Name == "nginx-exporter", "nginx-prometheus-exporter sidecar required");
        containers.ShouldContain(c => c.Name == "mtail", "mtail sidecar required");

        // Verify proxy-sidecar port 8080 is still present
        var proxyContainer = containers.Single(c => c.Name == "proxy");
        proxyContainer.Ports.ShouldContain(p => p.ContainerPort == 8080, "proxy sidecar must expose port 8080");

        // Verify metrics containers have correct ports
        var exporter = containers.Single(c => c.Name == "nginx-exporter");
        exporter.Ports.ShouldContain(p => p.ContainerPort == 9113, "nginx-exporter must expose port 9113");

        var mtail = containers.Single(c => c.Name == "mtail");
        mtail.Ports.ShouldContain(p => p.ContainerPort == 3903, "mtail must expose port 3903");
    }

    [IntegrationFact]
    public async Task Reconcile_MetricsEnabled_MtailConfigMap_IsCreated()
    {
        var name = $"int-virt-m6a-cm-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"], metricsEnabled: true);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var cm = await cluster.Client.GetAsync<V1ConfigMap>($"{name}-mtail-cm", cluster.Namespace, CancellationToken.None);
        cm.ShouldNotBeNull("mtail ConfigMap must be created when metrics are enabled");
        cm!.Data.ShouldContainKey("maven.mtail", "mtail ConfigMap must contain the maven.mtail program key");
        cm.Data["maven.mtail"].Length.ShouldBeGreaterThan(0, "maven.mtail program must not be empty");
    }

    [IntegrationFact]
    public async Task Reconcile_MetricsEnabled_Service_HasMetricsPorts()
    {
        var name = $"int-virt-m6a-svc-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"], metricsEnabled: true);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var svc = await cluster.Client.GetAsync<V1Service>($"{name}-svc", cluster.Namespace, CancellationToken.None);
        svc.ShouldNotBeNull();
        var ports = svc!.Spec.Ports.ToList();
        ports.ShouldContain(p => p.Name == "nginx-metrics", "Service must expose the nginx-metrics port when metrics are enabled");
        ports.ShouldContain(p => p.Name == "mtail-metrics", "Service must expose the mtail-metrics port when metrics are enabled");
    }

    [IntegrationFact]
    public async Task Reconcile_MetricsEnabled_Deployment_HasNginxLogsVolume()
    {
        var name = $"int-virt-m6a-vol-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"], metricsEnabled: true);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var deploy = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
        var volumes = deploy!.Spec.Template.Spec.Volumes.ToList();
        volumes.ShouldContain(v => v.Name == "nginx-logs", "nginx-logs emptyDir volume required to share NGINX access log with mtail");
        volumes.ShouldContain(v => v.Name == "mtail-config", "mtail-config volume (from ConfigMap) required to mount the mtail program");
    }

    [IntegrationFact]
    public async Task Reconcile_MetricsDisabled_Service_HasOnlyHttpPort()
    {
        var name = $"int-virt-m6a-svo-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"], metricsEnabled: false);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var svc = await cluster.Client.GetAsync<V1Service>($"{name}-svc", cluster.Namespace, CancellationToken.None);
        var ports = svc!.Spec.Ports.ToList();
        ports.Count.ShouldBe(1, "Service with metrics disabled must expose only the http port");
        ports[0].Name.ShouldBe("http");
    }

    [IntegrationFact]
    public async Task Reconcile_DisablingMetrics_CleansUpMtailResources()
    {
        var name = $"int-virt-m6a-clean-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"], metricsEnabled: true);
        var reconciler = BuildReconciler();

        // First run with metrics enabled
        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        // Fetch mtail ConfigMap (should exist)
        var cm = await cluster.Client.GetAsync<V1ConfigMap>($"{name}-mtail-cm", cluster.Namespace, CancellationToken.None);
        cm.ShouldNotBeNull("mtail ConfigMap must exist after first reconcile with metrics enabled");

        // Re-fetch entity (resourceVersion may change)
        entity = await cluster.Client.GetAsync<MavenRepositoryV1Alpha1>(name, cluster.Namespace, CancellationToken.None)
            ?? throw new InvalidOperationException("Entity disappeared");

        // Second run with metrics disabled
        entity.Spec.Metrics = new() { Enabled = false };
        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        // mtail ConfigMap should be deleted
        cm = await cluster.Client.GetAsync<V1ConfigMap>($"{name}-mtail-cm", cluster.Namespace, CancellationToken.None);
        cm.ShouldBeNull("mtail ConfigMap must be deleted when metrics is disabled");
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesNginxDeployment()
    {
        var name   = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var deploy = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);

        deploy.ShouldNotBeNull();
        deploy.Spec!.Template.Spec!.Containers!.ShouldContain(c => c.Name == "nginx");
    }

    // ── Services ──────────────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_CreatesProxyInternalService()
    {
        var name   = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var svc = await cluster.Client.GetAsync<V1Service>(
            $"{name}-proxy-svc", cluster.Namespace, CancellationToken.None);

        svc.ShouldNotBeNull();
        svc.Spec!.Ports!.ShouldContain(p => p.Port == 8080);
    }

    [IntegrationFact]
    public async Task Reconcile_CreatesNginxExternalService()
    {
        var name   = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var svc = await cluster.Client.GetAsync<V1Service>(
            $"{name}-svc", cluster.Namespace, CancellationToken.None);

        svc.ShouldNotBeNull();
        svc.Spec!.Ports!.ShouldContain(p => p.Port == 80);
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_RunTwice_IsIdempotent()
    {
        var name   = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);
        var reconciler = BuildReconciler();

        // First run
        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        // Re-fetch entity (resourceVersion may change)
        entity = await cluster.Client.GetAsync<MavenRepositoryV1Alpha1>(name, cluster.Namespace, CancellationToken.None)
            ?? throw new InvalidOperationException("Entity disappeared");

        // Second run — must not throw
        await Should.NotThrowAsync(() => reconciler.ReconcileAsync(entity, CancellationToken.None));
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_EmptyMembersList_ThrowsInvalidOperation()
    {
        // Build an in-memory entity — CEL validation in the CRD (Phase 4) would reject
        // creation of a Virtual repo with 0 members. We test the reconciler guard directly
        // (defence-in-depth) without going through the Kubernetes API.
        var entity = new MavenRepositoryV1Alpha1
        {
            Metadata = new k8s.Models.V1ObjectMeta
            {
                Name              = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}",
                NamespaceProperty = cluster.Namespace,
                Uid               = Guid.NewGuid().ToString(),
            },
            Spec = new()
            {
                Type    = RepositoryType.Virtual,
                Virtual = new() { Members = [], MetadataCacheTtlSeconds = 60 },
                Auth    = new()
                {
                    Download = new() { Policy = AuthPolicy.Anonymous },
                    Upload   = new() { Policy = AuthPolicy.Anonymous },
                },
            },
        };

        await Should.ThrowAsync<InvalidOperationException>(
            () => BuildReconciler().ReconcileAsync(entity, CancellationToken.None));
    }

    // ── Owner references ──────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_AllChildResources_HaveOwnerReference()
    {
        var name   = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        void AssertOwned(IList<V1OwnerReference>? refs, string resourceName)
        {
            refs.ShouldNotBeNull($"{resourceName} has no owner references");
            refs!.ShouldContain(
                r => r.Name == name,
                $"{resourceName} is not owned by {name}");
        }

        var ns = cluster.Namespace;
        var ct = CancellationToken.None;

        var secret = await cluster.Client.GetAsync<V1Secret>($"{name}-download-htpasswd", ns, ct);
        AssertOwned(secret?.Metadata.OwnerReferences, "download-htpasswd Secret");

        var proxyCm = await cluster.Client.GetAsync<V1ConfigMap>($"{name}-proxy-cm", ns, ct);
        AssertOwned(proxyCm?.Metadata.OwnerReferences, "proxy ConfigMap");

        var nginxCm = await cluster.Client.GetAsync<V1ConfigMap>($"{name}-nginx-cm", ns, ct);
        AssertOwned(nginxCm?.Metadata.OwnerReferences, "nginx ConfigMap");



        var nginxDeploy = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", ns, ct);
        AssertOwned(nginxDeploy?.Metadata.OwnerReferences, "nginx Deployment");

        var svc = await cluster.Client.GetAsync<V1Service>($"{name}-svc", ns, ct);
        AssertOwned(svc?.Metadata.OwnerReferences, "nginx external Service");
    }

    // ── Status ────────────────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_SetsAvailableConditionTrue()
    {
        var name   = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var available = entity.Status.Conditions.FirstOrDefault(c => c.Type == "Available");
        available.ShouldNotBeNull();
        available!.Status.ShouldBe("True");
    }
}

