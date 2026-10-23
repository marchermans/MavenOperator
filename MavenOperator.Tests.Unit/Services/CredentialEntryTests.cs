using Shouldly;
using MavenOperator.Services;

namespace MavenOperator.Tests.Unit.Services;

public sealed class CredentialEntryTests
{
    private static string RealBcryptHash(string password = "s3cr3t") =>
        BCrypt.Net.BCrypt.HashPassword(password, 10);

    /// <summary>53 chars from the bcrypt base64 alphabet — format-valid body for synthetic hashes.</summary>
    private const string FakeBody = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0"; // exactly 53 chars

    // ── FromPlainText ───────────────────────────────────────────────────────

    [Fact]
    public void FromPlainText_CreatesEntryWithPasswordOnly()
    {
        var entry = CredentialEntry.FromPlainText("deployer", "s3cr3t");

        entry.Username.ShouldBe("deployer");
        entry.Password.ShouldBe("s3cr3t");
        entry.BcryptHash.ShouldBeNull();
        entry.IsPrehashed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromPlainText_Throws_WhenUsernameIsBlank(string username)
    {
        Should.Throw<ArgumentException>(() => CredentialEntry.FromPlainText(username, "password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromPlainText_Throws_WhenPasswordIsBlank(string password)
    {
        Should.Throw<ArgumentException>(() => CredentialEntry.FromPlainText("user", password));
    }

    // ── FromBcryptHash ───────────────────────────────────────────────────────

    [Fact]
    public void FromBcryptHash_CreatesEntryWithHashOnly()
    {
        var hash  = RealBcryptHash();
        var entry = CredentialEntry.FromBcryptHash("deployer", hash);

        entry.Username.ShouldBe("deployer");
        entry.Password.ShouldBeNull();
        entry.BcryptHash.ShouldBe(hash);
        entry.IsPrehashed.ShouldBeTrue();
    }

    [Fact]
    public void FromBcryptHash_AcceptsAllSupportedPrefixes()
    {
        // $2a / $2b / $2y are all accepted by NGINX's auth_basic (via crypt(3)).
        CredentialEntry.FromBcryptHash("u", $"$2a$10${FakeBody}");
        CredentialEntry.FromBcryptHash("u", $"$2b$10${FakeBody}");
        CredentialEntry.FromBcryptHash("u", $"$2y$10${FakeBody}");

        // No exception = pass.
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plain-text-password")]
    [InlineData("$1$saltsalt$$53-chars-of-md5-or-apr1-hash-padded-ou")] // MD5/APR1 prefix not accepted
    [InlineData("$2y$99$abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0")]        // cost 99 > 31
    public void FromBcryptHash_Throws_OnMalformedValue(string? hash)
    {
        Should.Throw<ArgumentException>(() => CredentialEntry.FromBcryptHash("user", hash!))
              .Message.ShouldContain("bcrypt");
    }

    [Fact]
    public void FromBcryptHash_Throws_WhenBodyIsTooShort()
    {
        var truncated = RealBcryptHash()[..^1]; // 52 chars instead of 53

        Should.Throw<ArgumentException>(() => CredentialEntry.FromBcryptHash("user", truncated));
    }

    [Fact]
    public void FromBcryptHash_Throws_WhenValueHasTrailingNewline()
    {
        var hashWithNewline = RealBcryptHash() + "\n";

        Should.Throw<ArgumentException>(() => CredentialEntry.FromBcryptHash("user", hashWithNewline));
    }

    [Fact]
    public void FromBcryptHash_Throws_WhenCostBelowMinimum()
    {
        Should.Throw<ArgumentException>(
            () => CredentialEntry.FromBcryptHash("user", $"$2y$03${FakeBody}"));
    }

    // ── IsValidBcryptHash ────────────────────────────────────────────────────

    [Fact]
    public void IsValidBcryptHash_AcceptsRealGeneratedHash()
    {
        CredentialEntry.IsValidBcryptHash(RealBcryptHash()).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("$2y$10$short")]
    [InlineData("not-a-hash-at-all")]
    public void IsValidBcryptHash_RejectsInvalidValues(string? value)
    {
        CredentialEntry.IsValidBcryptHash(value).ShouldBeFalse();
    }
}
