using k8s.Models;
using System.Text;
using Shouldly;
using MavenOperator.Services;

namespace MavenOperator.Tests.Unit.Services;

public sealed class CredentialEntryResolverTests
{
    private static V1Secret Secret(params (string Key, string Value)[] data) =>
        new() { Data = data.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetBytes(kv.Value)) };

    // ── Valid shapes ─────────────────────────────────────────────────────────

    [Fact]
    public void FromSecret_UsernameAndPassword_ReturnsPlaintextEntry()
    {
        var entry = CredentialEntryResolver.FromSecret(
            Secret(("username", "deployer"), ("password", "s3cr3t")), "creds");

        entry.Username.ShouldBe("deployer");
        entry.Password.ShouldBe("s3cr3t");
        entry.BcryptHash.ShouldBeNull();
        entry.IsPrehashed.ShouldBeFalse();
    }

    [Fact]
    public void FromSecret_UsernameAndBcryptHash_ReturnsPrehashedEntry()
    {
        var hash  = BCrypt.Net.BCrypt.HashPassword("pre-hashed", 10);
        var entry = CredentialEntryResolver.FromSecret(
            Secret(("username", "deployer"), ("bcryptHash", hash)), "creds");

        entry.Username.ShouldBe("deployer");
        entry.Password.ShouldBeNull();
        entry.BcryptHash.ShouldBe(hash);
        entry.IsPrehashed.ShouldBeTrue();
    }

    // ── Invalid shapes ───────────────────────────────────────────────────────

    [Fact]
    public void FromSecret_BothPasswordAndBcryptHash_Throws()
    {
        var secret = Secret(
            ("username", "deployer"),
            ("password", "s3cr3t"),
            ("bcryptHash", BCrypt.Net.BCrypt.HashPassword("x", 10)));

        Should.Throw<InvalidOperationException>(() => CredentialEntryResolver.FromSecret(secret, "creds"))
              .Message.ShouldContain("'password' and 'bcryptHash'");
    }

    [Fact]
    public void FromSecret_OnlyUsername_Throws()
    {
        var secret = Secret(("username", "deployer"));

        Should.Throw<InvalidOperationException>(() => CredentialEntryResolver.FromSecret(secret, "creds"))
              .Message.ShouldContain("provide either 'password'");
    }

    [Fact]
    public void FromSecret_MissingUsernameKey_Throws()
    {
        var secret = Secret(("password", "s3cr3t"));

        Should.Throw<InvalidOperationException>(() => CredentialEntryResolver.FromSecret(secret, "creds"))
              .Message.ShouldContain("username");
    }

    [Fact]
    public void FromSecret_NullData_Throws()
    {
        var secret = new V1Secret(); // no Data at all

        Should.Throw<InvalidOperationException>(() => CredentialEntryResolver.FromSecret(secret, "creds"));
    }

    [Fact]
    public void FromSecret_MalformedBcryptHashValue_Throws()
    {
        var secret = Secret(("username", "deployer"), ("bcryptHash", "not-a-hash"));

        Should.Throw<ArgumentException>(() => CredentialEntryResolver.FromSecret(secret, "creds"))
              .Message.ShouldContain("bcrypt");
    }
}
