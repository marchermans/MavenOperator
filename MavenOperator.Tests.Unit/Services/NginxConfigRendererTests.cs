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

    // ── Proxy upload forwarding tests ────────────────────────────────────────

    [Fact]
    public void RenderProxy_WithoutUpload_DoesNotContainLimitExcept()
    {
        var result = _sut.RenderProxy(
            "my-proxy",
            AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2/",
            "1d",
            string.Empty);

        result.ShouldNotContain("limit_except");
        result.ShouldNotContain("upload.htpasswd");
    }

    [Fact]
    public void RenderProxy_UploadEnabled_PassthroughMode_ContainsClientAuthAndUpstreamCredentials()
    {
        var upstreamCreds = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("deployer:s3cr3t"));
        var result = _sut.RenderProxy(
            "my-proxy",
            AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2/",
            "1d",
            string.Empty,
            uploadEnabled: true,
            uploadMode: ProxyUploadMode.Passthrough,
            uploadPolicy: AuthPolicy.Authenticated,
            upstreamUploadAuthHeader: $"Basic {upstreamCreds}");

        // Should have limit_except block for write methods
        result.ShouldContain("limit_except GET HEAD OPTIONS");

        // Client auth enforced in Passthrough mode with Authenticated policy
        result.ShouldContain("auth_basic \"Maven Upload - my-proxy\"");
        result.ShouldContain("upload.htpasswd");

        // Upstream credentials injected for forwarding
        result.ShouldContain($"proxy_set_header Authorization \"Basic {upstreamCreds}\"");

        // Cache bypassed for writes
        result.ShouldContain("proxy_cache off;");
    }

    [Fact]
    public void RenderProxy_UploadEnabled_PassthroughMode_AnonymousPolicy_NoClientAuth()
    {
        var upstreamCreds = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("deployer:s3cr3t"));
        var result = _sut.RenderProxy(
            "my-proxy",
            AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2/",
            "1d",
            string.Empty,
            uploadEnabled: true,
            uploadMode: ProxyUploadMode.Passthrough,
            uploadPolicy: AuthPolicy.Anonymous,
            upstreamUploadAuthHeader: $"Basic {upstreamCreds}");

        // limit_except block exists but no client auth (Anonymous policy)
        result.ShouldContain("limit_except GET HEAD OPTIONS");
        result.ShouldNotContain("auth_basic \"Maven Upload - my-proxy\"");

        // Still forwards with upstream credentials
        result.ShouldContain($"proxy_set_header Authorization \"Basic {upstreamCreds}\"");
    }

    [Fact]
    public void RenderProxy_UploadEnabled_OverrideMode_NoClientAuth()
    {
        var upstreamCreds = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("deployer:s3cr3t"));
        var result = _sut.RenderProxy(
            "my-proxy",
            AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2/",
            "1d",
            string.Empty,
            uploadEnabled: true,
            uploadMode: ProxyUploadMode.Override,
            uploadPolicy: AuthPolicy.Authenticated, // Ignored in Override mode
            upstreamUploadAuthHeader: $"Basic {upstreamCreds}");

        // limit_except block exists but no client auth (Override mode)
        result.ShouldContain("limit_except GET HEAD OPTIONS");
        result.ShouldNotContain("auth_basic \"Maven Upload - my-proxy\"");

        // Still forwards with upstream credentials
        result.ShouldContain($"proxy_set_header Authorization \"Basic {upstreamCreds}\"");
    }

    [Fact]
    public void RenderProxy_UploadEnabled_FallsBackToReadAuthHeader_WhenNoUploadCredentials()
    {
        var readCreds = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("reader:pass"));
        var result = _sut.RenderProxy(
            "my-proxy",
            AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2/",
            "1d",
            $"Basic {readCreds}", // upstreamAuthHeader for reads
            uploadEnabled: true,
            uploadMode: ProxyUploadMode.Passthrough,
            uploadPolicy: AuthPolicy.Authenticated,
            upstreamUploadAuthHeader: string.Empty);

        // Should fall back to read auth header when no dedicated upload credentials
        result.ShouldContain($"proxy_set_header Authorization \"Basic {readCreds}\"");
    }

    [Fact]
    public void RenderProxy_UploadEnabled_PreservesReadCacheBehavior()
    {
        var result = _sut.RenderProxy(
            "my-proxy",
            AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2/",
            "1d",
            string.Empty,
            uploadEnabled: true,
            uploadMode: ProxyUploadMode.Passthrough,
            uploadPolicy: AuthPolicy.Authenticated);

        // Read caching should still be configured at location level
        result.ShouldContain("proxy_cache my_proxy_cache;");
        result.ShouldContain("proxy_cache_methods GET HEAD;");

        // Write operations bypass cache (inside limit_except)
        result.ShouldContain("proxy_cache off;");
    }

    [Fact]
    public void RenderProxy_UploadEnabled_IncludesErrorHandlingForUpstreamRejection()
    {
        var upstreamCreds = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("deployer:s3cr3t"));
        var result = _sut.RenderProxy(
            "my-proxy",
            AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2/",
            "1d",
            string.Empty,
            uploadEnabled: true,
            uploadMode: ProxyUploadMode.Passthrough,
            uploadPolicy: AuthPolicy.Authenticated,
            upstreamUploadAuthHeader: $"Basic {upstreamCreds}");

        // proxy_intercept_errors must be enabled for error mapping to work
        result.ShouldContain("proxy_intercept_errors on;");

        // Error page directives map upstream 401/403 to internal handler
        result.ShouldContain("error_page 401 = @upload_upstream_error;");
        result.ShouldContain("error_page 403 = @upload_upstream_error;");

        // Internal error handler returns helpful message without leaking credentials
        result.ShouldContain("location @upload_upstream_error {");
        result.ShouldContain("internal;");
        result.ShouldContain("return 502 \"Upstream rejected upload");
    }

    [Fact]
    public void RenderProxy_UploadDisabled_NoErrorHandlingDirectives()
    {
        var result = _sut.RenderProxy(
            "my-proxy",
            AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2/",
            "1d",
            string.Empty,
            uploadEnabled: false);

        // No error handling when uploads are disabled
        result.ShouldNotContain("proxy_intercept_errors on;");
        result.ShouldNotContain("@upload_upstream_error");
    }

    [Fact]
    public void RenderProxy_OverrideMode_WithExplicitUploadCredentials_ForwardsCorrectly()
    {
        var readCreds = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("reader:pass"));
        var uploadCreds = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("deployer:s3cr3t"));
        var result = _sut.RenderProxy(
            "my-proxy",
            AuthPolicy.Authenticated, // Download requires auth
            "https://repo1.maven.org/maven2/",
            "1d",
            $"Basic {readCreds}", // Read credentials
            uploadEnabled: true,
            uploadMode: ProxyUploadMode.Override,
            uploadPolicy: AuthPolicy.Authenticated, // Would require auth in Passthrough mode
            upstreamUploadAuthHeader: $"Basic {uploadCreds}");

        // Override mode skips client auth for uploads even with Authenticated policy
        result.ShouldContain("limit_except GET HEAD OPTIONS");
        result.ShouldNotContain("auth_basic \"Maven Upload - my-proxy\"");

        // Uses dedicated upload credentials (not read credentials) for forwarding
        result.ShouldContain($"proxy_set_header Authorization \"Basic {uploadCreds}\"");

        // Download auth is still enforced separately (outside limit_except)
        result.ShouldContain("auth_basic \"Maven Proxy - my-proxy\"");
    }
}
