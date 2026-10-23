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
        var result = _sut.BuildHtpasswd(Array.Empty<(string, string)>());
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

    // ── BuildHtpasswdStable ────────────────────────────────────────────────

    [Fact]
    public void BuildHtpasswdStable_UnchangedCredentials_ReturnsIdenticalBytes()
    {
        var credentials = new[] { ("deployer", "pw1"), ("ci-bot", "pw2") };
        var first  = _sut.BuildHtpasswd(credentials);
        // A second reconcile with the same spec must not re-salt the hashes —
        // byte-stability is what keeps the operator's config hash stable.
        var second = _sut.BuildHtpasswdStable(credentials, first);
        second.ShouldBe(first);
    }

    [Fact]
    public void BuildHtpasswdStable_ChangedPassword_RehashesOnlyThatLine()
    {
        var original = _sut.BuildHtpasswd(new[] { ("deployer", "pw1"), ("ci-bot", "pw2") });
        var lines = original.Split('\n');

        var updated  = _sut.BuildHtpasswdStable(
            new[] { ("deployer", "pw1"), ("ci-bot", "CHANGED") }, original);
        var newLines = updated.Split('\n');

        // Unchanged user keeps the exact same bcrypt line (same salt).
        newLines[0].ShouldBe(lines[0]);
        // Changed user gets a fresh hash that verifies against the NEW password.
        newLines[1].ShouldStartWith("ci-bot:");
        newLines[1].ShouldNotBe(lines[1]);
        BCrypt.Net.BCrypt.Verify("CHANGED", newLines[1].Split(':')[1]).ShouldBeTrue();
    }

    [Fact]
    public void BuildHtpasswdStable_NewUser_Appended_OldLinesPreserved()
    {
        var original = _sut.BuildHtpasswd(new[] { ("deployer", "pw1") });

        var updated = _sut.BuildHtpasswdStable(
            new[] { ("deployer", "pw1"), ("admin", "pw3") }, original);
        var newLines = updated.Split('\n');

        newLines.Length.ShouldBe(2);
        newLines[0].ShouldBe(original);   // old line reused byte-for-byte
        newLines[1].ShouldStartWith("admin:");
    }

    [Fact]
    public void BuildHtpasswdStable_RemovedUser_Dropped()
    {
        var original = _sut.BuildHtpasswd(new[] { ("deployer", "pw1"), ("ci-bot", "pw2") });

        var updated = _sut.BuildHtpasswdStable(
            new[] { ("deployer", "pw1") }, original);

        updated.Split('\n').Length.ShouldBe(1);
        updated.ShouldStartWith("deployer:");
    }

    [Fact]
    public void BuildHtpasswdStable_MalformedExistingLine_Rehashed()
    {
        const string bogus = "user:not-a-bcrypt-hash";

        var result = _sut.BuildHtpasswdStable(new[] { ("user", "pw1") }, bogus);

        // Unverifiable line is replaced by a real, verifiable bcrypt hash.
        BCrypt.Net.BCrypt.Verify("pw1", result.Split(':')[1]).ShouldBeTrue();
    }

    [Fact]
    public void BuildHtpasswdStable_NoExistingContent_HashesAllUsers()
    {
        // With no previous file there is nothing to reuse — every user must be
        // freshly hashed (two independent fresh calls can't be compared byte-wise:
        // bcrypt salts are random by design).
        var credentials = new[] { ("deployer", "pw1"), ("ci-bot", "pw2") };
        var result = _sut.BuildHtpasswdStable(credentials, null);
        var lines  = result.Split('\n');

        lines.Length.ShouldBe(2);
        BCrypt.Net.BCrypt.Verify("pw1", lines[0].Split(':')[1]).ShouldBeTrue();
        BCrypt.Net.BCrypt.Verify("pw2", lines[1].Split(':')[1]).ShouldBeTrue();
    }

    [Fact]
    public void BuildHtpasswdStable_ReuseIsCaseInsensitive()
    {
        var original = _sut.BuildHtpasswd(new[] { ("Deployer", "pw1") });

        // Same logical user, different case — the previous hash must be reused,
        // not re-salted.
        var updated = _sut.BuildHtpasswdStable(new[] { ("deployer", "pw1") }, original);

        var line   = updated.Split('\n')[0];
        line.ShouldStartWith("deployer:");
        line.Split(':')[1].ShouldBe(original.Split(':')[1]); // identical salt/hash
    }

    // ── Pre-hashed entries (bcryptHash passthrough) ───────────────────────────

    [Fact]
    public void BuildHtpasswd_PrehashedEntry_WrittenVerbatim_NoRehashing()
    {
        var hash   = BCrypt.Net.BCrypt.HashPassword("s3cr3t", 10);
        var result = _sut.BuildHtpasswd([CredentialEntry.FromBcryptHash("deployer", hash)]);

        // The exact supplied hash must appear in the file — no re-salting.
        result.ShouldBe($"deployer:{hash}");
    }

    [Fact]
    public void BuildHtpasswd_PrehashedLine_IsVerifiable_ByBcrypt()
    {
        var hash   = BCrypt.Net.BCrypt.HashPassword("correcthorsebatterystaple", 10);
        var result = _sut.BuildHtpasswd([CredentialEntry.FromBcryptHash("user", hash)]);

        // Proves the passthrough line is a real bcrypt hash NGINX can verify.
        BCrypt.Net.BCrypt.Verify("correcthorsebatterystaple", result.Split(':')[1]).ShouldBeTrue();
    }

    [Fact]
    public void BuildHtpasswd_MixedPlaintextAndPrehashed_BuildsBothLineKinds()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword("pre-hashed-pw", 10);

        var result = _sut.BuildHtpasswd([
            CredentialEntry.FromPlainText("plain-user", "plain-pw"),
            CredentialEntry.FromBcryptHash("hashed-user", hash),
        ]);
        var lines  = result.Split('\n');

        lines.Length.ShouldBe(2);
        // Plain-text user is freshly hashed by the operator.
        BCrypt.Net.BCrypt.Verify("plain-pw", lines[0].Split(':')[1]).ShouldBeTrue();
        // Pre-hashed entry passes through verbatim.
        lines[1].ShouldBe($"hashed-user:{hash}");
    }

    [Fact]
    public void BuildHtpasswd_Throws_OnDuplicateUsername_AcrossModes()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword("pw", 10);

        Should.Throw<ArgumentException>(() => _sut.BuildHtpasswd([
            CredentialEntry.FromPlainText("deployer", "plain-pw"),
            CredentialEntry.FromBcryptHash("deployer", hash),
        ])).Message.ShouldContain("deployer");
    }

    [Fact]
    public void BuildHtpasswdStable_PrehashedUnchanged_ReturnsIdenticalBytes()
    {
        var hash   = BCrypt.Net.BCrypt.HashPassword("pw", 10);
        var first  = _sut.BuildHtpasswd([CredentialEntry.FromBcryptHash("user", hash)]);

        // Re-reconciling with the same pre-hashed entry must not change a single byte.
        var second = _sut.BuildHtpasswdStable([CredentialEntry.FromBcryptHash("user", hash)], first);

        second.ShouldBe(first);
    }

    [Fact]
    public void BuildHtpasswdStable_PrehashedChanged_ReplacesLineWithNewHash()
    {
        var oldHash = BCrypt.Net.BCrypt.HashPassword("old-pw", 10);
        var newHash = BCrypt.Net.BCrypt.HashPassword("new-pw", 10);

        var original = _sut.BuildHtpasswd([CredentialEntry.FromBcryptHash("user", oldHash)]);
        var updated  = _sut.BuildHtpasswdStable([CredentialEntry.FromBcryptHash("user", newHash)], original);

        updated.ShouldBe($"user:{newHash}");
    }

    [Fact]
    public void BuildHtpasswdStable_MixedModes_KeepsBothGuarantees()
    {
        var prehash = BCrypt.Net.BCrypt.HashPassword("pre-hashed-pw", 10);
        var entries = new[]
        {
            CredentialEntry.FromPlainText("plain-user", "plain-pw"),
            CredentialEntry.FromBcryptHash("hashed-user", prehash),
        };

        // First build, then a stable rebuild with identical input — must be byte-identical.
        var first  = _sut.BuildHtpasswd(entries);
        var second = _sut.BuildHtpasswdStable(entries, first);
        second.ShouldBe(first);

        // Now change both: the plain-text user gets re-hashed (verifiable against the new
        // password), the pre-hashed user swaps to the newly supplied hash verbatim.
        var changedHash = BCrypt.Net.BCrypt.HashPassword("changed-pre-hash", 10);
        var updated     = _sut.BuildHtpasswdStable([
            CredentialEntry.FromPlainText("plain-user", "CHANGED"),
            CredentialEntry.FromBcryptHash("hashed-user", changedHash),
        ], second);

        var lines = updated.Split('\n');
        BCrypt.Net.BCrypt.Verify("CHANGED", lines[0].Split(':')[1]).ShouldBeTrue();
        lines[1].ShouldBe($"hashed-user:{changedHash}");
    }
}
