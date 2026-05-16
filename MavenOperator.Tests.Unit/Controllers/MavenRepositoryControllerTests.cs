using Shouldly;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using KubeOps.Abstractions.Reconciliation;
using MavenOperator.Controllers;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
namespace MavenOperator.Tests.Unit.Controllers;
public sealed class MavenRepositoryControllerTests
{
    private readonly IHostedRepositoryReconciler _hosted  = Substitute.For<IHostedRepositoryReconciler>();
    private readonly IProxyRepositoryReconciler  _proxy   = Substitute.For<IProxyRepositoryReconciler>();
    private readonly IVirtualRepositoryReconciler _virtual = Substitute.For<IVirtualRepositoryReconciler>();
    private readonly IKubernetesEventService _events = Substitute.For<IKubernetesEventService>();
    private readonly IKubernetesResourceManager _resources = Substitute.For<IKubernetesResourceManager>();
    private readonly MavenRepositoryController _sut;
    public MavenRepositoryControllerTests()
    {
        _sut = new MavenRepositoryController(
            _hosted, _proxy, _virtual,
            _events, _resources,
            NullLogger<MavenRepositoryController>.Instance);
    }
    [Fact]
    public async Task ReconcileAsync_DispatchesToHostedReconciler_WhenTypeIsHosted()
    {
        var entity = BuildEntity(RepositoryType.Hosted);
        await _sut.ReconcileAsync(entity, CancellationToken.None);
        await _hosted.Received(1).ReconcileAsync(entity, CancellationToken.None);
        await _proxy.DidNotReceive().ReconcileAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<CancellationToken>());
        await _virtual.DidNotReceive().ReconcileAsync(Arg.Any<MavenRepositoryV1Alpha1>(), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task ReconcileAsync_DispatchesToProxyReconciler_WhenTypeIsProxy()
    {
        var entity = BuildEntity(RepositoryType.Proxy);
        await _sut.ReconcileAsync(entity, CancellationToken.None);
        await _proxy.Received(1).ReconcileAsync(entity, CancellationToken.None);
    }
    [Fact]
    public async Task ReconcileAsync_DispatchesToVirtualReconciler_WhenTypeIsVirtual()
    {
        var entity = BuildEntity(RepositoryType.Virtual);
        await _sut.ReconcileAsync(entity, CancellationToken.None);
        await _virtual.Received(1).ReconcileAsync(entity, CancellationToken.None);
    }
    [Fact]
    public async Task ReconcileAsync_SetsPhaseToReady_OnSuccess()
    {
        var entity = BuildEntity(RepositoryType.Hosted);
        await _sut.ReconcileAsync(entity, CancellationToken.None);
        entity.Status.Phase.ShouldBe(RepositoryPhase.Ready);
    }
    [Fact]
    public async Task ReconcileAsync_ReturnsNonNullResult_WhenReconcilerSucceeds()
    {
        var entity = BuildEntity(RepositoryType.Hosted);
        var result = await _sut.ReconcileAsync(entity, CancellationToken.None);
        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue();
    }
    [Fact]
    public async Task ReconcileAsync_SetsPhaseToFailed_WhenReconcilerThrows()
    {
        var entity = BuildEntity(RepositoryType.Hosted);
        _hosted.ReconcileAsync(entity, Arg.Any<CancellationToken>())
               .Returns(Task.FromException(new InvalidOperationException("disk full")));
        var result = await _sut.ReconcileAsync(entity, CancellationToken.None);
        entity.Status.Phase.ShouldBe(RepositoryPhase.Failed);
        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeFalse();
    }
    [Fact]
    public async Task ReconcileAsync_SetsAvailableFalseCondition_WhenReconcilerThrows()
    {
        var entity = BuildEntity(RepositoryType.Hosted);
        _hosted.ReconcileAsync(entity, Arg.Any<CancellationToken>())
               .Returns(Task.FromException(new InvalidOperationException("timeout")));
        await _sut.ReconcileAsync(entity, CancellationToken.None);
        var condition = entity.Status.Conditions.ShouldHaveSingleItem();
        condition.Type.ShouldBe("Available");
        condition.Status.ShouldBe("False");
        condition.Reason.ShouldBe("ReconciliationFailed");
    }
    [Fact]
    public async Task DeletedAsync_CompletesWithoutException()
    {
        var entity = BuildEntity(RepositoryType.Hosted);
        var result = await _sut.DeletedAsync(entity, CancellationToken.None);
        result.ShouldNotBeNull();
    }
    // ── Helpers ──────────────────────────────────────────────────────────────
    private static MavenRepositoryV1Alpha1 BuildEntity(RepositoryType type) =>
        new()
        {
            Metadata = new() { Name = "test-repo", NamespaceProperty = "maven", Generation = 1 },
            Spec     = new MavenRepositorySpec { Type = type },
        };
}
