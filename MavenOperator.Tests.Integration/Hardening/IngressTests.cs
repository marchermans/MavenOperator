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
/// Integration tests verifying that Kubernetes Ingress resources are created correctly
/// when <c>spec.ingress.enabled = true</c>.
///
/// Run with: INTEGRATION_TESTS=true dotnet test MavenOperator.Tests.Integration
/// </summary>
[Collection(ClusterCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class IngressTests(ClusterFixture cluster)
{
    private HostedRepositoryReconciler BuildReconciler() =>
        new(
            cluster.Client,
            new KubernetesResourceManager(cluster.Client, NullLogger<KubernetesResourceManager>.Instance),
            new HtpasswdService(),
            new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(),
            new NginxConfigRenderer(),
            Substitute.For<IKubernetesEventService>(),
            NullLogger<HostedRepositoryReconciler>.Instance);

    private async Task<MavenRepositoryV1Alpha1> CreateRepoWithIngressAsync(
        string name,
        string? host = "maven.example.com",
        string? path = null,
        string? tlsSecret = null)
    {
        var repo = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = cluster.Namespace },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Ingress = new IngressSpec
                {
                    Enabled      = true,
                    Host         = host,
                    Path         = path,
                    TlsSecretRef = tlsSecret,
                },
            },
        };
        return await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(repo, CancellationToken.None);
    }

    [IntegrationFact]
    public async Task Ingress_IsCreated_WhenEnabled()
    {
        var name   = "ingress-basic-test";
        var entity = await CreateRepoWithIngressAsync(name, host: "maven.example.com");

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var ingress = await cluster.Client.GetAsync<V1Ingress>(
            $"{name}-ingress", cluster.Namespace, CancellationToken.None);

        ingress.ShouldNotBeNull();
        ingress.Spec!.Rules!.ShouldHaveSingleItem();
        ingress.Spec.Rules[0].Host.ShouldBe("maven.example.com");
        ingress.Spec.Rules[0].Http!.Paths!.ShouldHaveSingleItem();
        ingress.Spec.Rules[0].Http.Paths[0].Backend!.Service!.Name.ShouldBe($"{name}-svc");
        ingress.Spec.Rules[0].Http.Paths[0].PathType.ShouldBe("Prefix");
        (ingress.Spec.Tls is null || ingress.Spec.Tls.Count == 0).ShouldBeTrue("TLS should not be set when TlsSecretRef is null");
    }

    [IntegrationFact]
    public async Task Ingress_HasTls_WhenTlsSecretRefSet()
    {
        var name   = "ingress-tls-test";
        var entity = await CreateRepoWithIngressAsync(
            name, host: "maven.example.com", tlsSecret: "maven-tls");

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var ingress = await cluster.Client.GetAsync<V1Ingress>(
            $"{name}-ingress", cluster.Namespace, CancellationToken.None);

        ingress.ShouldNotBeNull();
        ingress.Spec!.Tls.ShouldNotBeNull();
        ingress.Spec.Tls.ShouldHaveSingleItem();
        ingress.Spec.Tls[0].SecretName.ShouldBe("maven-tls");
    }

    [IntegrationFact]
    public async Task Ingress_UsesDefaultPath_WhenPathIsNull()
    {
        var name   = "ingress-default-path-test";
        var entity = await CreateRepoWithIngressAsync(name, path: null);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        var ingress = await cluster.Client.GetAsync<V1Ingress>(
            $"{name}-ingress", cluster.Namespace, CancellationToken.None);

        ingress.ShouldNotBeNull();
        var path = ingress.Spec!.Rules![0].Http!.Paths![0].Path;
        path.ShouldBe($"/repository/{name}");
    }

    [IntegrationFact]
    public async Task StatusUrl_IsSetToIngressUrl_WhenEnabled()
    {
        var name   = "ingress-url-test";
        var entity = await CreateRepoWithIngressAsync(
            name, host: "maven.example.com", path: "/repository/releases");

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        entity.Status.Url.ShouldBe("http://maven.example.com/repository/releases");
    }

    [IntegrationFact]
    public async Task StatusUrl_IsSetToInternalUrl_WhenIngressDisabled()
    {
        var name = "no-ingress-url-test";
        var repo = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = cluster.Namespace },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Ingress = new IngressSpec { Enabled = false },
            },
        };
        var entity = await cluster.Client.CreateAsync<MavenRepositoryV1Alpha1>(
            repo, CancellationToken.None);

        await BuildReconciler().ReconcileAsync(entity, CancellationToken.None);

        entity.Status.Url.ShouldBe($"http://{name}-svc/repository/{name}");
    }
}



