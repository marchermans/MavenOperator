using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using MavenOperator.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace MavenOperator.Tests.Integration.Hosted;

/// <summary>
/// Integration tests for HostedRepositoryReconciler against a real Kubernetes API.
///
/// These tests validate that the reconciler actually creates/updates the expected
/// child resources (PVC, Secrets, ConfigMap, Deployment, Service) in the cluster.
/// They do NOT test that NGINX actually serves traffic — that is the E2E layer.
///
/// Run with: INTEGRATION_TESTS=true dotnet test --filter Category=Integration
/// </summary>
[Collection(ClusterCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class HostedRepositoryReconcilerTests(ClusterFixture cluster)
{
    // ── Setup helpers ──────────────────────────────────────────────────────

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

    /// <summary>
    /// Creates a MavenRepositoryV1Alpha1 in the cluster (so it has a real UID for owner
    /// references) and returns it. The CRD must be installed — the test script applies it.
    /// </summary>
    private async Task<MavenRepositoryV1Alpha1> BuildEntityAsync(
        string name,
        AuthPolicy download    = AuthPolicy.Anonymous,
        AuthPolicy upload      = AuthPolicy.Authenticated,
        List<string>? uploadRefs   = null,
        List<string>? downloadRefs = null)
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
                        Users = (downloadRefs ?? []).Select(s => new UserRef { SecretRef = s, Role = UserRole.Reader }).ToList(),
                    },
                    Upload   = new()
                    {
                        Policy = upload,
                        Users = (uploadRefs ?? []).Select(s => new UserRef { SecretRef = s, Role = UserRole.Deployer }).ToList(),
                    },
                },
            },
        };
        // Creating in the cluster assigns a real server-generated UID which is required
        // for owner references on child resources to pass Kubernetes validation.
        return await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(entity, CancellationToken.None);
    }

    // ── PVC creation ───────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_CreatesExpectedPvc_WithCorrectStorageSize()
    {
        var name = $"int-pvc-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var pvc = await cluster.Client.GetAsync<V1PersistentVolumeClaim>(
            $"{name}-pvc", cluster.Namespace, CancellationToken.None);
        pvc.ShouldNotBeNull();
        pvc!.Spec!.Resources!.Requests!["storage"].ToString().ShouldBe("1Gi");
    }

    [IntegrationFact]
    public async Task Reconcile_Pvc_HasNoOwnerReference_WhenDeletionPolicyIsRetain()
    {
        var name = $"int-retain-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);
        entity.Spec.Storage = new() { Size = "1Gi", DeletionPolicy = DeletionPolicy.Retain };

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var pvc = await cluster.Client.GetAsync<V1PersistentVolumeClaim>(
            $"{name}-pvc", cluster.Namespace, CancellationToken.None);
        pvc.ShouldNotBeNull();
        // Retain policy → no owner reference → PVC survives CRD deletion.
        // OwnerReferences is null (not empty list) when no owners are set.
        (pvc!.Metadata.OwnerReferences ?? []).ShouldBeEmpty();
    }

    // ── htpasswd Secrets ───────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_CreatesUploadHtpasswdSecret_WithBcryptContent()
    {
        var name = $"int-auth-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u1", "deployer", "supersecret");
        await cluster.CreateCredentialSecretAsync($"{name}-u2", "ci-bot",   "botpassword");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u1", $"{name}-u2"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var htpasswdSecret = await cluster.Client.GetAsync<V1Secret>(
            $"{name}-upload-htpasswd", cluster.Namespace, CancellationToken.None);
        htpasswdSecret.ShouldNotBeNull();
        var content = System.Text.Encoding.UTF8.GetString(
            htpasswdSecret!.Data!["upload.htpasswd"]);
        content.ShouldContain("deployer:$2");
        content.ShouldContain("ci-bot:$2");
    }

    [IntegrationFact]
    public async Task Reconcile_UpdatesHtpasswdSecret_WhenCredentialChanges()
    {
        var name      = $"int-rotate-{Guid.NewGuid().ToString("N")[..6]}";
        var secretRef = $"{name}-u1";
        await cluster.CreateCredentialSecretAsync(secretRef, "deployer", "old-password");
        var entity = await BuildEntityAsync(name, uploadRefs: [secretRef]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var first = (await cluster.Client.GetAsync<V1Secret>(
            $"{name}-upload-htpasswd", cluster.Namespace, CancellationToken.None))!
            .Data!["upload.htpasswd"];

        // Rotate the password in the credential Secret
        var credSecret = await cluster.Client.GetAsync<V1Secret>(
            secretRef, cluster.Namespace, CancellationToken.None);
        credSecret!.Data!["password"] = System.Text.Encoding.UTF8.GetBytes("new-password");
        await cluster.Client.UpdateAsync<V1Secret>(credSecret, CancellationToken.None);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var second = (await cluster.Client.GetAsync<V1Secret>(
            $"{name}-upload-htpasswd", cluster.Namespace, CancellationToken.None))!
            .Data!["upload.htpasswd"];

        second.ShouldNotBe(first, "htpasswd must change when password rotates");
    }

    // ── ConfigMap ──────────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_CreatesNginxConfigMap_WithValidNginxConfig()
    {
        var name = $"int-cm-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var cm = await cluster.Client.GetAsync<V1ConfigMap>(
            $"{name}-nginx-cm", cluster.Namespace, CancellationToken.None);
        cm.ShouldNotBeNull();
        cm!.Data!.ContainsKey("default.conf").ShouldBeTrue();
        cm.Data["default.conf"].ShouldContain($"/repository/{name}/");
        cm.Data["default.conf"].ShouldContain("dav_methods");
    }

    // ── Deployment ─────────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_CreatesDeployment_WithNginxImage()
    {
        var name = $"int-dep-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);
        dep.ShouldNotBeNull();
        dep!.Spec!.Template!.Spec!.Containers![0].Image.ShouldBe("nginx:1.27-alpine");
    }

    [IntegrationFact]
    public async Task Reconcile_UpdatesDeploymentAnnotation_WhenConfigChanges()
    {
        var name = $"int-hash-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, upload: AuthPolicy.Authenticated, uploadRefs: [$"{name}-u"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep1 = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);
        var hash1 = dep1!.Spec!.Template!.Metadata!.Annotations!["maven.operator.io/config-hash"];

        // Switch download policy — config changes → hash must change → rolling restart triggered
        entity.Spec.Auth.Download = new()
        {
            Policy = AuthPolicy.Authenticated,
            Users = [new UserRef { SecretRef = $"{name}-u", Role = UserRole.Reader }],
        };
        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var dep2 = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);
        var hash2 = dep2!.Spec!.Template!.Metadata!.Annotations!["maven.operator.io/config-hash"];

        hash2.ShouldNotBe(hash1, "config hash must change to trigger rolling restart");
    }

    // ── Service ────────────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_CreatesClusterIpService_ExposingPort80()
    {
        var name = $"int-svc-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var svc = await cluster.Client.GetAsync<V1Service>(
            $"{name}-svc", cluster.Namespace, CancellationToken.None);
        svc.ShouldNotBeNull();
        svc!.Spec!.Type.ShouldBe("ClusterIP");
        svc.Spec.Ports!.ShouldContain(p => p.Port == 80);
    }

    // ── Idempotency ────────────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_IsIdempotent_RunningTwiceDoesNotDuplicateResources()
    {
        var name = $"int-idem-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);

        var reconciler = BuildReconciler();
        await reconciler.ReconcileAsync(entity, CancellationToken.None);
        await reconciler.ReconcileAsync(entity, CancellationToken.None); // must not throw

        var deps = await cluster.Client.ListAsync<V1Deployment>(
            cluster.Namespace, labelSelector: $"maven.operator.io/managed-by={name}");
        deps.Count().ShouldBe(1);

        var svcs = await cluster.Client.ListAsync<V1Service>(
            cluster.Namespace, labelSelector: $"maven.operator.io/managed-by={name}");
        svcs.Count().ShouldBe(1);
    }

    // ── Status conditions ──────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_SetsExpectedConditions_InStatus()
    {
        var name = $"int-status-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        entity.Status.Conditions.ShouldContain(c => c.Type == "StorageBound" && c.Status == "True",
            "StorageBound condition must be set after reconcile");
        entity.Status.Conditions.ShouldContain(c => c.Type == "AuthReady"    && c.Status == "True",
            "AuthReady condition must be set after reconcile");
        entity.Status.Conditions.ShouldContain(c => c.Type == "Available"    && c.Status == "True",
            "Available condition must be set after reconcile");
    }

    // ── Owner references ───────────────────────────────────────────────────

    [IntegrationFact]
    public async Task Reconcile_AllChildResources_ExceptRetainPvc_HaveOwnerReference()
    {
        var name = $"int-owner-{Guid.NewGuid().ToString("N")[..6]}";
        await cluster.CreateCredentialSecretAsync($"{name}-u", "deployer", "secret");
        var entity = await BuildEntityAsync(name, uploadRefs: [$"{name}-u"]);
        // entity already has a real server-assigned UID from BuildEntityAsync

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        // PVC with DeletionPolicy=Delete → should have owner reference
        var pvc = await cluster.Client.GetAsync<V1PersistentVolumeClaim>(
            $"{name}-pvc", cluster.Namespace, CancellationToken.None);
        pvc!.Metadata.OwnerReferences.ShouldNotBeEmpty(
            "PVC with DeletionPolicy=Delete must have an owner reference");

        // ConfigMap must have owner reference
        var cm = await cluster.Client.GetAsync<V1ConfigMap>(
            $"{name}-nginx-cm", cluster.Namespace, CancellationToken.None);
        cm!.Metadata.OwnerReferences.ShouldNotBeEmpty(
            "ConfigMap must have an owner reference");

        // Deployment must have owner reference
        var dep = await cluster.Client.GetAsync<V1Deployment>(
            $"{name}-nginx", cluster.Namespace, CancellationToken.None);
        dep!.Metadata.OwnerReferences.ShouldNotBeEmpty(
            "Deployment must have an owner reference");

        // Service must have owner reference
        var svc = await cluster.Client.GetAsync<V1Service>(
            $"{name}-svc", cluster.Namespace, CancellationToken.None);
        svc!.Metadata.OwnerReferences.ShouldNotBeEmpty(
            "Service must have an owner reference");
    }
}

