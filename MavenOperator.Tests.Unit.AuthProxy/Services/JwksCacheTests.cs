using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MavenOperator.AuthProxy.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Shouldly;

namespace MavenOperator.Tests.Unit.AuthProxy.Services;

public sealed class JwksCacheTests
{
    private static IMemoryCache CreateCache() =>
        new MemoryCache(new MemoryCacheOptions());

    private static string BuildJwksJson(RSA rsa)
    {
        var key = new RsaSecurityKey(rsa) { KeyId = "test-kid" };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
        var set = new JsonWebKeySet();
        set.Keys.Add(jwk);
        return JsonSerializer.Serialize(new { keys = set.Keys });
    }

    [Fact]
    public async Task GetKeysAsync_FetchesFromHttp_WhenNotCached()
    {
        using var rsa   = RSA.Create(2048);
        var jwksJson    = BuildJwksJson(rsa);

        var mockHttp = Substitute.For<HttpClient>();
        var cache    = CreateCache();

        // Use a real HttpClient pointing to a fake response via a custom handler
        var handler  = new FakeHttpMessageHandler(jwksJson);
        var http     = new HttpClient(handler);
        var sut      = new JwksCache(cache, http, NullLogger<JwksCache>.Instance);

        var keys = await sut.GetKeysAsync("https://example.com/.well-known/jwks");
        keys.ShouldNotBeEmpty();
        keys[0].KeyId.ShouldBe("test-kid");
    }

    [Fact]
    public async Task GetKeysAsync_ReturnsCachedResult_OnSecondCall()
    {
        using var rsa = RSA.Create(2048);
        var jwksJson  = BuildJwksJson(rsa);

        var handler   = new CountingHttpMessageHandler(jwksJson);
        var http      = new HttpClient(handler);
        var cache     = CreateCache();
        var sut       = new JwksCache(cache, http, NullLogger<JwksCache>.Instance);

        _ = await sut.GetKeysAsync("https://example.com/.well-known/jwks");
        _ = await sut.GetKeysAsync("https://example.com/.well-known/jwks");

        // Should only have made 1 HTTP call (second came from cache)
        handler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetKeysAsync_ForceRefresh_HitsHttpEvenIfCached()
    {
        using var rsa = RSA.Create(2048);
        var jwksJson  = BuildJwksJson(rsa);

        var handler   = new CountingHttpMessageHandler(jwksJson);
        var http      = new HttpClient(handler);
        var cache     = CreateCache();
        var sut       = new JwksCache(cache, http, NullLogger<JwksCache>.Instance);

        _ = await sut.GetKeysAsync("https://example.com/.well-known/jwks");
        _ = await sut.GetKeysAsync("https://example.com/.well-known/jwks", forceRefresh: true);

        handler.CallCount.ShouldBe(2);
    }
}

// ── Test HTTP handlers ────────────────────────────────────────────────────────

internal sealed class FakeHttpMessageHandler(string responseJson) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
        });
}

internal sealed class CountingHttpMessageHandler(string responseJson) : HttpMessageHandler
{
    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
        });
    }
}

