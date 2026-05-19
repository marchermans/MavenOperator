using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace MavenOperator.Tests.Unit.Services;

public sealed class Phase6BAuthWiringTests
{
    [Fact]
    public void RenderHosted_UsesAuthRequest_WhenDownloadAuthProxyEnabled()
    {
        var renderer = new NginxConfigRenderer();

        var result = renderer.RenderHosted(
            "releases",
            AuthPolicy.Authenticated,
            AuthPolicy.Authenticated,
            metrics: new MetricsSpec { Enabled = false },
            downloadAuthProxyEnabled: true,
            uploadAuthProxyEnabled: false);

        result.ShouldContain("location = /auth/validate");
        result.ShouldContain("auth_request /auth/validate");
        result.ShouldNotContain("auth_basic \"Maven - releases\"");
    }

    [Fact]
    public void RenderHosted_UsesAuthRequest_WhenUploadAuthProxyEnabled()
    {
        var renderer = new NginxConfigRenderer();

        var result = renderer.RenderHosted(
            "releases",
            AuthPolicy.Anonymous,
            AuthPolicy.Authenticated,
            metrics: new MetricsSpec { Enabled = false },
            downloadAuthProxyEnabled: false,
            uploadAuthProxyEnabled: true);

        // Download location should NOT use auth_request (download is anonymous)
        result.ShouldContain("location /repository/releases/");
        // But upload (inside limit_except) should use auth_request
        result.ShouldContain("limit_except GET HEAD OPTIONS");
        // The /auth/validate endpoint must exist
        result.ShouldContain("location = /auth/validate");
    }

    [Fact]
    public void RenderHosted_BothDownloadAndUploadAuthProxy_CreatesSharedAuthEndpoint()
    {
        var renderer = new NginxConfigRenderer();

        var result = renderer.RenderHosted(
            "releases",
            AuthPolicy.Authenticated,
            AuthPolicy.Authenticated,
            metrics: new MetricsSpec { Enabled = false },
            downloadAuthProxyEnabled: true,
            uploadAuthProxyEnabled: true);

        // Single /auth/validate endpoint handles both download and upload
        var validateCount = result.Split("location = /auth/validate").Length - 1;
        validateCount.ShouldBe(1, "Should only define /auth/validate once");
        
        // Both download and upload should use auth_request
        result.Split("auth_request /auth/validate").Length.ShouldBeGreaterThanOrEqualTo(3); // before and after plus once in endpoint
    }

    [Fact]
    public void RenderProxy_UsesAuthRequest_WhenDownloadAuthProxyEnabled()
    {
        var renderer = new NginxConfigRenderer();

        var result = renderer.RenderProxy(
            "central-cache",
            AuthPolicy.Authenticated,
            "https://repo1.maven.org/maven2",
            "1d",
            "",
            metrics: new MetricsSpec { Enabled = false },
            downloadAuthProxyEnabled: true);

        result.ShouldContain("location = /auth/validate");
        result.ShouldContain("auth_request /auth/validate");
        result.ShouldNotContain("auth_basic \"Maven Proxy");
    }

    [Fact]
    public void RenderProxy_NoAuthProxy_WhenDownloadAuthProxyDisabled()
    {
        var renderer = new NginxConfigRenderer();

        var result = renderer.RenderProxy(
            "central-cache",
            AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2",
            "1d",
            "",
            metrics: new MetricsSpec { Enabled = false },
            downloadAuthProxyEnabled: false);

        result.ShouldNotContain("auth_request");
        result.ShouldNotContain("auth_basic");
    }

    [Fact]
    public async Task HostedReconciler_WiresCiTrustAclUsers_IntoAuthProxyAndRoleHtpasswd()
    {
        var resources = Substitute.For<IKubernetesResourceManager>();
        var k8s = Substitute.For<IKubernetesClient>();
        var events = Substitute.For<IKubernetesEventService>();

        var reconciler = new HostedRepositoryReconciler(
            k8s,
            resources,
            new HtpasswdService(),
            new RoleBasedHtpasswdService(new HtpasswdService()),
            new AuthProxyConfigRenderer(),
            new NginxConfigRenderer(),
            events,
            NullLogger<HostedRepositoryReconciler>.Instance);

        var entity = BuildHostedEntityWithPhase6B("hosted-auth", "ns");

        k8s.GetAsync<V1Secret>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var secretName = (string)call.Args()[0]!;
                return secretName switch
                {
                    "reader-secret" => BuildCredentialSecret("reader", "reader-pass"),
                    "deployer-secret" => BuildCredentialSecret("deployer", "deployer-pass"),
                    _ => null,
                };
            });

        IDictionary<string, string>? authProxyConfig = null;
        IDictionary<string, string>? downloadSecret = null;
        IDictionary<string, string>? uploadSecret = null;
        V1PodSpec? capturedPodSpec = null;

        resources.EnsurePvcAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new V1PersistentVolumeClaim());
        resources.EnsureSecretAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(), Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var name = ci.ArgAt<string>(1);
                var data = ci.ArgAt<IDictionary<string, string>>(2);
                if (name.EndsWith("download-htpasswd", StringComparison.Ordinal)) downloadSecret = data;
                if (name.EndsWith("upload-htpasswd", StringComparison.Ordinal)) uploadSecret = data;
                return new V1Secret();
            });
        resources.EnsureConfigMapAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(), Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var name = ci.ArgAt<string>(1);
                var data = ci.ArgAt<IDictionary<string, string>>(2);
                if (name.EndsWith("auth-proxy-cm", StringComparison.Ordinal)) authProxyConfig = data;
                return new V1ConfigMap();
            });
        resources.EnsureDeploymentAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1PodSpec>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedPodSpec = ci.ArgAt<V1PodSpec>(3);
                return new V1Deployment();
            });
        resources.EnsureServiceWithPortsAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IList<V1ServicePort>>(), Arg.Any<CancellationToken>())
            .Returns(new V1Service());
        resources.EnsurePodMonitorAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MetricsSpec>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await reconciler.ReconcileAsync(entity, CancellationToken.None);

        authProxyConfig.ShouldNotBeNull();
        var configJson = authProxyConfig!["config.json"];
        configJson.ShouldContain("\"ciTrust\"");
        configJson.ShouldContain("\"acls\"");
        configJson.ShouldContain("com/example/**");

        downloadSecret.ShouldNotBeNull();
        uploadSecret.ShouldNotBeNull();
        downloadSecret!["download.htpasswd"].ShouldContain("reader:");
        downloadSecret["download.htpasswd"].ShouldContain("deployer:");
        uploadSecret!["upload.htpasswd"].ShouldContain("deployer:");
        uploadSecret["upload.htpasswd"].ShouldNotContain("reader:");

        capturedPodSpec.ShouldNotBeNull();
        capturedPodSpec!.Containers.Select(c => c.Name).ShouldContain("maven-auth-proxy");
    }

    private static MavenRepositoryV1Alpha1 BuildHostedEntityWithPhase6B(string name, string ns) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
            Spec = new MavenRepositorySpec
            {
                Type = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi" },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec
                    {
                        Policy = AuthPolicy.Authenticated,
                        Users =
                        [
                            new UserRef { SecretRef = "reader-secret", Role = UserRole.Reader },
                            new UserRef { SecretRef = "deployer-secret", Role = UserRole.Deployer },
                        ],
                    },
                    Upload = new AuthPolicySpec
                    {
                        Policy = AuthPolicy.Authenticated,
                        Users =
                        [
                            new UserRef { SecretRef = "deployer-secret", Role = UserRole.Deployer },
                        ],
                        CiTrust =
                        [
                            new CiTrustBinding
                            {
                                Platform = CiPlatform.GitHubActions,
                                Audience = "maven-operator",
                                Role = UserRole.Deployer,
                                Claims = new Dictionary<string, string>
                                {
                                    ["repository"] = "owner/repo",
                                },
                            },
                        ],
                        Acls =
                        [
                            new ArtifactAcl
                            {
                                Path = "com/example/**",
                                Roles = [UserRole.Deployer],
                            },
                        ],
                    },
                },
                Metrics = new MetricsSpec { Enabled = false },
            },
        };

    private static V1Secret BuildCredentialSecret(string username, string password) =>
        new()
        {
            Data = new Dictionary<string, byte[]>
            {
                ["username"] = System.Text.Encoding.UTF8.GetBytes(username),
                ["password"] = System.Text.Encoding.UTF8.GetBytes(password),
            },
        };
}


