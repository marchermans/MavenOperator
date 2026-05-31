using k8s.Models;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace MavenOperator.Tests.Unit.Services;

/// <summary>
/// Unit tests for the Ingress CertManager and Annotations features.
/// </summary>
public sealed class IngressSpecTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MavenRepositoryV1Alpha1 BuildHostedEntity(
        string name,
        IngressSpec ingress)
        => new()
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta { Name = name, NamespaceProperty = "test-ns" },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Ingress = ingress,
            },
        };

    private static (HostedRepositoryReconciler reconciler, IKubernetesResourceManager resources)
        BuildReconcilerWithMocks()
    {
        var resources = Substitute.For<IKubernetesResourceManager>();
        var k8s       = Substitute.For<KubeOps.KubernetesClient.IKubernetesClient>();
        var events    = Substitute.For<IKubernetesEventService>();
        var nginx     = new NginxConfigRenderer();

        // Wire up common mocks
        resources.EnsurePvcAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new V1PersistentVolumeClaim());
        resources.EnsureSecretAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new V1Secret());
        resources.EnsureConfigMapAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new V1ConfigMap());
        resources.EnsureDeploymentAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<V1PodSpec>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new V1Deployment());
        resources.EnsureServiceWithPortsAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<IList<V1ServicePort>>(), Arg.Any<CancellationToken>())
            .Returns(new V1Service());
        resources.EnsurePodMonitorAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<MetricsSpec>(), Arg.Any<CancellationToken>())
            .Returns(false);
        resources.EnsureIngressAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<IngressSpec>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new V1Ingress());
        resources.EnsureCertificateAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CertManagerSpec>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var reconciler = new HostedRepositoryReconciler(
            k8s,
            resources,
            new HtpasswdService(),
            new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(),
            nginx,
            events,
            NullLogger<HostedRepositoryReconciler>.Instance);

        return (reconciler, resources);
    }

    // ── CertManager tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task Reconciler_CallsEnsureCertificate_WhenIngressCertManagerConfigured()
    {
        var (reconciler, resources) = BuildReconcilerWithMocks();
        var certManager = new CertManagerSpec
        {
            IssuerName      = "letsencrypt-staging",
            IsClusterIssuer = true,
        };
        var entity = BuildHostedEntity("repo-cert", new IngressSpec
        {
            Enabled     = true,
            Host        = "maven.example.com",
            CertManager = certManager,
        });

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.Received(1).EnsureCertificateAsync(
            entity,
            "repo-cert-ingress-cert",
            "maven.example.com",
            certManager,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconciler_DoesNotCallEnsureCertificate_WhenIngressCertManagerIsNull()
    {
        var (reconciler, resources) = BuildReconcilerWithMocks();
        var entity = BuildHostedEntity("repo-no-cert", new IngressSpec
        {
            Enabled      = true,
            Host         = "maven.example.com",
            TlsSecretRef = "my-tls-secret",
            CertManager  = null,
        });

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.DidNotReceive().EnsureCertificateAsync(
            Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CertManagerSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconciler_SetsHttpsScheme_WhenIngressCertManagerConfigured()
    {
        var (reconciler, _) = BuildReconcilerWithMocks();
        var entity = BuildHostedEntity("repo-https", new IngressSpec
        {
            Enabled     = true,
            Host        = "maven.example.com",
            Path        = "/repository/releases",
            CertManager = new CertManagerSpec { IssuerName = "letsencrypt", AutoCreate = true },
        });

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        entity.Status.Url.ShouldBe("https://maven.example.com/repository/releases");
    }

    [Fact]
    public async Task Reconciler_SetsHttpScheme_WhenNoTlsConfigured()
    {
        var (reconciler, _) = BuildReconcilerWithMocks();
        var entity = BuildHostedEntity("repo-http", new IngressSpec
        {
            Enabled = true,
            Host    = "maven.example.com",
            Path    = "/repository/releases",
        });

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        entity.Status.Url.ShouldBe("http://maven.example.com/repository/releases");
    }

    [Fact]
    public async Task Reconciler_UsesFallbackHostname_WhenHostIsNull_ForCertificate()
    {
        var (reconciler, resources) = BuildReconcilerWithMocks();
        var certManager = new CertManagerSpec { IssuerName = "letsencrypt" };
        var entity = BuildHostedEntity("repo-no-host", new IngressSpec
        {
            Enabled     = true,
            Host        = null,
            CertManager = certManager,
        });

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        // Falls back to repo name when host is null
        await resources.Received(1).EnsureCertificateAsync(
            entity,
            "repo-no-host-ingress-cert",
            "repo-no-host",
            certManager,
            Arg.Any<CancellationToken>());
    }

    // ── Annotations tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task Reconciler_PassesAnnotations_InIngressSpec_ToEnsureIngress()
    {
        var (reconciler, resources) = BuildReconcilerWithMocks();
        var annotations = new Dictionary<string, string>
        {
            ["nginx.ingress.kubernetes.io/rewrite-target"] = "/",
            ["nginx.ingress.kubernetes.io/proxy-body-size"] = "0",
        };
        var ingressSpec = new IngressSpec
        {
            Enabled     = true,
            Host        = "maven.example.com",
            Annotations = annotations,
        };
        var entity = BuildHostedEntity("repo-annotated", ingressSpec);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        // Verify EnsureIngressAsync is called with the spec that carries annotations
        await resources.Received(1).EnsureIngressAsync(
            entity,
            "repo-annotated-ingress",
            "repo-annotated-svc",
            Arg.Is<IngressSpec>(s => s.Annotations.ContainsKey("nginx.ingress.kubernetes.io/rewrite-target")),
            "repo-annotated",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconciler_WorksCorrectly_WhenAnnotationsAreEmpty()
    {
        var (reconciler, resources) = BuildReconcilerWithMocks();
        var entity = BuildHostedEntity("repo-no-annotations", new IngressSpec
        {
            Enabled     = true,
            Host        = "maven.example.com",
            Annotations = new Dictionary<string, string>(),
        });

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        await resources.Received(1).EnsureIngressAsync(
            entity,
            "repo-no-annotations-ingress",
            Arg.Any<string>(),
            Arg.Any<IngressSpec>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }
}

