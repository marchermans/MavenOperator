using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace MavenOperator.Tests.Unit.Services;

public sealed class KubernetesResourceManagerIngressTests
{
    [Fact]
    public async Task EnsureIngressAsync_AddsClusterIssuerAnnotation_WhenIngressCertManagerUsesClusterIssuer()
    {
        var client = Substitute.For<IKubernetesClient>();
        var sut = new KubernetesResourceManager(client, NullLogger<KubernetesResourceManager>.Instance);
        var owner = BuildHostedEntity("repo", "test-ns");
        var ingressSpec = new IngressSpec
        {
            Enabled = true,
            Host = "maven.example.com",
            CertManager = new CertManagerSpec
            {
                IssuerName = "letsencrypt-prod",
                IsClusterIssuer = true,
            },
        };

        client.GetAsync<V1Ingress>("repo-ingress", "test-ns", Arg.Any<CancellationToken>())
            .Returns((V1Ingress?)null);

        V1Ingress? createdIngress = null;
        client.CreateAsync(Arg.Do<V1Ingress>(ing => createdIngress = ing), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<V1Ingress>());

        var result = await sut.EnsureIngressAsync(
            owner,
            "repo-ingress",
            "repo-svc",
            ingressSpec,
            "repo",
            CancellationToken.None);

        result.ShouldNotBeNull();
        createdIngress.ShouldNotBeNull();
        createdIngress.Metadata.ShouldNotBeNull();
        createdIngress.Metadata.Annotations.ShouldNotBeNull();
        createdIngress.Metadata.Annotations.ShouldContainKey("cert-manager.io/cluster-issuer");
        createdIngress.Metadata.Annotations["cert-manager.io/cluster-issuer"].ShouldBe("letsencrypt-prod");
        createdIngress.Metadata.Annotations.ShouldNotContainKey("cert-manager.io/issuer");
        createdIngress.Spec.ShouldNotBeNull();
        createdIngress.Spec.Tls.ShouldNotBeNull();
        createdIngress.Spec.Tls.ShouldHaveSingleItem();
        createdIngress.Spec.Tls[0].SecretName.ShouldBe("repo-ingress-tls");
    }

    [Fact]
    public async Task EnsureIngressAsync_AddsNamespacedIssuerAnnotation_WhenIngressCertManagerUsesIssuer()
    {
        var client = Substitute.For<IKubernetesClient>();
        var sut = new KubernetesResourceManager(client, NullLogger<KubernetesResourceManager>.Instance);
        var owner = BuildHostedEntity("repo", "test-ns");
        var ingressSpec = new IngressSpec
        {
            Enabled = true,
            Host = "maven.example.com",
            CertManager = new CertManagerSpec
            {
                IssuerName = "repo-issuer",
                IsClusterIssuer = false,
            },
        };

        client.GetAsync<V1Ingress>("repo-ingress", "test-ns", Arg.Any<CancellationToken>())
            .Returns((V1Ingress?)null);

        V1Ingress? createdIngress = null;
        client.CreateAsync(Arg.Do<V1Ingress>(ing => createdIngress = ing), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<V1Ingress>());

        await sut.EnsureIngressAsync(
            owner,
            "repo-ingress",
            "repo-svc",
            ingressSpec,
            "repo",
            CancellationToken.None);

        createdIngress.ShouldNotBeNull();
        createdIngress.Metadata!.Annotations.ShouldNotBeNull();
        createdIngress.Metadata.Annotations.ShouldContainKey("cert-manager.io/issuer");
        createdIngress.Metadata.Annotations["cert-manager.io/issuer"].ShouldBe("repo-issuer");
        createdIngress.Metadata.Annotations.ShouldNotContainKey("cert-manager.io/cluster-issuer");
    }

    [Fact]
    public async Task EnsureIngressAsync_RecreatesIngress_WhenOnlyCertManagerAnnotationChanges()
    {
        var client = Substitute.For<IKubernetesClient>();
        var sut = new KubernetesResourceManager(client, NullLogger<KubernetesResourceManager>.Instance);
        var owner = BuildHostedEntity("repo", "test-ns");
        var existing = new V1Ingress
        {
            Metadata = new V1ObjectMeta
            {
                Name = "repo-ingress",
                NamespaceProperty = "test-ns",
                Annotations = new Dictionary<string, string>
                {
                    ["cert-manager.io/cluster-issuer"] = "old-issuer",
                },
            },
            Spec = new V1IngressSpec
            {
                Rules =
                [
                    new V1IngressRule
                    {
                        Host = "maven.example.com",
                        Http = new V1HTTPIngressRuleValue
                        {
                            Paths =
                            [
                                new V1HTTPIngressPath
                                {
                                    Path = "/repository/repo",
                                    PathType = "Prefix",
                                    Backend = new V1IngressBackend
                                    {
                                        Service = new V1IngressServiceBackend
                                        {
                                            Name = "repo-svc",
                                            Port = new V1ServiceBackendPort { Number = 80 },
                                        },
                                    },
                                },
                            ],
                        },
                    },
                ],
                Tls =
                [
                    new V1IngressTLS
                    {
                        Hosts = ["maven.example.com"],
                        SecretName = "repo-ingress-tls",
                    },
                ],
            },
        };

        client.GetAsync<V1Ingress>("repo-ingress", "test-ns", Arg.Any<CancellationToken>())
            .Returns(existing, (V1Ingress?)null);

        client.CreateAsync(Arg.Any<V1Ingress>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<V1Ingress>());

        var ingressSpec = new IngressSpec
        {
            Enabled = true,
            Host = "maven.example.com",
            CertManager = new CertManagerSpec
            {
                IssuerName = "new-issuer",
                IsClusterIssuer = true,
            },
        };

        await sut.EnsureIngressAsync(
            owner,
            "repo-ingress",
            "repo-svc",
            ingressSpec,
            "repo",
            CancellationToken.None);

        await client.Received(1).DeleteAsync<V1Ingress>("repo-ingress", "test-ns", Arg.Any<CancellationToken>());
        await client.Received(1).CreateAsync(
            Arg.Is<V1Ingress>(ing => ing.Metadata!.Annotations!["cert-manager.io/cluster-issuer"] == "new-issuer"),
            Arg.Any<CancellationToken>());
    }

    private static MavenRepositoryV1Alpha1 BuildHostedEntity(string name, string ns)
        => new()
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind = "MavenRepository",
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                Uid = "uid-123",
            },
            Spec = new MavenRepositorySpec
            {
                Type = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Ingress = new IngressSpec { Enabled = true },
            },
        };
}

