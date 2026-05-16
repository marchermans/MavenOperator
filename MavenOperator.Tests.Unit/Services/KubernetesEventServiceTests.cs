using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace MavenOperator.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="KubernetesEventService"/>.
/// Verifies that events are published correctly and that publishing failures are swallowed.
/// </summary>
public sealed class KubernetesEventServiceTests
{
    private readonly IKubernetesClient _client = Substitute.For<IKubernetesClient>();
    private readonly KubernetesEventService _sut;

    public KubernetesEventServiceTests()
    {
        _sut = new KubernetesEventService(_client, NullLogger<KubernetesEventService>.Instance);
    }

    [Fact]
    public async Task PublishAsync_CreatesEvent_WithCorrectFields()
    {
        var entity = BuildEntity("my-repo", "maven");

        Corev1Event? captured = null;
        await _client.CreateAsync(
            Arg.Do<Corev1Event>(e => captured = e),
            Arg.Any<CancellationToken>());

        await _sut.PublishAsync(entity, "TestReason", "Test message", "Normal");

        await _client.Received(1).CreateAsync(
            Arg.Any<Corev1Event>(), Arg.Any<CancellationToken>());

        captured.ShouldNotBeNull();
        captured.Reason.ShouldBe("TestReason");
        captured.Message.ShouldBe("Test message");
        captured.Type.ShouldBe("Normal");
        captured.InvolvedObject.Kind.ShouldBe("MavenRepository");
        captured.InvolvedObject.Name.ShouldBe("my-repo");
        captured.InvolvedObject.NamespaceProperty.ShouldBe("maven");
        captured.Source!.Component.ShouldBe("maven-operator");
        captured.ReportingComponent.ShouldBe("maven-operator");
        captured.Count.ShouldBe(1);
        captured.ApiVersion.ShouldBe("v1");
        captured.Kind.ShouldBe("Event");
    }

    [Fact]
    public async Task PublishAsync_SetsWarningType_WhenTypeIsWarning()
    {
        var entity = BuildEntity("repo", "ns");
        Corev1Event? captured = null;
        await _client.CreateAsync(
            Arg.Do<Corev1Event>(e => captured = e),
            Arg.Any<CancellationToken>());

        await _sut.PublishAsync(entity, "Failed", "Something went wrong", "Warning");

        captured.ShouldNotBeNull();
        captured.Type.ShouldBe("Warning");
    }

    [Fact]
    public async Task PublishAsync_DoesNotThrow_WhenClientThrows()
    {
        var entity = BuildEntity("repo", "ns");
        _client.CreateAsync(Arg.Any<Corev1Event>(), Arg.Any<CancellationToken>())
               .ThrowsAsync(new Exception("network error"));

        // Must NOT throw — event publishing is best-effort.
        await Should.NotThrowAsync(() =>
            _sut.PublishAsync(entity, "Test", "msg", ct: CancellationToken.None));
    }

    [Fact]
    public async Task PublishAsync_GeneratesUniqueName_PerCall()
    {
        var entity = BuildEntity("repo", "ns");
        var names = new List<string>();
        await _client.CreateAsync(
            Arg.Do<Corev1Event>(e => names.Add(e.Metadata.Name!)),
            Arg.Any<CancellationToken>());

        await _sut.PublishAsync(entity, "R1", "m1");
        await Task.Delay(1); // ensure tick difference
        await _sut.PublishAsync(entity, "R2", "m2");

        names.Count.ShouldBe(2);
        names[0].ShouldNotBe(names[1]);
    }

    private static MavenRepositoryV1Alpha1 BuildEntity(string name, string ns) =>
        new()
        {
            Metadata = new V1ObjectMeta
            {
                Name              = name,
                NamespaceProperty = ns,
                Uid               = "test-uid-123",
                ResourceVersion   = "100",
            },
        };
}

