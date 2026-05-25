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
/// download-htpasswd Secret, proxy ConfigMap, proxy Deployment, proxy Service,
/// NGINX ConfigMap, NGINX Deployment, and external Service.
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
            NSubstitute.Substitute.For<MavenOperator.Services.IKubernetesEventService>(),
            NullLogger<VirtualRepositoryReconciler>.Instance
        );

    private async Task<MavenRepositoryV1Alpha1> BuildEntityAsync(
        string name,
        List<string> members,
        AuthPolicy downloadPolicy      = AuthPolicy.Anonymous,
        List<string>? downloadSecrets  = null)
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

    // ── Proxy and NGINX Deployments ───────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_CreatesProxyDeployment()
    {
        var name   = $"int-virt-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, members: ["releases"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var deploy = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-proxy", cluster.Namespace, CancellationToken.None);

        deploy.ShouldNotBeNull();
        var proxy = deploy.Spec!.Template.Spec!.Containers!.Single(c => c.Name == "proxy");
        var expectedImage = Environment.GetEnvironmentVariable("VIRTUAL_PROXY_IMAGE")
            ?? "ghcr.io/marchermans/maven-virtual-proxy:0.3.0-pre.1";
        proxy.Image.ShouldBe(expectedImage);
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

        var proxyDeploy = await cluster.Client.GetAsync<V1Deployment>($"{name}-proxy", ns, ct);
        AssertOwned(proxyDeploy?.Metadata.OwnerReferences, "proxy Deployment");

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

