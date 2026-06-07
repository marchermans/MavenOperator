using MavenOperator.Entities.Spec;
using MavenOperator.Services;
using Shouldly;

namespace MavenOperator.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="NginxConfigRenderer.RenderProxy"/>.
/// All tests are pure — no cluster, no I/O.
/// </summary>
public sealed class NginxProxyConfigRendererTests
{
    private readonly INginxConfigRenderer _sut = new NginxConfigRenderer();

    private const string UpstreamUrl = "https://repo1.maven.org/maven2";

    // ── Location block ────────────────────────────────────────────────────────

    [Fact]
    public void RenderProxy_ContainsRepositoryNameInLocationBlock()
    {
        var result = _sut.RenderProxy("maven-central", AuthPolicy.Anonymous, UpstreamUrl, "1d", "");
        result.ShouldContain("/repository/maven-central/");
    }

    [Fact]
    public void RenderProxy_ContainsProxyPassDirective_PointingToUpstream()
    {
        var result = _sut.RenderProxy("central", AuthPolicy.Anonymous, UpstreamUrl, "1d", "");
        result.ShouldContain("proxy_pass");
        // The scheme+host goes into the $upstream variable; the path goes into the rewrite target.
        result.ShouldContain("repo1.maven.org");
        result.ShouldContain("/maven2/");
    }

    [Fact]
    public void RenderProxy_TrimsTrailingSlashFromUpstreamUrl()
    {
        var result = _sut.RenderProxy("central", AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2/", "1d", "");
        // The rendered rewrite target should not have a double slash
        result.ShouldNotContain("maven2//");
    }

    // ── Cache directives ──────────────────────────────────────────────────────

    [Fact]
    public void RenderProxy_ContainsCacheZoneNamed_AfterRepository()
    {
        var result = _sut.RenderProxy("my-proxy", AuthPolicy.Anonymous, UpstreamUrl, "1d", "");
        result.ShouldContain("my_proxy_cache");
    }

    [Theory]
    [InlineData("1d")]
    [InlineData("7d")]
    [InlineData("1h")]
    public void RenderProxy_ContainsCacheTtl(string ttl)
    {
        var result = _sut.RenderProxy("central", AuthPolicy.Anonymous, UpstreamUrl, ttl, "");
        result.ShouldContain(ttl);
    }

    [Fact]
    public void RenderProxy_UsesDefaultTtl_WhenCacheTtlIsEmpty()
    {
        var result = _sut.RenderProxy("central", AuthPolicy.Anonymous, UpstreamUrl, "", "");
        result.ShouldContain("1d");
    }

    [Fact]
    public void RenderProxy_ContainsProxyCacheLock_ToPreventThunderingHerd()
    {
        var result = _sut.RenderProxy("central", AuthPolicy.Anonymous, UpstreamUrl, "1d", "");
        result.ShouldContain("proxy_cache_lock on");
    }

    // ── Download auth ─────────────────────────────────────────────────────────

    [Fact]
    public void RenderProxy_AnonymousDownload_DoesNotContainAuthBasic()
    {
        var result = _sut.RenderProxy("central", AuthPolicy.Anonymous, UpstreamUrl, "1d", "");
        result.ShouldNotContain("auth_basic");
    }

    [Fact]
    public void RenderProxy_AuthenticatedDownload_ContainsAuthBasicWithDownloadHtpasswd()
    {
        var result = _sut.RenderProxy("my-proxy", AuthPolicy.Authenticated, UpstreamUrl, "1d", "");
        result.ShouldContain("auth_basic \"Maven Proxy - my-proxy\"");
        result.ShouldContain("download.htpasswd");
    }

    // ── Upstream auth header ──────────────────────────────────────────────────

    [Fact]
    public void RenderProxy_NoUpstreamAuth_DoesNotContainProxySetHeaderAuthorization()
    {
        var result = _sut.RenderProxy("central", AuthPolicy.Anonymous, UpstreamUrl, "1d", "");
        // Should not emit a proxy_set_header Authorization line
        result.ShouldNotContain("proxy_set_header Authorization");
    }

    [Fact]
    public void RenderProxy_UpstreamAuth_ContainsProxySetHeaderWithValue()
    {
        const string header = "Basic dXNlcjpwYXNz";
        var result = _sut.RenderProxy("central", AuthPolicy.Anonymous, UpstreamUrl, "1d", header);
        result.ShouldContain($"proxy_set_header Authorization \"{header}\"");
    }

    // ── Health check ──────────────────────────────────────────────────────────

    [Fact]
    public void RenderProxy_ContainsHealthCheckLocation()
    {
        var result = _sut.RenderProxy("central", AuthPolicy.Anonymous, UpstreamUrl, "1d", "");
        result.ShouldContain("/healthz");
    }

    [Fact]
    public void RenderProxy_DownloadAuthProxy_UsesCorrectSidecarPort()
    {
        var result = _sut.RenderProxy(
            "central",
            AuthPolicy.Authenticated,
            UpstreamUrl,
            "1d",
            "",
            downloadAuthProxyEnabled: true);

        result.ShouldContain("proxy_pass http://127.0.0.1:8080/auth/validate;");
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public void RenderProxy_Throws_WhenNameIsEmpty()
    {
        Should.Throw<ArgumentException>(() =>
            _sut.RenderProxy("", AuthPolicy.Anonymous, UpstreamUrl, "1d", ""));
    }

    [Fact]
    public void RenderProxy_Throws_WhenUpstreamUrlIsEmpty()
    {
        Should.Throw<ArgumentException>(() =>
            _sut.RenderProxy("central", AuthPolicy.Anonymous, "", "1d", ""));
    }

    [Theory]
    [InlineData("maven-central")]
    [InlineData("corp-nexus-proxy")]
    [InlineData("jcenter-mirror")]
    public void RenderProxy_WorksForArbitraryValidNames(string name)
    {
        var result = _sut.RenderProxy(name, AuthPolicy.Anonymous, UpstreamUrl, "1d", "");
        result.ShouldContain($"/repository/{name}/");
        result.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RenderProxy_OutputSize_IsReasonable()
    {
        var result = _sut.RenderProxy("central", AuthPolicy.Anonymous, UpstreamUrl, "1d", "");
        result.Length.ShouldBeGreaterThan(200);
        result.Length.ShouldBeLessThan(64_000);
    }

    [Fact]
    public void RenderProxy_UsesCustomPathPrefix_WhenConfigured()
    {
        var result = _sut.RenderProxy(
            "central",
            AuthPolicy.Anonymous,
            UpstreamUrl,
            "1d",
            "",
            pathPrefix: "/");

        result.ShouldContain("location /");
        result.ShouldContain("rewrite ^/(.*)$ /maven2/$1 break;");
        result.ShouldNotContain("/repository/central/");
    }
}


