using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using MavenOperator.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace MavenOperator.Tests.Integration.Proxy;

/// <summary>
/// Integration tests for <see cref="ProxyRepositoryReconciler"/> against a real Kubernetes API.
///
/// Validates that the reconciler creates the expected child resources
/// (download htpasswd Secret, NGINX ConfigMap, Deployment, Service) in the cluster.
/// Traffic-level validation (actual proxy caching) is the E2E layer.
///
/// Run with: INTEGRATION_TESTS=true dotnet test --filter Category=Integration
/// </summary>
[Collection(ClusterCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class ProxyRepositoryReconcilerTests(ClusterFixture cluster)
{
    // ── Setup helpers ──────────────────────────────────────────────────────

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

    // ── Anonymous download (no local auth) ────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_Anonymous_CreatesDownloadHtpasswdSecret_WithEmptyContent()
    {
        var name   = $"int-px-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var secret = await cluster.Client.GetAsync<V1Secret>(
            $"{name}-download-htpasswd", cluster.Namespace, CancellationToken.None);

        secret.ShouldNotBeNull();
        // Anonymous policy → empty htpasswd content
        var content = System.Text.Encoding.UTF8.GetString(
            secret!.Data!["download.htpasswd"]);
        content.ShouldBeEmpty();
    }

    [IntegrationFact]
    public async Task Reconcile_Anonymous_CreatesNginxConfigMap_WithProxyPassDirective()
    {
        var name   = $"int-px-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name, upstreamUrl: "https://repo1.maven.org/maven2");

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var cm = await cluster.Client.GetAsync<V1ConfigMap>(
            $"{name}-nginx-cm", cluster.Namespace, CancellationToken.None);

        cm.ShouldNotBeNull();
        var config = cm!.Data!["default.conf"];
        config.ShouldContain("proxy_pass");
        // The upstream host appears in the $upstream variable; the path goes in the rewrite target.
        config.ShouldContain("repo1.maven.org");
        config.ShouldContain("/maven2/");
        config.ShouldContain($"/repository/{name}/");
        config.ShouldNotContain("auth_basic");  // Anonymous — no local auth
    }

    [IntegrationFact]
    public async Task Reconcile_Anonymous_CreatesDeployment_WithNoCachePvc()
    {
        var name   = $"int-px-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var deploy = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);

        deploy.ShouldNotBeNull();
        // Proxy repos use emptyDir for cache — no PVC
        var vols = deploy!.Spec.Template.Spec.Volumes;
        vols.ShouldContain(v => v.EmptyDir != null, "cache volume should be emptyDir");
        vols.ShouldNotContain(v => v.PersistentVolumeClaim != null, "proxy repo must not mount a PVC");
    }

    [IntegrationFact]
    public async Task Reconcile_Anonymous_CreatesService_OnPort80()
    {
        var name   = $"int-px-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var svc = await cluster.Client.GetAsync<V1Service>(
            $"{name}-svc", cluster.Namespace, CancellationToken.None);

        svc.ShouldNotBeNull();
        svc!.Spec.Ports.ShouldContain(p => p.Port == 80);
    }

    [IntegrationFact]
    public async Task Reconcile_Anonymous_SetsOwnerReferences_OnAllChildResources()
    {
        var name   = $"int-px-{Guid.NewGuid().ToString("N")[..6]}";
        var entity = await BuildEntityAsync(name);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var uid = entity.Metadata.Uid;

        var secret = await cluster.Client.GetAsync<V1Secret>(
            $"{name}-download-htpasswd", cluster.Namespace, CancellationToken.None);
        var cm     = await cluster.Client.GetAsync<V1ConfigMap>(
            $"{name}-nginx-cm", cluster.Namespace, CancellationToken.None);
        var deploy = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);
        var svc    = await cluster.Client.GetAsync<V1Service>(
            $"{name}-svc", cluster.Namespace, CancellationToken.None);

        secret!.Metadata.OwnerReferences.ShouldContain(r => r.Uid == uid);
        cm!.Metadata.OwnerReferences.ShouldContain(r => r.Uid == uid);
        deploy!.Metadata.OwnerReferences.ShouldContain(r => r.Uid == uid);
        svc!.Metadata.OwnerReferences.ShouldContain(r => r.Uid == uid);
    }

    // ── Authenticated download ─────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_AuthenticatedDownload_CreatesPopulatedHtpasswdSecret()
    {
        var name = $"int-px-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-dl", "reader", "r3adS3cr3t!");

        // Create entity directly with download.users — CEL validation (Phase 4) requires
        // users or ciTrust when policy is Authenticated.
        var fullEntity = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new() { Name = name, NamespaceProperty = cluster.Namespace },
            Spec       = new()
            {
                Type     = RepositoryType.Proxy,
                Upstream = new() { Url = "https://repo1.maven.org/maven2", CacheTtl = "1d" },
                Auth     = new()
                {
                    Download = new()
                    {
                        Policy = AuthPolicy.Authenticated,
                        Users = [new UserRef { SecretRef = $"{name}-dl", Role = UserRole.Reader }],
                    },
                    Upload   = new() { Policy = AuthPolicy.Anonymous },
                },
            },
        };
        var created = await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(
            fullEntity, CancellationToken.None);

        await BuildReconciler().ReconcileAsync(created, CancellationToken.None);

        var secret = await cluster.Client.GetAsync<V1Secret>(
            $"{name}-download-htpasswd", cluster.Namespace, CancellationToken.None);

        secret.ShouldNotBeNull();
        var content = System.Text.Encoding.UTF8.GetString(secret!.Data!["download.htpasswd"]);
        content.ShouldNotBeEmpty();
        content.ShouldContain("reader");
    }

    [IntegrationFact]
    public async Task Reconcile_AuthenticatedDownload_ConfigMapContainsAuthBasic()
    {
        var name = $"int-px-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-dl", "reader", "r3adS3cr3t!");

        var fullEntity = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new() { Name = name, NamespaceProperty = cluster.Namespace },
            Spec       = new()
            {
                Type     = RepositoryType.Proxy,
                Upstream = new() { Url = "https://repo1.maven.org/maven2", CacheTtl = "1d" },
                Auth     = new()
                {
                    Download = new()
                    {
                        Policy = AuthPolicy.Authenticated,
                        Users = [new UserRef { SecretRef = $"{name}-dl", Role = UserRole.Reader }],
                    },
                    Upload   = new() { Policy = AuthPolicy.Anonymous },
                },
            },
        };
        var created = await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(
            fullEntity, CancellationToken.None);

        await BuildReconciler().ReconcileAsync(created, CancellationToken.None);

        var cm = await cluster.Client.GetAsync<V1ConfigMap>(
            $"{name}-nginx-cm", cluster.Namespace, CancellationToken.None);

        cm!.Data!["default.conf"].ShouldContain("auth_basic");
        cm.Data["default.conf"].ShouldContain("download.htpasswd");
    }

    // ── Upstream auth ──────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_UpstreamAuth_ConfigMapContainsProxySetHeaderAuthorization()
    {
        var name = $"int-px-{Guid.NewGuid().ToString("N")[..6]}";
        // Create the upstream credential secret
        await cluster.CreateCredentialSecretAsync($"{name}-up", "nexus-user", "nexusPw!");

        var fullEntity = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new() { Name = name, NamespaceProperty = cluster.Namespace },
            Spec       = new()
            {
                Type     = RepositoryType.Proxy,
                Upstream = new()
                {
                    Url      = "https://nexus.corp.example.com/repository/maven-central",
                    CacheTtl = "1d",
                    Auth     = new() { SecretRef = $"{name}-up" },
                },
                Auth = new()
                {
                    Download = new() { Policy = AuthPolicy.Anonymous },
                    Upload   = new() { Policy = AuthPolicy.Anonymous },
                },
            },
        };
        var created = await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(
            fullEntity, CancellationToken.None);

        await BuildReconciler().ReconcileAsync(created, CancellationToken.None);

        var cm = await cluster.Client.GetAsync<V1ConfigMap>(
            $"{name}-nginx-cm", cluster.Namespace, CancellationToken.None);

        cm.ShouldNotBeNull();
        var config = cm!.Data!["default.conf"];
        config.ShouldContain("proxy_set_header Authorization");
        config.ShouldContain("Basic ");  // base64-encoded header
    }

    // ── Idempotency ───────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_IsIdempotent_SecondCallDoesNotError()
    {
        var name      = $"int-px-{Guid.NewGuid().ToString("N")[..6]}";
        var entity    = await BuildEntityAsync(name);
        var reconciler = BuildReconciler();

        // First reconcile
        await reconciler.ReconcileAsync(entity, CancellationToken.None);
        // Second reconcile must not throw
        var ex = await Record.ExceptionAsync(
            () => reconciler.ReconcileAsync(entity, CancellationToken.None));
        ex.ShouldBeNull();
    }

    // ── Error cases ───────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_Throws_WhenUpstreamSpecIsMissing()
    {
        // Build an in-memory entity that bypasses API validation — CEL rules in the CRD
        // would reject this at admission time (Phase 4), so we test the reconciler guard directly.
        var entity = new MavenRepositoryV1Alpha1
        {
            Metadata = new()
            {
                Name              = $"int-px-{Guid.NewGuid().ToString("N")[..6]}",
                NamespaceProperty = cluster.Namespace,
                Uid               = Guid.NewGuid().ToString(),
            },
            Spec = new()
            {
                Type     = RepositoryType.Proxy,
                Upstream = null,          // intentionally missing — guarded by reconciler
                Auth     = new(),
            },
        };

        // The reconciler must guard against this even if the API allows it (defence-in-depth).
        await Should.ThrowAsync<InvalidOperationException>(
            () => BuildReconciler().ReconcileAsync(entity, CancellationToken.None));
    }
}


