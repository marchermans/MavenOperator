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

