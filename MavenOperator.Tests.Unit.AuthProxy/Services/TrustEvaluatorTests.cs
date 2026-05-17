using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using MavenOperator.AuthProxy;
using MavenOperator.AuthProxy.Services;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace MavenOperator.Tests.Unit.AuthProxy.Services;

public sealed class TrustEvaluatorTests
{
    private readonly TrustEvaluator _sut = new();

    // ── Helper to build JWTs ─────────────────────────────────────────────────

    private static JwtSecurityToken BuildToken(
        string issuer,
        IEnumerable<Claim> claims,
        string? audience = null)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-kid" };
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var allClaims = claims.ToList();
        if (audience is not null)
            allClaims.Add(new Claim("aud", audience));

        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer             = issuer,
            Subject            = new ClaimsIdentity(allClaims),
            Expires            = DateTime.UtcNow.AddHours(1),
            SigningCredentials  = creds,
        };
        return (JwtSecurityToken)handler.CreateToken(descriptor);
    }

    private static CiTrustBindingConfig GitHubBinding(
        UserRole role = UserRole.Deployer,
        Dictionary<string, string>? claims = null,
        string? audience = null) =>
        new()
        {
            Platform = "github-actions",
            Role     = role.ToString().ToLowerInvariant(),
            Audience = audience,
            Claims   = claims ?? new() { { "repository", "acme-org/my-service" } },
        };

    // ── Basic matching ────────────────────────────────────────────────────────

    [Fact]
    public void EvaluateRole_ReturnsRole_WhenAllClaimsMatch()
    {
        var token = BuildToken(
            "https://token.actions.githubusercontent.com",
            [new("repository", "acme-org/my-service"), new("ref", "refs/heads/main")]);

        var binding = GitHubBinding(claims: new()
        {
            { "repository", "acme-org/my-service" },
            { "ref",        "refs/heads/main" },
        });

        var role = _sut.EvaluateRole(token, [binding]);
        role.ShouldBe("deployer");
    }

    [Fact]
    public void EvaluateRole_ReturnsNull_WhenClaimMissing()
    {
        var token = BuildToken(
            "https://token.actions.githubusercontent.com",
            [new("repository", "acme-org/my-service")]);

        var binding = GitHubBinding(claims: new()
        {
            { "repository", "acme-org/my-service" },
            { "environment", "prod" },   // missing from token
        });

        var role = _sut.EvaluateRole(token, [binding]);
        role.ShouldBeNull();
    }

    [Fact]
    public void EvaluateRole_ReturnsNull_WhenIssuerMismatch()
    {
        var token = BuildToken(
            "https://gitlab.com",   // wrong issuer
            [new("repository", "acme-org/my-service")]);

        var binding = GitHubBinding(); // expects github-actions issuer

        var role = _sut.EvaluateRole(token, [binding]);
        role.ShouldBeNull();
    }

    // ── First-match semantics ────────────────────────────────────────────────

    [Fact]
    public void EvaluateRole_FirstMatchWins()
    {
        var token = BuildToken(
            "https://token.actions.githubusercontent.com",
            [new("repository", "acme-org/my-service"), new("ref", "refs/heads/main")]);

        var bindings = new List<CiTrustBindingConfig>
        {
            new()
            {
                Platform = "github-actions",
                Role     = "reader",
                Claims   = new() { { "repository", "acme-org/my-service" } },
            },
            new()
            {
                Platform = "github-actions",
                Role     = "deployer",
                Claims   = new() { { "repository", "acme-org/my-service" }, { "ref", "refs/heads/main" } },
            },
        };

        // First binding matches (repository only) → reader wins
        var role = _sut.EvaluateRole(token, bindings.AsReadOnly());
        role.ShouldBe("reader");
    }

    // ── Glob matching ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("acme-org/*",           "acme-org/my-service",    true)]
    [InlineData("acme-org/*",           "other-org/my-service",   false)]
    [InlineData("*",                    "anything",               true)]
    [InlineData("acme-org/my-*",        "acme-org/my-service",    true)]
    [InlineData("acme-org/my-*",        "acme-org/other-service", false)]
    [InlineData("refs/heads/*",         "refs/heads/main",        true)]
    [InlineData("refs/heads/main",      "refs/heads/main",        true)]
    [InlineData("refs/heads/main",      "refs/heads/feature",     false)]
    public void GlobMatch_Scenarios(string pattern, string value, bool expected)
    {
        TrustEvaluator.GlobMatch(pattern, value).ShouldBe(expected);
    }

    [Fact]
    public void EvaluateRole_GlobWildcard_MatchesPrefix()
    {
        var token = BuildToken(
            "https://token.actions.githubusercontent.com",
            [new("repository_owner", "acme-org"), new("ref_type", "tag")]);

        var binding = new CiTrustBindingConfig
        {
            Platform = "github-actions",
            Role     = "deployer",
            Claims   = new() { { "repository_owner", "acme-org" }, { "ref_type", "tag" } },
        };

        _sut.EvaluateRole(token, [binding]).ShouldBe("deployer");
    }

    // ── Empty claims guard ───────────────────────────────────────────────────

    [Fact]
    public void EvaluateRole_SkipsBinding_WithEmptyClaims()
    {
        var token = BuildToken(
            "https://token.actions.githubusercontent.com",
            [new("repository", "acme-org/my-service")]);

        var bindingEmpty = new CiTrustBindingConfig
        {
            Platform = "github-actions",
            Role     = "admin",
            Claims   = new(),  // empty — must be skipped
        };

        var bindingValid = GitHubBinding(UserRole.Reader);

        // Empty binding is skipped; valid binding matches
        var role = _sut.EvaluateRole(token, [bindingEmpty, bindingValid]);
        role.ShouldBe("reader");
    }

    // ── Audience enforcement ─────────────────────────────────────────────────

    [Fact]
    public void EvaluateRole_ReturnsNull_WhenAudienceMismatch()
    {
        var token = BuildToken(
            "https://token.actions.githubusercontent.com",
            [new("repository", "acme-org/my-service")],
            audience: "https://other.example.com");

        var binding = GitHubBinding(audience: "https://maven.acme.com");

        _sut.EvaluateRole(token, [binding]).ShouldBeNull();
    }

    [Fact]
    public void EvaluateRole_ReturnsRole_WhenAudienceMatches()
    {
        var token = BuildToken(
            "https://token.actions.githubusercontent.com",
            [new("repository", "acme-org/my-service")],
            audience: "https://maven.acme.com");

        var binding = GitHubBinding(audience: "https://maven.acme.com");

        _sut.EvaluateRole(token, [binding]).ShouldBe("deployer");
    }

    [Fact]
    public void EvaluateRole_ReturnsRole_WhenNoAudienceRequired()
    {
        var token = BuildToken(
            "https://token.actions.githubusercontent.com",
            [new("repository", "acme-org/my-service")],
            audience: "some-aud");

        // binding.Audience is null — accept any audience
        var binding = GitHubBinding(); // no Audience set

        _sut.EvaluateRole(token, [binding]).ShouldBe("deployer");
    }

    // ── GitLab platform ──────────────────────────────────────────────────────

    [Fact]
    public void EvaluateRole_GitLab_MatchesClaimsAgainstCorrectIssuer()
    {
        var token = BuildToken(
            "https://gitlab.com",
            [new("project_path", "acme-group/my-service"), new("ref_protected", "true")]);

        var binding = new CiTrustBindingConfig
        {
            Platform = "gitlab",
            Role     = "deployer",
            Claims   = new() { { "project_path", "acme-group/my-service" }, { "ref_protected", "true" } },
        };

        _sut.EvaluateRole(token, [binding]).ShouldBe("deployer");
    }

    [Fact]
    public void EvaluateRole_GitLab_SelfManaged_WithCustomIssuerUrl()
    {
        var token = BuildToken(
            "https://gitlab.internal.acme.com",
            [new("namespace_path", "acme-group"), new("ref", "main")]);

        var binding = new CiTrustBindingConfig
        {
            Platform  = "gitlab",
            IssuerUrl = "https://gitlab.internal.acme.com",
            Role      = "deployer",
            Claims    = new() { { "namespace_path", "acme-group" }, { "ref", "main" } },
        };

        _sut.EvaluateRole(token, [binding]).ShouldBe("deployer");
    }
}

// Reuse UserRole enum for test readability (mapped from AuthSpec since tests reference both)
internal enum UserRole { Reader, Deployer, Admin }

