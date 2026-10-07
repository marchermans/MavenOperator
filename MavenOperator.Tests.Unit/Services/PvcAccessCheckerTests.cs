using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Status;
using MavenOperator.Services;
using MavenOperator.Entities.Spec;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace MavenOperator.Tests.Unit.Services;

public sealed class PvcAccessCheckerTests
{
    private const string Ns = "test-ns";
    private const string RepoName = "target-repo";
    private const string PvcName = "target-repo-pvc";

    private static (PvcAccessChecker Checker, IKubernetesClient K8s) Build(
        V1PersistentVolumeClaim? pvc = null,
        List<V1Pod>? pods = null)
    {
        var k8s = Substitute.For<IKubernetesClient>();
        k8s.GetAsync<V1PersistentVolumeClaim>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(pvc);
        // KubeOps v10 signature: ListAsync<T>(string ns, string? selector = null, CancellationToken ct).
        k8s.ListAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(pods ?? []);

        return (new PvcAccessChecker(k8s, NullLogger<PvcAccessChecker>.Instance), k8s);
    }

    private static MavenRepositoryV1Alpha1 Target(string? accessMode = null)
    {
        var target = new MavenRepositoryV1Alpha1();
        target.Metadata.Name = RepoName;
        target.Metadata.NamespaceProperty = Ns;
        target.Spec = new MavenRepositorySpec
        {
            Storage = accessMode is null ? null : new StorageSpec { AccessMode = accessMode },
        };

        return target;
    }

    private static ImportOptionsSpec Options(ImportTransferMode mode) => new() { TransferMode = mode };

    private static V1Pod Pod(string name, string? pvcClaimName, string phase) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = name },
            Spec = new V1PodSpec
            {
                Volumes = pvcClaimName is null
                    ? []
                    : [new V1Volume { PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = pvcClaimName } }],
            },
            Status = new V1PodStatus { Phase = phase },
        };

    private static V1PersistentVolumeClaim Pvc(params string[] accessModes) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = PvcName },
            Spec = new V1PersistentVolumeClaimSpec { AccessModes = [..accessModes] },
        };

    // ── Explicit transfer mode overrides ────────────────────────────────────

    [Fact]
    public async Task Resolve_HonorsExplicitHttpMode()
    {
        var (checker, _) = Build();
        (await checker.ResolveTransferModeAsync(Target(), Ns, Options(ImportTransferMode.Http), CancellationToken.None))
            .ShouldBe(ResolvedTransferMode.Http);
    }

    [Fact]
    public async Task Resolve_HonorsExplicitDirectWriteMode()
    {
        var (checker, _) = Build();
        (await checker.ResolveTransferModeAsync(Target(), Ns, Options(ImportTransferMode.DirectWrite), CancellationToken.None))
            .ShouldBe(ResolvedTransferMode.DirectWrite);
    }

    // ── Auto mode: spec/actual access modes ──────────────────────────────────

    [Fact]
    public async Task Resolve_Auto_SpecSaysRwx_UsesDirectWriteWithoutProbing()
    {
        var (checker, k8s) = Build();
        var result = await checker.ResolveTransferModeAsync(Target("ReadWriteMany"), Ns, Options(ImportTransferMode.Auto), CancellationToken.None);

        result.ShouldBe(ResolvedTransferMode.DirectWrite);
        k8s.DidNotReceive()
            .GetAsync<V1PersistentVolumeClaim>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_Auto_ActualPvcIsRwx_UsesDirectWrite()
    {
        var (checker, _) = Build(pvc: Pvc("ReadWriteMany"));
        // Spec says RWO so the checker must probe the actual PVC.
        (await checker.ResolveTransferModeAsync(Target("ReadWriteOnce"), Ns, Options(ImportTransferMode.Auto), CancellationToken.None))
            .ShouldBe(ResolvedTransferMode.DirectWrite);
    }

    [Fact]
    public async Task Resolve_Auto_PvcNotYetCreated_DefaultsToDirectWrite()
    {
        var (checker, _) = Build(pvc: null);
        // Spec says RWO; PVC missing → default DirectWrite.
        (await checker.ResolveTransferModeAsync(Target("ReadWriteOnce"), Ns, Options(ImportTransferMode.Auto), CancellationToken.None))
            .ShouldBe(ResolvedTransferMode.DirectWrite);
    }

    // ── Auto mode: RWO PVC binding probes ────────────────────────────────────

    [Fact]
    public async Task Resolve_Auto_RwoBoundToRunningPod_FallsBackToHttp()
    {
        var (checker, _) = Build(
            pvc: Pvc("ReadWriteOnce"),
            pods: [Pod("target-repo-nginx-0", PvcName, "Running")]);

        (await checker.ResolveTransferModeAsync(Target("ReadWriteOnce"), Ns, Options(ImportTransferMode.Auto), CancellationToken.None))
            .ShouldBe(ResolvedTransferMode.Http);
    }

    [Fact]
    public async Task Resolve_Auto_RwoPendingPodAlsoCountsAsClaimed()
    {
        var (checker, _) = Build(
            pvc: Pvc("ReadWriteOnce"),
            pods: [Pod("target-repo-nginx-0", PvcName, "Pending")]);

        (await checker.ResolveTransferModeAsync(Target("ReadWriteOnce"), Ns, Options(ImportTransferMode.Auto), CancellationToken.None))
            .ShouldBe(ResolvedTransferMode.Http);
    }

    [Fact]
    public async Task Resolve_Auto_RwoUnbound_UsesDirectWrite()
    {
        var (checker, _) = Build(pvc: Pvc("ReadWriteOnce")); // no pods at all
        (await checker.ResolveTransferModeAsync(Target("ReadWriteOnce"), Ns, Options(ImportTransferMode.Auto), CancellationToken.None))
            .ShouldBe(ResolvedTransferMode.DirectWrite);
    }

    [Fact]
    public async Task Resolve_Auto_TerminatedPodDoesNotBlock_UsesDirectWrite()
    {
        var (checker, _) = Build(
            pvc: Pvc("ReadWriteOnce"),
            pods: [
                Pod("target-repo-nginx-old", PvcName, "Succeeded"),
                Pod("other-pod", null, "Running")]);

        (await checker.ResolveTransferModeAsync(Target("ReadWriteOnce"), Ns, Options(ImportTransferMode.Auto), CancellationToken.None))
            .ShouldBe(ResolvedTransferMode.DirectWrite);
    }

    // ── IsPvcRwoBoundToRunningPodAsync standalone ────────────────────────────

    [Fact]
    public async Task IsPvcRwoBound_FalseWhenNoPodUsesClaim()
    {
        var (checker, _) = Build(pods: [Pod("unrelated", "other-pvc", "Running")]);
        (await checker.IsPvcRwoBoundToRunningPodAsync(PvcName, Ns, CancellationToken.None)).ShouldBeFalse();
    }
}

