using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MavenOperator.AuthProxy;
using MavenOperator.AuthProxy.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Shouldly;

namespace MavenOperator.Tests.Integration.Hardening;

/// <summary>
/// Integration tests for Phase 6B — Enhanced Authentication (auth proxy sidecar).
///
/// These tests spin up the <c>MavenOperator.AuthProxy</c> ASP.NET Core app in-process
/// via <see cref="WebApplicationFactory{T}"/> and call <c>GET /auth/validate</c> with
/// synthetic credentials. The JWKS cache is replaced with a test double so no real
/// OIDC issuer is contacted.
///
/// Run with: INTEGRATION_TESTS=true dotnet test --filter Category=Integration
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthProxyIntegrationTests : IClassFixture<AuthProxyIntegrationTests.Factory>
{
    // ── WebApplicationFactory ────────────────────────────────────────────────

    /// <summary>
    /// Custom factory that registers an in-memory JWKS cache backed by an RSA key
    /// we control, so tests can mint JWTs that the auth proxy considers valid.
    /// </summary>
    public sealed class Factory : WebApplicationFactory<MavenOperator.AuthProxy.AuthProxyEntryPoint>
    {
        // The RSA key-pair used to sign test JWTs.
        public readonly RSA Rsa = RSA.Create(2048);
        public const string TestKid = "integration-test-kid";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                // Remove the real IJwksCache and replace with our stub
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IJwksCache));
                if (descriptor is not null)
                    services.Remove(descriptor);

                var stub = Substitute.For<IJwksCache>();
                stub.GetKeysAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo =>
                    {
                        // Return the test public key so JWT signature validation succeeds
                        IReadOnlyList<SecurityKey> keys =
                        [
                            new RsaSecurityKey(Rsa.ExportParameters(false))
                            {
                                KeyId = TestKid,
                            }
                        ];
                        return Task.FromResult(keys);
                    });

                services.AddSingleton(stub);

                // Configure ciTrust bindings that the tests rely on
                services.Configure<AuthProxyConfig>(config =>
                {
                    var bindings = new List<CiTrustBindingConfig>
                    {
                        // GitHub Actions binding
                        new()
                        {
                            Platform = "github-actions",
                            Role     = "deployer",
                            Claims   = new Dictionary<string, string>
                            {
                                { "repository", "acme-org/my-lib" },
                            },
                        },
                        // GitLab CI binding — requires ref_protected: true
                        new()
                        {
                            Platform = "gitlab",
                            Role     = "reader",
                            Claims   = new Dictionary<string, string>
                            {
                                { "project_path", "acme-group/my-lib" },
                                { "ref_protected", "true" },
                            },
                        },
                    };

                    config.Download.CiTrust = bindings;
                    config.Upload.CiTrust = bindings;
                });
            });
        }
    }

    // ── JWT helper ──────────────────────────────────────────────────────────

    private readonly Factory _factory;

    public AuthProxyIntegrationTests(Factory factory)
    {
        _factory = factory;
    }

    private string MintJwt(
        string issuer,
        IEnumerable<Claim> claims,
        bool expired     = false,
        string? audience = null)
    {
        var key   = new RsaSecurityKey(_factory.Rsa) { KeyId = Factory.TestKid };
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var allClaims = claims.ToList();
        if (audience is not null)
            allClaims.Add(new Claim("aud", audience));

        var handler    = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer            = issuer,
            Subject           = new ClaimsIdentity(allClaims),
            NotBefore         = expired ? DateTime.UtcNow.AddHours(-2) : DateTime.UtcNow.AddMinutes(-1),
            Expires           = expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1),
            SigningCredentials = creds,
        };
        return handler.CreateEncodedJwt(descriptor);
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    // ── Health check ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        var client   = CreateClient();
        var response = await client.GetAsync("/healthz");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ── No credentials → 401 ─────────────────────────────────────────────────

    [Fact]
    public async Task Validate_NoAuthorizationHeader_Returns401()
    {
        var client   = CreateClient();
        var response = await client.GetAsync("/auth/validate");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            "Missing credentials must yield 401");
    }

    // ── Bearer JWT — GitHub Actions ──────────────────────────────────────────

    [Fact]
    public async Task Validate_GitHubJwt_MatchingRepository_Returns200WithDeployerRole()
    {
        var jwt = MintJwt(
            "https://token.actions.githubusercontent.com",
            [new Claim("repository", "acme-org/my-lib"), new Claim("ref", "refs/heads/main")]);

        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");

        var response = await client.GetAsync("/auth/validate");

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "GitHub JWT matching configured ciTrust binding should return 200");
        response.Headers.ShouldContain(
            h => h.Key == "X-Auth-Role" && h.Value.Contains("deployer"),
            "X-Auth-Role header must be set to 'deployer'");
    }

    [Fact]
    public async Task Validate_GitHubJwt_WrongRepository_Returns403()
    {
        var jwt = MintJwt(
            "https://token.actions.githubusercontent.com",
            [new Claim("repository", "other-org/unrelated-repo")]);

        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");

        var response = await client.GetAsync("/auth/validate");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "GitHub JWT with non-matching 'repository' claim must return 403");
    }

    [Fact]
    public async Task Validate_GitHubJwt_ExpiredToken_Returns403()
    {
        var jwt = MintJwt(
            "https://token.actions.githubusercontent.com",
            [new Claim("repository", "acme-org/my-lib")],
            expired: true);

        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");

        var response = await client.GetAsync("/auth/validate");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "Expired JWT must be rejected with 403");
    }

    [Fact]
    public async Task Validate_GitHubJwt_UnknownIssuer_Returns403()
    {
        var jwt = MintJwt(
            "https://unknown-issuer.example.com",
            [new Claim("repository", "acme-org/my-lib")]);

        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");

        var response = await client.GetAsync("/auth/validate");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "JWT from unknown issuer must return 403");
    }

    // ── Bearer JWT — GitLab CI ──────────────────────────────────────────────

    [Fact]
    public async Task Validate_GitLabJwt_WithProtectedRef_Returns200WithReaderRole()
    {
        var jwt = MintJwt(
            "https://gitlab.com",
            [
                new Claim("project_path", "acme-group/my-lib"),
                new Claim("ref_protected", "true"),
                new Claim("ref", "refs/heads/main"),
            ]);

        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");

        var response = await client.GetAsync("/auth/validate");

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "GitLab JWT with ref_protected=true must match binding and return 200");
        response.Headers.ShouldContain(
            h => h.Key == "X-Auth-Role" && h.Value.Contains("reader"),
            "X-Auth-Role must be 'reader' for this GitLab binding");
    }

    [Fact]
    public async Task Validate_GitLabJwt_UnprotectedRef_Returns403()
    {
        var jwt = MintJwt(
            "https://gitlab.com",
            [
                new Claim("project_path", "acme-group/my-lib"),
                new Claim("ref_protected", "false"),   // binding requires "true"
            ]);

        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");

        var response = await client.GetAsync("/auth/validate");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "GitLab JWT with ref_protected=false must be rejected when binding requires true");
    }

    // ── Basic Auth ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_BasicAuth_NoHtpasswdFile_Returns403()
    {
        // With no htpasswd files present (temp environment), basic auth fails gracefully.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:password"));
        var client  = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Basic {encoded}");

        var response = await client.GetAsync("/auth/validate");

        // The auth proxy returns 403 when credentials are supplied but don't match.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "Basic Auth with no matching htpasswd file should return 403");
    }

    [Fact]
    public async Task Validate_InvalidBase64_Returns403()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Basic !!!invalid_base64!!!");

        var response = await client.GetAsync("/auth/validate");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "Malformed Basic Auth credential must return 403");
    }

    // ── Malformed Bearer ────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_MalformedBearerToken_Returns403()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer this-is-not-a-jwt");

        var response = await client.GetAsync("/auth/validate");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "Malformed Bearer token must return 403");
    }

    // ── Backward-compat: existing Basic Auth flows still work ───────────────

    [Fact]
    public async Task Validate_BasicAuth_WithValidHtpasswdFile_Returns200()
    {
        // Write a temporary htpasswd file so the test can verify the happy path.
        var tmpDir     = Path.Combine(Path.GetTempPath(), $"authproxy-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var htpasswdPath = Path.Combine(tmpDir, "download.htpasswd");

        try
        {
            // BCrypt-hash the password and write a valid htpasswd line
            var hash = BCrypt.Net.BCrypt.HashPassword("e2eReadS3cr3t!");
            await File.WriteAllTextAsync(htpasswdPath, $"reader:{hash}\n");

            // Spin up a factory with the custom htpasswd paths
            await using var factory = new WebApplicationFactory<MavenOperator.AuthProxy.AuthProxyEntryPoint>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureServices(services =>
                    {
                        // Stub the JWKS cache (not needed for Basic Auth test)
                        var desc = services.SingleOrDefault(d => d.ServiceType == typeof(IJwksCache));
                        if (desc is not null) services.Remove(desc);
                        var jwksStub = Substitute.For<IJwksCache>();
                        services.AddSingleton(jwksStub);

                        services.Configure<AuthProxyConfig>(cfg =>
                        {
                            cfg.Download.HtpasswdPath = htpasswdPath;
                            cfg.Upload.HtpasswdPath   = htpasswdPath; // same file for simplicity
                        });
                    });
                });

            var encoded  = Convert.ToBase64String(Encoding.UTF8.GetBytes("reader:e2eReadS3cr3t!"));
            var client   = factory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Basic {encoded}");

            var response = await client.GetAsync("/auth/validate");

            response.StatusCode.ShouldBe(HttpStatusCode.OK,
                "Valid Basic Auth against htpasswd file must return 200");
            response.Headers.ShouldContain(
                h => h.Key == "X-Auth-Role",
                "X-Auth-Role must be set for successful Basic Auth");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}






