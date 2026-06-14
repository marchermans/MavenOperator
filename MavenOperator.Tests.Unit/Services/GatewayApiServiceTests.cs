using MavenOperator.Entities.Spec;
using MavenOperator.Services;
using Shouldly;

namespace MavenOperator.Tests.Unit.Services;

public sealed class GatewayApiServiceTests
{
    private readonly IGatewayApiService sut = new GatewayApiService();

    [Fact]
    public void BuildHttpRoute_WithExtensionRefs_AddsExtensionRefFilters()
    {
        var gatewaySpec = new GatewaySpec
        {
            Enabled = true,
            GatewayRef = new GatewayRefSpec { Name = "public-gw" },
            ExtensionRefs =
            [
                new GatewayExtensionRefSpec
                {
                    Group = "traefik.io",
                    Kind = "Middleware",
                    Name = "waf-crs",
                },
            ],
        };

        var result = sut.BuildHttpRoute(
            "repo-route",
            "default",
            "repo-svc",
            80,
            gatewaySpec,
            "repo");

        var spec = result["spec"].ShouldBeOfType<Dictionary<string, object?>>();
        var rules = spec["rules"].ShouldBeOfType<List<Dictionary<string, object?>>>();
        var firstRule = rules.Single();
        firstRule.ContainsKey("filters").ShouldBeTrue();

        var filters = firstRule["filters"].ShouldBeOfType<List<object?>>();
        filters.Count.ShouldBe(1);

        var filter = filters[0].ShouldBeOfType<Dictionary<string, object?>>();
        filter["type"].ShouldBe("ExtensionRef");

        var extensionRef = filter["extensionRef"].ShouldBeOfType<Dictionary<string, string>>();
        extensionRef["group"].ShouldBe("traefik.io");
        extensionRef["kind"].ShouldBe("Middleware");
        extensionRef["name"].ShouldBe("waf-crs");
    }

    [Fact]
    public void BuildHttpRoute_WithoutExtensionRefs_DoesNotAddFilters()
    {
        var gatewaySpec = new GatewaySpec
        {
            Enabled = true,
            GatewayRef = new GatewayRefSpec { Name = "public-gw" },
        };

        var result = sut.BuildHttpRoute(
            "repo-route",
            "default",
            "repo-svc",
            80,
            gatewaySpec,
            "repo");

        var spec = result["spec"].ShouldBeOfType<Dictionary<string, object?>>();
        var rules = spec["rules"].ShouldBeOfType<List<Dictionary<string, object?>>>();
        var firstRule = rules.Single();

        firstRule.ContainsKey("filters").ShouldBeFalse();
    }

    [Fact]
    public void BuildHttpRoute_AddsCertManagerClusterIssuerAnnotation_WhenCertManagerIsClusterIssuer()
    {
        var gatewaySpec = new GatewaySpec
        {
            Enabled    = true,
            GatewayRef = new GatewayRefSpec { Name = "public-gw" },
            CertManager = new CertManagerSpec
            {
                IssuerName      = "letsencrypt-prod",
                IsClusterIssuer = true,
            },
        };

        var result = sut.BuildHttpRoute("repo-route", "default", "repo-svc", 80, gatewaySpec, "repo");

        var metadata = result["metadata"].ShouldBeOfType<Dictionary<string, object?>>();
        var annotations = metadata["annotations"].ShouldBeOfType<Dictionary<string, string>>();
        annotations.ShouldContainKey("cert-manager.io/cluster-issuer");
        annotations["cert-manager.io/cluster-issuer"].ShouldBe("letsencrypt-prod");
        annotations.ShouldNotContainKey("cert-manager.io/issuer");
    }

    [Fact]
    public void BuildHttpRoute_AddsCertManagerIssuerAnnotation_WhenCertManagerIsNamespacedIssuer()
    {
        var gatewaySpec = new GatewaySpec
        {
            Enabled    = true,
            GatewayRef = new GatewayRefSpec { Name = "public-gw" },
            CertManager = new CertManagerSpec
            {
                IssuerName      = "my-issuer",
                IsClusterIssuer = false,
            },
        };

        var result = sut.BuildHttpRoute("repo-route", "default", "repo-svc", 80, gatewaySpec, "repo");

        var metadata = result["metadata"].ShouldBeOfType<Dictionary<string, object?>>();
        var annotations = metadata["annotations"].ShouldBeOfType<Dictionary<string, string>>();
        annotations.ShouldContainKey("cert-manager.io/issuer");
        annotations["cert-manager.io/issuer"].ShouldBe("my-issuer");
        annotations.ShouldNotContainKey("cert-manager.io/cluster-issuer");
    }

    [Fact]
    public void BuildHttpRoute_NoAnnotations_WhenNoCertManagerAndNoRouteAnnotations()
    {
        var gatewaySpec = new GatewaySpec
        {
            Enabled    = true,
            GatewayRef = new GatewayRefSpec { Name = "public-gw" },
        };

        var result = sut.BuildHttpRoute("repo-route", "default", "repo-svc", 80, gatewaySpec, "repo");

        var metadata = result["metadata"].ShouldBeOfType<Dictionary<string, object?>>();
        metadata.ShouldNotContainKey("annotations");
    }

    [Fact]
    public void BuildHttpRoute_UsesRepositoryPathPrefix_WhenGatewayPathMissing()
    {
        var gatewaySpec = new GatewaySpec
        {
            Enabled = true,
            GatewayRef = new GatewayRefSpec { Name = "public-gw" },
        };

        var result = sut.BuildHttpRoute(
            "repo-route",
            "default",
            "repo-svc",
            80,
            gatewaySpec,
            "repo",
            "/");

        var spec = result["spec"].ShouldBeOfType<Dictionary<string, object?>>();
        var rules = spec["rules"].ShouldBeOfType<List<Dictionary<string, object?>>>();
        var matches = rules.Single()["matches"].ShouldBeOfType<Dictionary<string, object?>[]>();
        var path = matches.Single()["path"].ShouldBeOfType<Dictionary<string, string>>();
        path["value"].ShouldBe("/");
    }
}

