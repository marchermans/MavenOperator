using Shouldly;
using MavenOperator.Entities.Spec;
using MavenOperator.Services;
namespace MavenOperator.Tests.Unit.Services;
public sealed class NginxConfigRendererTests
{
    private readonly INginxConfigRenderer _sut = new NginxConfigRenderer();
    [Fact]
    public void RenderHosted_ContainsRepositoryName_InLocationBlock()
    {
        var result = _sut.RenderHosted("my-releases", AuthPolicy.Anonymous, AuthPolicy.Authenticated);
        result.ShouldContain("/repository/my-releases/");
    }
    [Fact]
    public void RenderHosted_AnonymousDownload_DoesNotContainDownloadAuthBasic()
    {
        var result = _sut.RenderHosted("my-repo", AuthPolicy.Anonymous, AuthPolicy.Authenticated);
        // The outer location block should not have auth_basic for download
        // (auth_basic for upload only appears inside limit_except)
        result.ShouldNotContain("auth_basic \"Maven - my-repo\"");
    }
    [Fact]
    public void RenderHosted_AuthenticatedDownload_ContainsDownloadAuthBasic()
    {
        var result = _sut.RenderHosted("my-repo", AuthPolicy.Authenticated, AuthPolicy.Authenticated);
        result.ShouldContain("auth_basic \"Maven - my-repo\"");
        result.ShouldContain("download.htpasswd");
    }
    [Fact]
    public void RenderHosted_AuthenticatedUpload_ContainsUploadAuthBasicInsideLimitExcept()
    {
        var result = _sut.RenderHosted("my-repo", AuthPolicy.Anonymous, AuthPolicy.Authenticated);
        result.ShouldContain("limit_except GET HEAD OPTIONS");
        result.ShouldContain("auth_basic \"Maven Upload - my-repo\"");
        result.ShouldContain("upload.htpasswd");
    }
    [Fact]
    public void RenderHosted_ContainsDavMethods_ForWebDav()
    {
        var result = _sut.RenderHosted("my-repo", AuthPolicy.Anonymous, AuthPolicy.Authenticated);
        result.ShouldContain("dav_methods PUT DELETE");
    }
    [Fact]
    public void RenderHosted_ContainsHealthCheckLocation()
    {
        var result = _sut.RenderHosted("my-repo", AuthPolicy.Anonymous, AuthPolicy.Authenticated);
        result.ShouldContain("/healthz");
    }

    [Fact]
    public void RenderHosted_UploadAuthProxy_UsesLocationScopedAuthRequest_AndCorrectSidecarPort()
    {
        var result = _sut.RenderHosted(
            "my-repo",
            AuthPolicy.Anonymous,
            AuthPolicy.Authenticated,
            downloadAuthProxyEnabled: false,
            uploadAuthProxyEnabled: true);

        result.ShouldContain("proxy_pass http://127.0.0.1:8080/auth/validate;");
        result.ShouldContain("auth_request /auth/validate;");
        result.ShouldContain("# Enforced by the outer auth_request using X-Original-Method.");
        result.ShouldNotContain("limit_except GET HEAD OPTIONS {\n            # Upload authentication enforcement\n            auth_request /auth/validate;");
    }
    [Theory]
    [InlineData("releases")]
    [InlineData("my-snapshots")]
    [InlineData("corp-proxy")]
    public void RenderHosted_WorksForArbitraryValidNames(string name)
    {
        var result = _sut.RenderHosted(name, AuthPolicy.Anonymous, AuthPolicy.Authenticated);
        result.ShouldContain($"/repository/{name}/");
        result.ShouldNotBeNullOrWhiteSpace();
    }
    [Fact]
    public void RenderHosted_Throws_WhenNameIsEmpty()
    {
        Should.Throw<ArgumentException>(() =>
            _sut.RenderHosted(string.Empty, AuthPolicy.Anonymous, AuthPolicy.Authenticated));
    }

    [Fact]
    public void RenderHosted_UsesCustomPathPrefix_WhenConfigured()
    {
        var result = _sut.RenderHosted(
            "public",
            AuthPolicy.Anonymous,
            AuthPolicy.Authenticated,
            pathPrefix: "/");

        result.ShouldContain("location /");
        result.ShouldNotContain("location /repository/public/");
    }
}
