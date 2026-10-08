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
        // The per-request full upstream URL is computed by an http-context map;
        // proxy_pass references that variable (variable-only → complete URL).
        result.ShouldContain($"proxy_pass $rs_up_url_central;");
        result.ShouldContain("repo1.maven.org/maven2/$r");
    }

    [Fact]
    public void RenderProxy_BareHostUpstream_ProducesNoDoubleSlashInUrlMap()
    {
        var result = _sut.RenderProxy("central", AuthPolicy.Anonymous,
            "http://repo1.maven.org", "1d", "");
        // upstream_path is empty (not "/") → map renders http://host/<path>, not host//<path>
        result.ShouldNotContain("repo1.maven.org//");
        result.ShouldContain($"\"http://repo1.maven.org/$r\"");
    }

    [Fact]
    public void RenderProxy_TrimsTrailingSlashFromUpstreamUrl()
    {
        var result = _sut.RenderProxy("central", AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2/", "1d", "");
        // The rendered URL map should not have a double slash
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
        // Map strips the bare "/" prefix and prepends /maven2 — no rewrite directive.
        result.ShouldNotContain("rewrite ^");
        result.ShouldContain($"\"https://repo1.maven.org/maven2/$r\"");
        result.ShouldNotContain("/repository/central/");
    }

    // ── Upload forwarding rendering ────────────────────────────────────────

    [Fact]
    public void RenderProxy_PassthroughAuthenticatedUpload_RendersAuthInLimitExcept()
    {
        var result = _sut.RenderProxy(
            "my-proxy", AuthPolicy.Anonymous, UpstreamUrl, "1d", "",
            uploadEnabled: true,
            uploadMode: ProxyUploadMode.Passthrough,
            uploadPolicy: AuthPolicy.Authenticated,
            upstreamUploadAuthHeader: "Basic dXNlcjpwYXNz");

        // Client-side auth gate (applies only to write methods via limit_except).
        result.ShouldContain("auth_basic \"Maven Upload - my-proxy\"");
        result.ShouldContain("auth_basic_user_file /etc/nginx/upload-auth/upload.htpasswd;");
        // Upstream credential map: client Authorization passes through on reads,
        // upload credentials are injected for write methods.
        // Map values are the raw Authorization header value (consumed via
        // proxy_set_header Authorization $rs_up_auth_... in the location).
        result.ShouldContain($"map $request_method $rs_up_auth_my_proxy {{");
        result.ShouldContain(@"PUT    ""Basic dXNlcjpwYXNz""");
    }

    [Fact]
    public void RenderProxy_OverrideUpload_RendersNoClientAuthGate()
    {
        var result = _sut.RenderProxy(
            "my-proxy", AuthPolicy.Anonymous, UpstreamUrl, "1d", "",
            uploadEnabled: true,
            uploadMode: ProxyUploadMode.Override,
            upstreamUploadAuthHeader: "Basic dXNlcjpwYXNz");

        // Override mode: no client auth gate (comments may mention the directive name).
        result.ShouldNotContain(@"auth_basic ""Maven Upload");
        result.ShouldNotContain("auth_basic_user_file");
        // Upstream credentials are still injected for write methods.
        result.ShouldContain($"map $request_method $rs_up_auth_my_proxy {{");
        result.ShouldContain(@"PUT    ""Basic dXNlcjpwYXNz""");
    }

    [Fact]
    public void RenderProxy_UploadEnabledWithoutDedicatedCreds_ReusesReadCredentialsForAllMethods()
    {
        const string header = "Basic cmVhZGVyOnBhc3M=";
        var result = _sut.RenderProxy(
            "my-proxy", AuthPolicy.Anonymous, UpstreamUrl, "1d", header,
            uploadEnabled: true);

        // No dedicated upload credentials → the per-method map reuses the read
        // credential for every method (reads and writes alike).
        result.ShouldContain("map $request_method $rs_up_auth_my_proxy {");
        result.ShouldContain($"default \"{header}\";");
        result.ShouldContain($@"PUT    ""{header}""");
    }

    [Fact]
    public void RenderProxy_UploadDisabled_RendersReadOnly405Gate()
    {
        var result = _sut.RenderProxy(
            "my-proxy", AuthPolicy.Anonymous, UpstreamUrl, "1d", "",
            uploadEnabled: false);

        // Read-only cache: writes are rejected with 405.
        result.ShouldContain("return 405;");
        result.ShouldNotContain("auth_basic");
    }
}


