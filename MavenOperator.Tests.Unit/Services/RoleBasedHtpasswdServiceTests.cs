using MavenOperator.Entities.Spec;
using MavenOperator.Services;
using Shouldly;

namespace MavenOperator.Tests.Unit.Services;

public sealed class RoleBasedHtpasswdServiceTests
{
    private readonly IHtpasswdService          _htpasswd = new HtpasswdService();
    private readonly IRoleBasedHtpasswdService _sut;

    public RoleBasedHtpasswdServiceTests()
    {
        _sut = new RoleBasedHtpasswdService(_htpasswd);
    }

    // ── Download htpasswd — all roles included ────────────────────────────────

    [Fact]
    public void BuildDownloadHtpasswd_IncludesAllRoles()
    {
        var users = new[]
        {
            ("alice", "pw1", UserRole.Reader),
            ("bob",   "pw2", UserRole.Deployer),
            ("carol", "pw3", UserRole.Admin),
        };

        var result = _sut.BuildDownloadHtpasswd(users);
        var lines  = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(3);
        lines[0].ShouldStartWith("alice:");
        lines[1].ShouldStartWith("bob:");
        lines[2].ShouldStartWith("carol:");
    }

    // ── Upload htpasswd — only Deployer and Admin ─────────────────────────────

    [Fact]
    public void BuildUploadHtpasswd_ExcludesReaders()
    {
        var users = new[]
        {
            ("alice", "pw1", UserRole.Reader),    // excluded
            ("bob",   "pw2", UserRole.Deployer),  // included
            ("carol", "pw3", UserRole.Admin),     // included
        };

        var result = _sut.BuildUploadHtpasswd(users);
        var lines  = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(2);
        lines.ShouldAllBe(l => !l.StartsWith("alice:"));
        lines[0].ShouldStartWith("bob:");
        lines[1].ShouldStartWith("carol:");
    }

    [Fact]
    public void BuildUploadHtpasswd_ReturnsEmpty_WhenOnlyReaders()
    {
        var users = new[]
        {
            ("alice", "pw1", UserRole.Reader),
            ("dave",  "pw2", UserRole.Reader),
        };

        var result = _sut.BuildUploadHtpasswd(users);
        result.ShouldBe(string.Empty);
    }

    [Fact]
    public void BuildUploadHtpasswd_IncludesDeployerAndAdmin()
    {
        var users = new[]
        {
            ("deployer-user", "pw1", UserRole.Deployer),
            ("admin-user",    "pw2", UserRole.Admin),
        };

        var result = _sut.BuildUploadHtpasswd(users);
        var lines  = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(2);
    }

    // ── Empty input ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildDownloadHtpasswd_EmptyInput_ReturnsEmpty()
    {
        _sut.BuildDownloadHtpasswd([]).ShouldBe(string.Empty);
    }

    [Fact]
    public void BuildUploadHtpasswd_EmptyInput_ReturnsEmpty()
    {
        _sut.BuildUploadHtpasswd([]).ShouldBe(string.Empty);
    }
}

