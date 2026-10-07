using System;
using System.Linq;
using k8s.Models;
using MavenOperator.Controllers;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;
using MavenOperator.Services;
using MavenOperator.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace MavenOperator.Tests.Integration.Import;

/// <summary>
/// In-process reconciliation tests for the MavenRepositoryImport controller against a live cluster.
/// The controller is invoked directly (no operator pod needed); real Jobs are created in-cluster
/// and, where the import Job image is loaded, actually run to completion.
/// </summary>
[Collection(ClusterCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class MavenRepositoryImportIntegrationTests(ClusterFixture cluster)
{
    private readonly ILogger<MavenRepositoryImportController> _logger = NullLogger<MavenRepositoryImportController>.Instance;

    /// <summary>Builds the controller with real collaborators wired to the live cluster client.</summary>
    private MavenRepositoryImportController BuildController() => new(
        cluster.Client,
        new KubernetesEventService(cluster.Client, NullLogger<KubernetesEventService>.Instance),
        new PvcAccessChecker(cluster.Client, NullLogger<PvcAccessChecker>.Instance),
        new ImportJobBuilder(NullLogger<ImportJobBuilder>.Instance),
        _logger);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a Hosted MavenRepository CR and force-marks its status Ready (bypassing NGINX).</summary>
    private async Task<MavenRepositoryV1Alpha1> CreateReadyHostedTargetAsync(
        string name,
        string accessMode = "ReadWriteOnce")
    {
        var repo = await cluster.CreateHostedRepositoryAsync(name, accessMode: accessMode);

        repo.Status.Phase = RepositoryPhase.Ready;
        repo.Status.SetCondition("ProvisioningComplete", true, "ManualTestShortcut", "force-ready for import tests");
        await cluster.Client.UpdateStatusAsync(repo, CancellationToken.None);

        return await cluster.Client.GetAsync<MavenRepositoryV1Alpha1>(name, cluster.Namespace, CancellationToken.None)!;
    }

    private Task<MavenRepositoryImportV1Alpha1> CreateImportAsync(
        string name,
        string targetRepo,
        ImportSourceSpec source,
        ImportOptionsSpec? options = null) =>
        cluster.Client.CreateAsync(new MavenRepositoryImportV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepositoryImport",
            Metadata   = new V1ObjectMeta
            {
                Name              = name,
                NamespaceProperty = cluster.Namespace,
            },
            Spec = new MavenRepositoryImportSpec
            {
                TargetRepository = targetRepo,
                Source           = source,
                Options          = options ?? new ImportOptionsSpec(),
            },
        }, CancellationToken.None);

    private Task<MavenRepositoryImportV1Alpha1> GetImportAsync(string name) =>
        cluster.Client.GetAsync<MavenRepositoryImportV1Alpha1>(name, cluster.Namespace, CancellationToken.None)!;

    /// <summary>Creates the target repository's PVC (normally created by the hosted reconciler) so the import Job can mount it.</summary>
    private Task<V1PersistentVolumeClaim> CreateTargetPvcAsync(string targetRepo) =>
        cluster.CreateRawPvcAsync($"{targetRepo}-pvc");

    // ── Validation-path tests (no import image required) ─────────────────────

    [IntegrationFact(DisplayName = "Reconcile: missing target repository fails the import")]
    public async Task Reconcile_TargetNotFound_FailsImport()
    {
        var controller = BuildController();
        var import     = await CreateImportAsync("imp-no-target", "no-such-repo", new ImportSourceSpec
        {
            PvcSnapshot = new PvcSnapshotSourceSpec { ClaimName = "no-such-pvc" },
        });

        var result = await controller.ReconcileAsync(import, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("not found");

        var after = await GetImportAsync("imp-no-target");
        after.Status.Phase.ShouldBe(ImportPhase.Failed);
        var cond = after.Status.Conditions!.First(c => c.Type == "TargetAvailable");
        cond.Status.ShouldBe("False");
        cond.Reason.ShouldBe("TargetNotFound");
    }

    [IntegrationFact(DisplayName = "Reconcile: target not Ready requeues with 30s backoff")]
    public async Task Reconcile_TargetNotReady_Requeues()
    {
        var controller = BuildController();
        // Created without forcing status — phase starts as Pending.
        await cluster.CreateHostedRepositoryAsync("imp-target-notready");

        var import = await CreateImportAsync("imp-not-ready", "imp-target-notready", new ImportSourceSpec
        {
            PvcSnapshot = new PvcSnapshotSourceSpec { ClaimName = "whatever" },
        });

        var result = await controller.ReconcileAsync(import, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.RequeueAfter!.Value.ShouldBe(TimeSpan.FromSeconds(30));

        var after = await GetImportAsync("imp-not-ready");
        after.Status.Phase.ShouldBe(ImportPhase.Pending);
        var cond = after.Status.Conditions!.First(c => c.Type == "TargetAvailable");
        cond.Status.ShouldBe("False");
        cond.Reason.ShouldBe("TargetNotReady");
    }

    [IntegrationFact(DisplayName = "Reconcile: snapshot source PVC bound to running pod fails the import")]
    public async Task Reconcile_SnapshotSourceRwoConflict_FailsImport()
    {
        var controller = BuildController();
        var target     = await CreateReadyHostedTargetAsync("imp-snap-conflict-target");

        // A raw RWO PVC with a running pod mounted on it → RWO conflict.
        var sourcePvc  = await cluster.CreateRawPvcAsync("imp-snap-src");
        await RunLongRunningPodAsync("imp-snap-holder", "sleep 300", pvcClaimName: "imp-snap-src", readOnly: false);

        var import = await CreateImportAsync("imp-snap-conflict", target.Metadata.Name!, new ImportSourceSpec
        {
            PvcSnapshot = new PvcSnapshotSourceSpec { ClaimName = "imp-snap-src" },
        });

        var result = await controller.ReconcileAsync(import, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();

        var after = await GetImportAsync("imp-snap-conflict");
        after.Status.Phase.ShouldBe(ImportPhase.Failed);
        var cond = after.Status.Conditions!.First(c => c.Type == "SourceAvailable");
        cond.Status.ShouldBe("False");
        cond.Reason.ShouldBe("SourcePvcRwoConflict");
    }

    // ── Job-spec tests (Job is created but does not need to run) ─────────────

    [IntegrationFact(DisplayName = "Reconcile: API source creates Job with expected env and volumes")]
    public async Task ApiSource_CreatesJobWithExpectedSpec()
    {
        var controller = BuildController();
        var target     = await CreateReadyHostedTargetAsync("imp-api-target");
        await CreateTargetPvcAsync(target.Metadata.Name!);
        var creds      = await cluster.CreateCredentialSecretAsync("imp-api-creds", "user", "pass");

        var import = await CreateImportAsync("imp-api-job", target.Metadata.Name!, new ImportSourceSpec
        {
            Api = new ApiSourceSpec
            {
                Type              = ApiSourceType.Reposilite,
                Url               = "https://reposilite.example.com/maven",
                Repository        = "releases",
                CredentialsSecret = creds.Metadata.Name!,
            },
        });

        var result = await controller.ReconcileAsync(import, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();

        var job = await cluster.Client.GetAsync<V1Job>("imp-api-job-import-job", cluster.Namespace, CancellationToken.None)!;
        job.ShouldNotBeNull();

        var env  = job.Spec.Template.Spec.Containers[0].Env!;
        env.First(e => e.Name == "IMPORT_MODE").Value!.ShouldBe("api-reposilite");
        // RWO target PVC with no running pod claiming it → direct write.
        env.First(e => e.Name == "IMPORT_TRANSFER_MODE").Value!.ShouldBe("direct-write");
        env.ShouldContain(e => e.Name == "SOURCE_URL" && e.Value == "https://reposilite.example.com/maven");
        env.ShouldContain(e => e.Name == "SOURCE_REPO" && e.Value == "releases");
        env.First(e => e.Name == "CREDENTIALS_FILE").Value!.ShouldBe("/etc/import-credentials/credentials.json");

        var volumes = job.Spec.Template.Spec.Volumes!;
        volumes.ShouldContain(v => v.PersistentVolumeClaim != null
            && v.PersistentVolumeClaim.ClaimName == $"{target.Metadata.Name!}-pvc"
            && (v.PersistentVolumeClaim.ReadOnlyProperty ?? false) == false);
        volumes.ShouldContain(v => v.Secret != null && v.Secret.SecretName == "imp-api-creds");

        // CR phase should now be Running.
        var after = await GetImportAsync("imp-api-job");
        after.Status.Phase.ShouldBe(ImportPhase.Running);
    }

    [IntegrationFact(DisplayName = "Reconcile: RWO target claimed by running pod falls back to HTTP transfer")]
    public async Task HttpFallback_WhenTargetRwoClaimedByRunningPod()
    {
        var controller = BuildController();
        var target     = await CreateReadyHostedTargetAsync("imp-http-target");
        var targetPvc  = await CreateTargetPvcAsync(target.Metadata.Name!);

        // Simulate the repository's NGINX holding the RWO volume.
        await RunLongRunningPodAsync("imp-http-holder", "sleep 300", pvcClaimName: targetPvc.Metadata.Name!, readOnly: false);

        var creds = await cluster.CreateCredentialSecretAsync("imp-http-creds", "user", "pass");
        var import = await CreateImportAsync("imp-http-fallback", target.Metadata.Name!, new ImportSourceSpec
        {
            Api = new ApiSourceSpec
            {
                Type              = ApiSourceType.Reposilite,
                Url               = "https://reposilite.example.com/maven",
                Repository        = "releases",
                CredentialsSecret = creds.Metadata.Name!,
            },
        });

        var result = await controller.ReconcileAsync(import, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();

        var job = await cluster.Client.GetAsync<V1Job>("imp-http-fallback-import-job", cluster.Namespace, CancellationToken.None)!;
        var env = job.Spec.Template.Spec.Containers[0].Env!;

        env.First(e => e.Name == "IMPORT_TRANSFER_MODE").Value!.ShouldBe("http");
        env.ShouldContain(e => e.Name == "TARGET_HTTP_URL" && e.Value!.Contains($"{target.Metadata.Name}-svc"));
        // No target-data volume in HTTP mode.
        job.Spec.Template.Spec.Volumes!.ShouldNotContain(v => v.PersistentVolumeClaim != null
            && v.PersistentVolumeClaim.ClaimName == $"{target.Metadata.Name!}-pvc");

        var after = await GetImportAsync("imp-http-fallback");
        after.Status.TransferMode.ShouldBe(ResolvedTransferMode.Http);
        var cond = after.Status.Conditions!.First(c => c.Type == "TransferMode");
        cond.Status.ShouldBe("False");
        cond.Reason.ShouldBe("HttpFallback");
    }

    // ── End-to-end tests (import Job must actually run; requires loaded image) ─

    [ImportDirectWriteFact(DisplayName = "Snapshot import: end-to-end PVC copy into target repository")]
    public async Task SnapshotImport_CopiesArtifactsEndToEnd()
    {
        var controller = BuildController();
        var target     = await CreateReadyHostedTargetAsync("imp-snap-target");
        await CreateTargetPvcAsync(target.Metadata.Name!);

        // Seed a raw Maven-layout source PVC.
        var srcName = "imp-snap-src-e2e";
        var source  = await cluster.CreateRawPvcAsync(srcName);
        await cluster.SeedPvcAsync(source.Metadata.Name!, new Dictionary<string, string>
        {
            ["com/example/lib/1.0/lib-1.0.jar"]     = "PK fake jar bytes",
            ["com/example/lib/1.0/lib-1.0.pom.xml"] = "<project/>",
        });

        var import = await CreateImportAsync("imp-snap-e2e", target.Metadata.Name!, new ImportSourceSpec
        {
            // Raw maven layout (no leading repository segment to strip).
            PvcSnapshot = new PvcSnapshotSourceSpec
            {
                ClaimName      = srcName,
                ReposiliteLayout = false,
            },
        });

        var result = await controller.ReconcileAsync(import, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();

        // Job must have the source PVC mounted read-only.
        var job = await cluster.Client.GetAsync<V1Job>("imp-snap-e2e-import-job", cluster.Namespace, CancellationToken.None)!;
        job.Spec.Template.Spec.Volumes!
            .ShouldContain(v => v.PersistentVolumeClaim != null
                && v.PersistentVolumeClaim.ClaimName == srcName
                && (v.PersistentVolumeClaim.ReadOnlyProperty ?? false) == true);

        // Wait for the Job to actually run.
        var finished = await cluster.WaitForJobCompletionAsync("imp-snap-e2e-import-job");
        (finished.Status!.Succeeded ?? 0).ShouldBeGreaterThanOrEqualTo(1);

        // Artifacts must now exist on the target PVC.
        var copied = await cluster.AssertPvcFilesExistAsync(
            $"{target.Metadata.Name!}-pvc",
            new[] { "com/example/lib/1.0/lib-1.0.jar", "com/example/lib/1.0/lib-1.0.pom.xml" },
            expectedFileCount: 2);
        copied.ShouldBeTrue();

        // One more reconciliation syncs the Job outcome into CR status.
        var after = await GetImportAsync("imp-snap-e2e");
        _ = await controller.ReconcileAsync(after, CancellationToken.None);
        after = await GetImportAsync("imp-snap-e2e");
        after.Status.Phase.ShouldBe(ImportPhase.Succeeded);
    }

    [ImportDirectWriteFact(DisplayName = "Live PVC import: scales down Reposilite and restores it afterwards")]
    public async Task LivePvcImport_ScalesDownAndRestoresDeployment()
    {
        var controller = BuildController();
        var target     = await CreateReadyHostedTargetAsync("imp-live-target");
        await CreateTargetPvcAsync(target.Metadata.Name!);

        // Fake "live Reposilite": a plain Deployment holding the source PVC.
        const string deployName  = "imp-live-src-deploy";
        const int    originalReplicas = 2;
        var srcName              = "imp-live-src-pvc";
        var source               = await cluster.CreateRawPvcAsync(srcName);
        await cluster.SeedPvcAsync(source.Metadata.Name!, new Dictionary<string, string>
        {
            ["com/example/lib/1.0/lib-1.0.jar"] = "PK fake jar bytes",
        });

        var deployment = new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = deployName, NamespaceProperty = cluster.Namespace },
            Spec     = new V1DeploymentSpec
            {
                Replicas = originalReplicas,
                Selector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string> { ["app"] = "imp-live-src" },
                },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string> { ["app"] = "imp-live-src" },
                    },
                    Spec = new V1PodSpec
                    {
                        Containers = new List<V1Container>
                        {
                            new V1Container
                            {
                                Name    = "nginx",
                                Image   = "nginx:1.27-alpine",
                                Command = new List<string> { "nginx", "-g", "daemon off;" },
                                VolumeMounts = new List<V1VolumeMount>
                                {
                                    new V1VolumeMount { Name = "data", MountPath = "/usr/share/nginx/html" },
                                },
                            },
                        },
                        Volumes = new List<V1Volume>
                        {
                            new V1Volume
                            {
                                Name   = "data",
                                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = srcName },
                            },
                        },
                    },
                },
            },
        };
        await cluster.Client.CreateAsync(deployment, CancellationToken.None);

        var import = await CreateImportAsync("imp-live-e2e", target.Metadata.Name!, new ImportSourceSpec
        {
            PvcLive = new PvcLiveSourceSpec
            {
                ClaimName           = srcName,
                ReposiliteDeployment = deployName,
            },
        });

        var result = await controller.ReconcileAsync(import, CancellationToken.None);
        if (!result.IsSuccess) throw new InvalidOperationException($"Initial reconcile failed: {result.ErrorMessage}");

        // Deployment must be scaled to 0 while the import runs.
        var duringImport = await cluster.Client.GetAsync<V1Deployment>(deployName, cluster.Namespace, CancellationToken.None)!;
        duringImport.Spec!.Replicas!.Value.ShouldBe(0);
        duringImport.Metadata.Annotations!["maven.operator.io/pre-import-replicas"].ShouldBe(originalReplicas.ToString());

        // Finalizer must be present for scale-up recovery.
        var after = await GetImportAsync("imp-live-e2e");
        after.Metadata.Finalizers!.ShouldContain("maven.operator.io/import-cleanup");

        // Wait for the Job to finish, then reconcile again — this restores replicas + finalizer removal.
        var finished = await cluster.WaitForJobCompletionAsync("imp-live-e2e-import-job");
        (finished.Status!.Succeeded ?? 0).ShouldBeGreaterThanOrEqualTo(1);

        after = await GetImportAsync("imp-live-e2e");
        var second = await controller.ReconcileAsync(after, CancellationToken.None);
        if (!second.IsSuccess) throw new InvalidOperationException($"Status-sync reconcile failed: {second.ErrorMessage}");

        // The same pass restores the deployment and removes the finalizer.
        var restored = await cluster.Client.GetAsync<V1Deployment>(deployName, cluster.Namespace, CancellationToken.None)!;
        restored.Spec!.Replicas!.Value.ShouldBe(originalReplicas);

        after = await GetImportAsync("imp-live-e2e");
        (after.Metadata.Finalizers ?? []).ShouldNotContain("maven.operator.io/import-cleanup");
        after.Status.Phase.ShouldBe(ImportPhase.Succeeded);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    /// <summary>Creates a long-running pod (nginx:alpine) with an arbitrary shell script.</summary>
    private async Task RunLongRunningPodAsync(string name, string script, string pvcClaimName, bool readOnly)
    {
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = cluster.Namespace },
            Spec     = new V1PodSpec
            {
                RestartPolicy  = "Never",
                Containers     = new List<V1Container>
                {
                    new V1Container
                    {
                        Name    = "worker",
                        Image   = "nginx:1.27-alpine",
                        Command = new List<string> { "sh", "-c", script },
                        VolumeMounts = new List<V1VolumeMount>
                        {
                            new V1VolumeMount { Name = "data", MountPath = "/data", ReadOnlyProperty = readOnly },
                        },
                    },
                },
                Volumes = new List<V1Volume>
                {
                    new V1Volume
                    {
                        Name   = "data",
                        PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = pvcClaimName },
                    },
                },
            },
        };

        await cluster.Client.CreateAsync(pod, CancellationToken.None);

        // Wait for it to actually be Running (so the RWO-claim check sees a live pod).
        await ClusterFixture.WaitUntilAsync(
            async () =>
            {
                var p = await cluster.Client.GetAsync<V1Pod>(name, cluster.Namespace, CancellationToken.None);
                return p?.Status?.Phase == "Running";
            },
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(2),
            $"pod {name} becomes Running");
    }
}
