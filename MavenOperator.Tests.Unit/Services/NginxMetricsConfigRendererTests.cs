using Shouldly;
using MavenOperator.Entities.Spec;
using MavenOperator.Services;

namespace MavenOperator.Tests.Unit.Services;

/// <summary>
/// Unit tests for Phase 6A NginxConfigRenderer changes:
/// - Map directives for Maven coordinate extraction
/// - JSON access log format block
/// - stub_status server block (conditional on metrics.enabled)
/// - access_log directive inside location
/// </summary>
public sealed class NginxMetricsConfigRendererTests
{
    private readonly INginxConfigRenderer _sut = new NginxConfigRenderer();

    // ── Hosted — metrics enabled (default) ───────────────────────────────────

    [Fact]
    public void RenderHosted_WithMetricsEnabled_ContainsMapDirectives()
    {
        var result = _sut.RenderHosted("releases", AuthPolicy.Anonymous, AuthPolicy.Authenticated,
            new MetricsSpec { Enabled = true });

        result.ShouldContain("map $uri $maven_repo_releases");
        result.ShouldContain("map $uri $maven_artifact_group_releases");
        result.ShouldContain("map $uri $maven_artifact_id_releases");
        result.ShouldContain("map $uri $maven_artifact_version_releases");
        result.ShouldContain("map $uri $maven_asset_type_releases");
    }

    [Fact]
    public void RenderHosted_WithMetricsEnabled_ContainsLogFormat()
    {
        var result = _sut.RenderHosted("releases", AuthPolicy.Anonymous, AuthPolicy.Authenticated,
            new MetricsSpec { Enabled = true });

        result.ShouldContain("log_format maven_json_releases escape=json");
        result.ShouldContain("\"artifact_group\"");
        result.ShouldContain("\"artifact_id\"");
        result.ShouldContain("\"asset_type\"");
    }

    [Fact]
    public void RenderHosted_WithMetricsEnabled_ContainsStubStatusServer()
    {
        var result = _sut.RenderHosted("releases", AuthPolicy.Anonymous, AuthPolicy.Authenticated,
            new MetricsSpec { Enabled = true, StubStatusPort = 9080 });

        result.ShouldContain("listen 9080");
        result.ShouldContain("stub_status");
        result.ShouldContain("allow 127.0.0.1");
    }

    [Fact]
    public void RenderHosted_WithMetricsEnabled_ContainsAccessLogDirective()
    {
        var result = _sut.RenderHosted("releases", AuthPolicy.Anonymous, AuthPolicy.Authenticated,
            new MetricsSpec { Enabled = true });

        result.ShouldContain("access_log /var/log/nginx/access.json maven_json_releases");
    }

    [Fact]
    public void RenderHosted_WithMetricsDisabled_NoStubStatus()
    {
        var result = _sut.RenderHosted("releases", AuthPolicy.Anonymous, AuthPolicy.Authenticated,
            new MetricsSpec { Enabled = false });

        // No stub_status server block rendered
        result.ShouldNotContain("location /stub_status");
        result.ShouldNotContain("access_log /var/log/nginx");
    }

    [Fact]
    public void RenderHosted_NullMetrics_DefaultsToEnabled()
    {
        // Null MetricsSpec defaults to a new MetricsSpec() which has Enabled=true
        var result = _sut.RenderHosted("releases", AuthPolicy.Anonymous, AuthPolicy.Authenticated, null);

        result.ShouldContain("stub_status");
        result.ShouldContain("log_format maven_json_releases");
    }

    [Fact]
    public void RenderHosted_MapDirectivesUseRepoNameSuffix()
    {
        // Ensures two repos don't clash in the same nginx http context
        var r1 = _sut.RenderHosted("alpha", AuthPolicy.Anonymous, AuthPolicy.Authenticated, new MetricsSpec());
        var r2 = _sut.RenderHosted("beta",  AuthPolicy.Anonymous, AuthPolicy.Authenticated, new MetricsSpec());

        r1.ShouldContain("$maven_repo_alpha");
        r1.ShouldNotContain("$maven_repo_beta");
        r2.ShouldContain("$maven_repo_beta");
        r2.ShouldNotContain("$maven_repo_alpha");
    }

    [Theory]
    [InlineData("my-repo",          "my_repo")]
    [InlineData("e2e-hosted-abc",   "e2e_hosted_abc")]
    [InlineData("central-proxy",    "central_proxy")]
    [InlineData("releases",         "releases")]      // no hyphens — unchanged
    [InlineData("e2e-virt-m1-abc",  "e2e_virt_m1_abc")]
    public void RenderHosted_HyphensInRepoName_SanitisedInNginxVariables(string repoName, string expectedVarName)
    {
        // NGINX variable names cannot contain hyphens — the renderer must replace them.
        var result = _sut.RenderHosted(repoName, AuthPolicy.Anonymous, AuthPolicy.Authenticated, new MetricsSpec());

        // Variable identifiers use the sanitised name
        result.ShouldContain($"$maven_repo_{expectedVarName}");
        result.ShouldContain($"$maven_asset_type_{expectedVarName}");
        result.ShouldContain($"log_format maven_json_{expectedVarName}");

        // URL paths still use the original name (hyphens are valid in URLs)
        result.ShouldContain($"/repository/{repoName}/");

        // Must never appear: a raw hyphenated NGINX variable
        if (repoName.Contains('-'))
            result.ShouldNotContain($"$maven_repo_{repoName}");
    }

    [Theory]
    [InlineData("my-proxy",      "my_proxy")]
    [InlineData("central-proxy", "central_proxy")]
    public void RenderProxy_HyphensInRepoName_SanitisedInNginxVariablesAndCacheZone(string repoName, string expectedVarName)
    {
        var result = _sut.RenderProxy(repoName, AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2", "1d", string.Empty, new MetricsSpec());

        result.ShouldContain($"$maven_repo_{expectedVarName}");
        result.ShouldContain($"keys_zone={expectedVarName}_cache");
        result.ShouldContain($"proxy_cache {expectedVarName}_cache");
        result.ShouldContain($"/repository/{repoName}/");  // URL path unchanged

        if (repoName.Contains('-'))
            result.ShouldNotContain($"$maven_repo_{repoName}");
    }

    // ── Proxy — metrics enabled ───────────────────────────────────────────────

    [Fact]
    public void RenderProxy_WithMetricsEnabled_ContainsMapDirectives()
    {
        var result = _sut.RenderProxy("central-proxy", AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2", "1d", string.Empty,
            new MetricsSpec { Enabled = true });

        // Hyphens in the repo name are replaced with underscores in NGINX variable names
        // because NGINX identifiers cannot contain hyphens.
        result.ShouldContain("map $uri $maven_repo_central_proxy");
        result.ShouldContain("map $uri $maven_asset_type_central_proxy");
    }

    [Fact]
    public void RenderProxy_WithMetricsEnabled_ContainsCacheStatusInLogFormat()
    {
        var result = _sut.RenderProxy("central-proxy", AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2", "1d", string.Empty,
            new MetricsSpec { Enabled = true });

        // Proxy log format should include actual $upstream_cache_status
        result.ShouldContain("$upstream_cache_status");
    }

    [Fact]
    public void RenderProxy_WithMetricsEnabled_ContainsStubStatusServer()
    {
        var result = _sut.RenderProxy("central-proxy", AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2", "1d", string.Empty,
            new MetricsSpec { Enabled = true, StubStatusPort = 9080 });

        result.ShouldContain("listen 9080");
        result.ShouldContain("stub_status");
    }

    [Fact]
    public void RenderProxy_WithMetricsDisabled_NoStubStatus()
    {
        var result = _sut.RenderProxy("central-proxy", AuthPolicy.Anonymous,
            "https://repo1.maven.org/maven2", "1d", string.Empty,
            new MetricsSpec { Enabled = false });

        result.ShouldNotContain("location /stub_status");
    }

    // ── mtail program ─────────────────────────────────────────────────────────

    [Fact]
    public void RenderMtailConfig_ContainsCounterDeclarations()
    {
        var result = _sut.RenderMtailConfig();

        result.ShouldContain("counter maven_artifact_requests_total");
        result.ShouldContain("counter maven_artifact_bytes_total");
        result.ShouldContain("counter maven_cache_hits_total");
        result.ShouldContain("counter maven_upload_bytes_total");
    }

    [Fact]
    public void RenderMtailConfig_ContainsHistogramDeclaration()
    {
        var result = _sut.RenderMtailConfig();

        result.ShouldContain("histogram maven_request_duration_seconds");
        result.ShouldContain("buckets");
    }

    [Fact]
    public void RenderMtailConfig_ContainsJsonParsingRule()
    {
        var result = _sut.RenderMtailConfig();

        result.ShouldContain("json()");
        result.ShouldContain("artifact_group");
        result.ShouldContain("artifact_id");
        result.ShouldContain("artifact_version");
    }

    [Fact]
    public void RenderMtailConfig_ContainsUploadTracking()
    {
        var result = _sut.RenderMtailConfig();
        // Upload bytes only accumulated for PUT method
        result.ShouldContain("maven_upload_bytes_total");
        result.ShouldContain("PUT");
    }

    [Fact]
    public void RenderMtailConfig_ContainsCacheHitTracking()
    {
        var result = _sut.RenderMtailConfig();
        // cache_status "-" guard prevents noise from hosted repos
        result.ShouldContain("maven_cache_hits_total");
        result.ShouldContain("!= \"-\"");
    }

    [Fact]
    public void RenderMtailConfig_IsNotEmpty()
    {
        var result = _sut.RenderMtailConfig();
        result.ShouldNotBeNullOrWhiteSpace();
    }
}



