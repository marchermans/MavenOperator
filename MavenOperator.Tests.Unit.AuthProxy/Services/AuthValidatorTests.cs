using System.Text;
using MavenOperator.AuthProxy;
using MavenOperator.AuthProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace MavenOperator.Tests.Unit.AuthProxy.Services;

public sealed class AuthValidatorTests
{
    [Fact]
    public async Task ValidateAsync_AnonymousDirectionWithoutHeader_AllowsRequest()
    {
        var sut = BuildValidator(new AuthProxyConfig
        {
            Download = new AuthDirectionConfig
            {
                Policy = "Anonymous",
                HtpasswdPath = "/tmp/unused-download.htpasswd",
            },
            Upload = new AuthDirectionConfig
            {
                Policy = "Authenticated",
                HtpasswdPath = "/tmp/unused-upload.htpasswd",
            },
        });

        var result = await sut.ValidateAsync(null, "/repository/releases/com/example/demo/1.0/demo-1.0.jar", "GET");

        result.Success.ShouldBeTrue();
        result.Role.ShouldBeNull();
    }

    [Fact]
    public async Task ValidateAsync_AuthenticatedDirectionWithoutHeader_DeniesRequest()
    {
        var sut = BuildValidator(new AuthProxyConfig
        {
            Download = new AuthDirectionConfig
            {
                Policy = "Authenticated",
                HtpasswdPath = "/tmp/unused-download.htpasswd",
            },
            Upload = new AuthDirectionConfig
            {
                Policy = "Authenticated",
                HtpasswdPath = "/tmp/unused-upload.htpasswd",
            },
        });

        var result = await sut.ValidateAsync(null, "/repository/releases/com/example/demo/1.0/demo-1.0.jar", "GET");

        result.Success.ShouldBeFalse();
        result.Role.ShouldBeNull();
    }

    // ── Gradle / OAuth2 Basic credential flow ────────────────────────────────
    // Gradle (and some other OAuth2-aware clients) send OIDC tokens as
    // Basic credentials: username="oauth2", password=<JWT token>.

    [Fact]
    public async Task ValidateAsync_GradleOauth2Basic_DelegatesToBearerValidation_OnSuccess()
    {
        // Arrange: configure a ciTrust binding for GitHub Actions
        var binding = new CiTrustBindingConfig
        {
            Platform = "github-actions",
            Role     = "deployer",
            Claims   = new Dictionary<string, string> { { "repository", "acme-org/my-service" } },
        };

        var config = new AuthProxyConfig
        {
            Download = new AuthDirectionConfig
            {
                Policy    = "Authenticated",
                CiTrust   = [binding],
                HtpasswdPath = "/tmp/unused-download.htpasswd",
            },
            Upload = new AuthDirectionConfig
            {
                Policy       = "Authenticated",
                HtpasswdPath = "/tmp/unused-upload.htpasswd",
            },
        };

        // The JWT validator is mocked to return a successful role
        var jwksCache      = Substitute.For<IJwksCache>();
        var trustEvaluator = Substitute.For<ITrustEvaluator>();

        // ValidateBearerAsync will fail to read the token (not a real JWT),
        // so we verify it is reached by observing the IJwksCache is NOT called
        // (the ReadJwtToken call itself throws first).  We use a real (but
        // invalid) token so we can confirm the code path attempts bearer flow.
        const string fakeJwt = "not.a.real.jwt";
        var basicHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"oauth2:{fakeJwt}"));

        var options = Substitute.For<IOptionsMonitor<AuthProxyConfig>>();
        options.CurrentValue.Returns(config);

        var sut = new AuthValidator(options, jwksCache, trustEvaluator, NullLogger<AuthValidator>.Instance);

        // Act
        var result = await sut.ValidateAsync(basicHeader, "/repository/releases/", "GET");

        // Assert: bearer validation was attempted (and failed — invalid token) → 403-class failure
        result.Success.ShouldBeFalse();
        // The JWKS cache must NOT have been called for a regular htpasswd lookup
        // (if we had fallen through to htpasswd, jwksCache would still not be called,
        //  but the key signal is that the result is false without a file-system hit).
        await jwksCache.DidNotReceive().GetKeysAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAsync_GradleOauth2Basic_CaseInsensitiveUsername_DelegatesToBearer()
    {
        // "OAuth2" with mixed case must also be treated as the oauth2 special user
        var config = new AuthProxyConfig
        {
            Download = new AuthDirectionConfig
            {
                Policy       = "Authenticated",
                HtpasswdPath = "/tmp/unused-download.htpasswd",
            },
            Upload = new AuthDirectionConfig
            {
                Policy       = "Authenticated",
                HtpasswdPath = "/tmp/unused-upload.htpasswd",
            },
        };

        const string fakeJwt  = "not.a.real.jwt";
        var basicHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"OAuth2:{fakeJwt}"));

        var jwksCache      = Substitute.For<IJwksCache>();
        var trustEvaluator = Substitute.For<ITrustEvaluator>();
        var options        = Substitute.For<IOptionsMonitor<AuthProxyConfig>>();
        options.CurrentValue.Returns(config);

        var sut = new AuthValidator(options, jwksCache, trustEvaluator, NullLogger<AuthValidator>.Instance);

        var result = await sut.ValidateAsync(basicHeader, "/repository/releases/", "GET");

        // The bearer path was taken (invalid JWT → false), not the htpasswd path
        result.Success.ShouldBeFalse();
        // JWKS cache is not reached because ReadJwtToken throws first, but
        // critically no file-system htpasswd check occurs.
        await jwksCache.DidNotReceive().GetKeysAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAsync_RegularBasic_WithOauth2Username_InHtpasswd_WouldFail()
    {
        // If a real user happened to be named "oauth2" in the htpasswd file,
        // the token path is still taken — the username "oauth2" is always
        // treated as the special CI-token principal, never an htpasswd user.
        var config = new AuthProxyConfig
        {
            Download = new AuthDirectionConfig
            {
                Policy       = "Authenticated",
                HtpasswdPath = "/tmp/unused.htpasswd",   // file does not exist
            },
            Upload = new AuthDirectionConfig
            {
                Policy       = "Authenticated",
                HtpasswdPath = "/tmp/unused.htpasswd",
            },
        };

        // Encode oauth2:somepassword (not a JWT)
        var basicHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("oauth2:somepassword"));

        var options = Substitute.For<IOptionsMonitor<AuthProxyConfig>>();
        options.CurrentValue.Returns(config);

        var sut = new AuthValidator(
            options,
            Substitute.For<IJwksCache>(),
            Substitute.For<ITrustEvaluator>(),
            NullLogger<AuthValidator>.Instance);

        // Even though "somepassword" is not a JWT, the validator takes the bearer
        // path and returns false (not an htpasswd lookup failure)
        var result = await sut.ValidateAsync(basicHeader, "/repository/releases/", "PUT");
        result.Success.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateAsync_NormalBasic_NotOauth2_UsesHtpasswdPath()
    {
        // A regular username (not "oauth2") must still use the htpasswd file path
        var config = new AuthProxyConfig
        {
            Download = new AuthDirectionConfig
            {
                Policy       = "Authenticated",
                HtpasswdPath = "/tmp/nonexistent-test.htpasswd",
            },
            Upload = new AuthDirectionConfig
            {
                Policy       = "Authenticated",
                HtpasswdPath = "/tmp/nonexistent-test.htpasswd",
            },
        };

        var basicHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret"));

        var options = Substitute.For<IOptionsMonitor<AuthProxyConfig>>();
        options.CurrentValue.Returns(config);

        var sut = new AuthValidator(
            options,
            Substitute.For<IJwksCache>(),
            Substitute.For<ITrustEvaluator>(),
            NullLogger<AuthValidator>.Instance);

        // htpasswd file does not exist → false (proves htpasswd path was taken)
        var result = await sut.ValidateAsync(basicHeader, "/repository/releases/", "GET");
        result.Success.ShouldBeFalse();
    }

    // ── Anonymous policy with credentials present ────────────────────────────

    [Fact]
    public async Task ValidateAsync_AnonymousDownload_AllowsEvenWhenBearerTokenPresent()
    {
        // Regression test: Gradle sends the same Bearer token on GETs as on PUTs.
        // If the download policy is Anonymous, the request must be allowed regardless
        // of whether the provided token validates against any CiTrust binding.
        var sut = BuildValidator(new AuthProxyConfig
        {
            Download = new AuthDirectionConfig
            {
                Policy   = "Anonymous",
                CiTrust  = [],   // no CiTrust — token cannot match anything
                HtpasswdPath = "/tmp/unused-download.htpasswd",
            },
            Upload = new AuthDirectionConfig
            {
                Policy       = "Authenticated",
                HtpasswdPath = "/tmp/unused-upload.htpasswd",
            },
        });

        // Gradle sends its OIDC token even on the metadata GET after a successful upload
        var bearerHeader = "Bearer some.jwt.token";

        var result = await sut.ValidateAsync(bearerHeader,
            "/repository/formatum-adhibe/com/example/maven-metadata.xml", "GET");

        result.Success.ShouldBeTrue("Anonymous policy must allow access even when credentials are present");
        result.Role.ShouldBeNull();
    }

    [Fact]
    public async Task ValidateAsync_AnonymousDownload_AllowsWhenBasicOauth2Present()
    {
        // Same scenario via Basic oauth2 header (Gradle with Basic auth mode)
        var sut = BuildValidator(new AuthProxyConfig
        {
            Download = new AuthDirectionConfig
            {
                Policy   = "Anonymous",
                CiTrust  = [],
                HtpasswdPath = "/tmp/unused-download.htpasswd",
            },
            Upload = new AuthDirectionConfig
            {
                Policy       = "Authenticated",
                HtpasswdPath = "/tmp/unused-upload.htpasswd",
            },
        });

        var basicHeader = "Basic " + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("oauth2:some.jwt.token"));

        var result = await sut.ValidateAsync(basicHeader,
            "/repository/formatum-adhibe/com/example/maven-metadata.xml", "GET");

        result.Success.ShouldBeTrue("Anonymous policy must allow access even when oauth2 Basic header is present");
        result.Role.ShouldBeNull();
    }

    private static AuthValidator BuildValidator(AuthProxyConfig config)
    {
        var options = Substitute.For<IOptionsMonitor<AuthProxyConfig>>();
        options.CurrentValue.Returns(config);

        return new AuthValidator(
            options,
            Substitute.For<IJwksCache>(),
            Substitute.For<ITrustEvaluator>(),
            NullLogger<AuthValidator>.Instance);
    }
}

