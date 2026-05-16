using Shouldly;
using MavenOperator.Services;
namespace MavenOperator.Tests.Unit.Services;
public sealed class HtpasswdServiceTests
{
    private readonly IHtpasswdService _sut = new HtpasswdService();
    // ── HashPassword ─────────────────────────────────────────────────────────
    [Fact]
    public void HashPassword_ReturnsLineWithUsernamePrefix()
    {
        var line = _sut.HashPassword("deployer", "s3cr3t");
        line.ShouldStartWith("deployer:");
    }
    [Fact]
    public void HashPassword_ProducesBcryptHash()
    {
        var line = _sut.HashPassword("ci-bot", "password123");
        var hash = line.Split(':')[1];
        // bcrypt hashes start with $2y$ (or $2a$)
        hash.ShouldStartWith("$2");
    }
    [Fact]
    public void HashPassword_TwoCalls_ProduceDifferentHashes_ForSamePassword()
    {
        // bcrypt uses random salt — each call must produce a unique hash
        var line1 = _sut.HashPassword("user", "same-password");
        var line2 = _sut.HashPassword("user", "same-password");
        line1.ShouldNotBe(line2);
    }
    [Theory]
    [InlineData("", "password")]
    [InlineData("  ", "password")]
    [InlineData("user", "")]
    [InlineData("user", "  ")]
    public void HashPassword_Throws_WhenUsernameOrPasswordIsBlank(string username, string password)
    {
        Should.Throw<ArgumentException>(() => _sut.HashPassword(username, password));
    }
    // ── BuildHtpasswd ────────────────────────────────────────────────────────
    [Fact]
    public void BuildHtpasswd_ReturnsOneLinePerUser()
    {
        var credentials = new[]
        {
            ("deployer", "pw1"),
            ("ci-bot",   "pw2"),
            ("admin",    "pw3"),
        };
        var result = _sut.BuildHtpasswd(credentials);
        var lines  = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(3);
        lines[0].ShouldStartWith("deployer:");
        lines[1].ShouldStartWith("ci-bot:");
        lines[2].ShouldStartWith("admin:");
    }
    [Fact]
    public void BuildHtpasswd_EmptyCredentials_ReturnsEmptyString()
    {
        var result = _sut.BuildHtpasswd([]);
        result.ShouldBe(string.Empty);
    }
    [Fact]
    public void BuildHtpasswd_Throws_OnDuplicateUsername()
    {
        var credentials = new[]
        {
            ("deployer", "pw1"),
            ("deployer", "pw2"),   // duplicate
        };
        Should.Throw<ArgumentException>(() => _sut.BuildHtpasswd(credentials))
              .Message.ShouldContain("deployer");
    }
    [Fact]
    public void BuildHtpasswd_DuplicateCheck_IsCaseInsensitive()
    {
        var credentials = new[]
        {
            ("Admin", "pw1"),
            ("admin", "pw2"),   // same logical name, different case
        };
        Should.Throw<ArgumentException>(() => _sut.BuildHtpasswd(credentials));
    }
    [Fact]
    public void BuildHtpasswd_HashesAreVerifiable_ByBcrypt()
    {
        // Proves the hash written is a real bcrypt hash NGINX can verify
        var result = _sut.BuildHtpasswd([("user", "correcthorsebatterystaple")]);
        var hash   = result.Split(':')[1].Trim();
        BCrypt.Net.BCrypt.Verify("correcthorsebatterystaple", hash).ShouldBeTrue();
        BCrypt.Net.BCrypt.Verify("wrongpassword", hash).ShouldBeFalse();
    }
}
