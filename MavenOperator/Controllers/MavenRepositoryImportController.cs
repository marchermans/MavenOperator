using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;
using MavenOperator.Services;

namespace MavenOperator.Controllers;

/// <summary>
/// Controller for MavenRepositoryImport CRDs.
/// Orchestrates one-shot Kubernetes Jobs that migrate artifacts from external
/// Maven servers into an operator-managed Hosted repository.
///
/// Lifecycle:
///   Pending  → validate target, resolve mode, scale down (Mode C), create Job → Running
///   Running  → sync Job status into CR status
///   Succeeded/Failed → terminal, skip re-reconcile
/// </summary>
[EntityRbac(typeof(MavenRepositoryImportV1Alpha1), Verbs = RbacVerb.All)]
[EntityRbac(typeof(MavenRepositoryV1Alpha1),       Verbs = RbacVerb.Get)]
[EntityRbac(typeof(V1Job),                          Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Deployment),                   Verbs = RbacVerb.Get | RbacVerb.Patch | RbacVerb.Update)]
[EntityRbac(typeof(V1PersistentVolumeClaim),        Verbs = RbacVerb.Get | RbacVerb.List)]
[EntityRbac(typeof(V1Pod),                          Verbs = RbacVerb.List)]
public sealed class MavenRepositoryImportController(
    IKubernetesClient k8s,
    IKubernetesEventService events,
    IPvcAccessChecker pvcChecker,
    IImportJobBuilder jobBuilder,
    ILogger<MavenRepositoryImportController> logger)
    : IEntityController<MavenRepositoryImportV1Alpha1>
{
    private const string PreImportReplicasAnnotation = "maven.operator.io/pre-import-replicas";
    private const string ImportCleanupFinalizer      = "maven.operator.io/import-cleanup";
    private const string ImportJobImage              = "ghcr.io/marchermans/maven-operator-import-job";

    public async Task<ReconciliationResult<MavenRepositoryImportV1Alpha1>> ReconcileAsync(
        MavenRepositoryImportV1Alpha1 entity,
        CancellationToken cancellationToken)
    {
        var ns   = entity.Metadata.NamespaceProperty!;
        var name = entity.Metadata.Name!;

        logger.LogInformation(
            "Reconciling MavenRepositoryImport {Namespace}/{Name} (phase={Phase})",
            ns, name, entity.Status.Phase);

        // Terminal states — nothing to do
        if (entity.Status.Phase is ImportPhase.Succeeded or ImportPhase.Failed)
        {
            logger.LogDebug(
                "MavenRepositoryImport {Namespace}/{Name} is in terminal phase {Phase} — skipping",
                ns, name, entity.Status.Phase);
            return ReconciliationResult<MavenRepositoryImportV1Alpha1>.Success(entity);
        }

        try
        {
            var jobName = $"{name}-import-job";

            // Check if Job already exists
            var existingJob = await k8s.GetAsync<V1Job>(jobName, ns, cancellationToken);

            if (existingJob is not null)
            {
                // Sync status from running/completed Job
                await SyncJobStatusAsync(entity, existingJob, ns, cancellationToken);
                return ReconciliationResult<MavenRepositoryImportV1Alpha1>.Success(entity);
            }

            // --- Pending → Running path ---

            // 1. Validate target repository
            var target = await k8s.GetAsync<MavenRepositoryV1Alpha1>(
                entity.Spec.TargetRepository, ns, cancellationToken);

            if (target is null)
            {
                entity.Status.SetCondition("TargetAvailable", false,
                    "TargetNotFound",
                    $"Target repository '{entity.Spec.TargetRepository}' not found in namespace '{ns}'");
                entity.Status.Phase = ImportPhase.Failed;
                await events.PublishAsync(entity, "TargetNotFound",
                    $"Target repository '{entity.Spec.TargetRepository}' not found", type: "Warning",
                    ct: cancellationToken);
                return ReconciliationResult<MavenRepositoryImportV1Alpha1>.Failure(entity,
                    $"Target repository '{entity.Spec.TargetRepository}' not found");
            }

            if (target.Status.Phase != RepositoryPhase.Ready)
            {
                entity.Status.SetCondition("TargetAvailable", false,
                    "TargetNotReady",
                    $"Target repository '{entity.Spec.TargetRepository}' is in phase '{target.Status.Phase}' (need Ready)");
                // Requeue until target is ready
                return ReconciliationResult<MavenRepositoryImportV1Alpha1>.Failure(entity,
                    $"Target repository is not Ready (phase={target.Status.Phase})");
            }

            entity.Status.SetCondition("TargetAvailable", true, "TargetReady",
                $"Target repository '{entity.Spec.TargetRepository}' is Ready");

            // 2. Validate source PVC constraints (Mode B: snapshot RWO conflict)
            if (entity.Spec.Source.PvcSnapshot is { } snapshot)
            {
                var rwoConflict = await pvcChecker.IsPvcRwoBoundToRunningPodAsync(
                    snapshot.ClaimName, ns, cancellationToken);
                if (rwoConflict)
                {
                    entity.Status.SetCondition("SourceAvailable", false,
                        "SourcePvcRwoConflict",
                        $"Source PVC '{snapshot.ClaimName}' is ReadWriteOnce and currently bound to a running pod. " +
                        "Stop all pods using this PVC before importing, or use a PVC with ReadWriteMany access mode.");
                    entity.Status.Phase = ImportPhase.Failed;
                    await events.PublishAsync(entity, "SourcePvcConflict",
                        $"Source PVC '{snapshot.ClaimName}' is RWO-bound — cannot mount for import",
                        type: "Warning", ct: cancellationToken);
                    return ReconciliationResult<MavenRepositoryImportV1Alpha1>.Failure(entity,
                        $"Source PVC '{snapshot.ClaimName}' is RWO-bound to a running pod");
                }
            }

            // 3. Mode C: scale down Reposilite Deployment
            if (entity.Spec.Source.PvcLive is { } live && !string.IsNullOrEmpty(live.ReposiliteDeployment))
            {
                var duration = live.ScaleDownDuration;
                if (duration != "0s" && duration != "0")
                {
                    await ScaleDownDeploymentAsync(entity, live.ReposiliteDeployment, ns, cancellationToken);
                }
                else
                {
                    entity.Status.SetCondition("SourceScaledDown", true,
                        "ScaleDownSkipped",
                        "scaleDownDuration=0s — import runs concurrently with Reposilite (Warning: possible read inconsistency)");
                    await events.PublishAsync(entity, "ConcurrentImport",
                        "Running concurrently with Reposilite — possible read inconsistency",
                        type: "Warning", ct: cancellationToken);
                }

                // Ensure finalizer for scale-up recovery
                await EnsureFinalizerAsync(entity, ns, cancellationToken);
            }

            // 4. Resolve transfer mode
            var transferMode = await pvcChecker.ResolveTransferModeAsync(
                target, ns, entity.Spec.Options, cancellationToken);

            if (transferMode == ResolvedTransferMode.Http)
            {
                entity.Status.SetCondition("TransferMode", false,
                    "HttpFallback",
                    "Target PVC is ReadWriteOnce and claimed by NGINX — falling back to HTTP PUT. " +
                    "Consider using a ReadWriteMany StorageClass for better performance.");
                await events.PublishAsync(entity, "HttpFallback",
                    "Import using HTTP PUT (RWO PVC conflict) — performance will be lower than direct PVC write",
                    type: "Warning", ct: cancellationToken);
            }

            entity.Status.TransferMode = transferMode;

            // 5. Build and create the Job
            var job = await jobBuilder.BuildJobAsync(entity, target, transferMode, ImportJobImage, cancellationToken);

            try
            {
                await k8s.CreateAsync(job, cancellationToken);
                logger.LogInformation(
                    "Created import Job {JobName} for {Namespace}/{Name} (mode={TransferMode})",
                    job.Metadata.Name, ns, name, transferMode);
            }
            catch (k8s.Autorest.HttpOperationException ex)
                when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                logger.LogDebug("Import Job {JobName} already exists (race) — continuing", job.Metadata.Name);
            }

            entity.Status.Phase     = ImportPhase.Running;
            entity.Status.StartTime = DateTime.UtcNow;

            await events.PublishAsync(entity, "ImportStarted",
                $"Import Job '{job.Metadata.Name}' created (transferMode={transferMode})",
                ct: cancellationToken);

            return ReconciliationResult<MavenRepositoryImportV1Alpha1>.Success(entity);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Reconciliation failed for MavenRepositoryImport {Namespace}/{Name}", ns, name);

            entity.Status.Phase = ImportPhase.Failed;
            entity.Status.SetCondition("ReconcileSucceeded", false,
                "ReconcileError", ex.Message);
            entity.Status.CompletionTime = DateTime.UtcNow;

            await events.PublishAsync(entity, "ImportFailed",
                $"Import failed: {ex.Message}", type: "Warning", ct: cancellationToken);

            return ReconciliationResult<MavenRepositoryImportV1Alpha1>.Failure(entity, ex.Message, ex);
        }
    }

    public async Task<ReconciliationResult<MavenRepositoryImportV1Alpha1>> DeletedAsync(
        MavenRepositoryImportV1Alpha1 entity,
        CancellationToken cancellationToken)
    {
        var ns   = entity.Metadata.NamespaceProperty!;
        var name = entity.Metadata.Name!;

        logger.LogInformation(
            "MavenRepositoryImport {Namespace}/{Name} deleted — running cleanup finalizer", ns, name);

        // Restore Reposilite replicas if we scaled it down (Mode C)
        if (entity.Spec.Source.PvcLive is { ReposiliteDeployment: { } deployName })
        {
            await RestoreDeploymentReplicasAsync(entity, deployName, ns, cancellationToken);
        }

        return ReconciliationResult<MavenRepositoryImportV1Alpha1>.Success(entity);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task SyncJobStatusAsync(
        MavenRepositoryImportV1Alpha1 entity,
        V1Job job,
        string ns,
        CancellationToken ct)
    {
        var succeeded = job.Status?.Succeeded ?? 0;
        var failed    = job.Status?.Failed    ?? 0;

        if (succeeded > 0)
        {
            entity.Status.Phase          = ImportPhase.Succeeded;
            entity.Status.CompletionTime = job.Status?.CompletionTime ?? DateTime.UtcNow;
            entity.Status.SetCondition("ImportCompleted", true, "JobSucceeded",
                "Import Job completed successfully");

            // Restore Reposilite replicas (Mode C)
            if (entity.Spec.Source.PvcLive is { ReposiliteDeployment: { } deployName })
            {
                await RestoreDeploymentReplicasAsync(entity, deployName, ns, ct);
                await RemoveFinalizerAsync(entity, ns, ct);
            }

            await events.PublishAsync(entity, "ImportSucceeded",
                $"Import completed: {entity.Status.ArtifactsCopied} artifacts copied", ct: ct);
        }
        else if (failed > 0 && job.Spec?.BackoffLimit.HasValue == true
                             && failed > job.Spec.BackoffLimit)
        {
            entity.Status.Phase          = ImportPhase.Failed;
            entity.Status.CompletionTime = DateTime.UtcNow;
            entity.Status.SetCondition("ImportCompleted", false, "JobFailed",
                $"Import Job exceeded backoff limit ({failed} failures)");

            if (entity.Spec.Source.PvcLive is { ReposiliteDeployment: { } deployName })
            {
                await RestoreDeploymentReplicasAsync(entity, deployName, ns, ct);
                await RemoveFinalizerAsync(entity, ns, ct);
            }

            await events.PublishAsync(entity, "ImportFailed",
                $"Import Job failed after {failed} attempts", type: "Warning", ct: ct);
        }
        else
        {
            // Still running — read progress annotations if present
            var annotations = job.Metadata?.Annotations ?? new Dictionary<string, string>();
            if (annotations.TryGetValue("maven.operator.io/artifacts-copied", out var copiedStr)
                && long.TryParse(copiedStr, out var copied))
                entity.Status.ArtifactsCopied = copied;

            if (annotations.TryGetValue("maven.operator.io/artifacts-discovered", out var discoveredStr)
                && long.TryParse(discoveredStr, out var discovered))
                entity.Status.ArtifactsDiscovered = discovered;

            if (annotations.TryGetValue("maven.operator.io/bytes-transferred", out var bytesStr)
                && long.TryParse(bytesStr, out var bytes))
                entity.Status.BytesTransferred = bytes;
        }
    }

    private async Task ScaleDownDeploymentAsync(
        MavenRepositoryImportV1Alpha1 entity,
        string deployName,
        string ns,
        CancellationToken ct)
    {
        var deploy = await k8s.GetAsync<V1Deployment>(deployName, ns, ct);
        if (deploy is null)
        {
            logger.LogWarning(
                "Reposilite Deployment '{DeployName}' not found — skipping scale-down", deployName);
            return;
        }

        var originalReplicas = deploy.Spec?.Replicas ?? 1;

        // Store original replica count in annotation before scaling down
        deploy.Metadata.Annotations ??= new Dictionary<string, string>();
        deploy.Metadata.Annotations[PreImportReplicasAnnotation] = originalReplicas.ToString();
        deploy.Spec!.Replicas = 0;

        await k8s.UpdateAsync(deploy, ct);

        logger.LogInformation(
            "Scaled down Deployment '{DeployName}' from {OriginalReplicas} to 0 for import",
            deployName, originalReplicas);

        entity.Status.SetCondition("SourceScaledDown", true,
            "DeploymentScaledDown",
            $"Deployment '{deployName}' scaled from {originalReplicas} to 0 replicas");

        await events.PublishAsync(entity, "DeploymentScaledDown",
            $"Scaled down '{deployName}' to 0 replicas for safe import", ct: ct);
    }

    private async Task RestoreDeploymentReplicasAsync(
        MavenRepositoryImportV1Alpha1 entity,
        string deployName,
        string ns,
        CancellationToken ct)
    {
        var deploy = await k8s.GetAsync<V1Deployment>(deployName, ns, ct);
        if (deploy is null)
        {
            logger.LogWarning(
                "Deployment '{DeployName}' not found — cannot restore replicas", deployName);
            return;
        }

        // Read original replica count from annotation
        var annotations = deploy.Metadata?.Annotations ?? new Dictionary<string, string>();
        var originalReplicas = annotations.TryGetValue(PreImportReplicasAnnotation, out var s)
                               && int.TryParse(s, out var r) ? r : 1;

        deploy.Spec!.Replicas = originalReplicas;
        if (deploy.Metadata?.Annotations?.ContainsKey(PreImportReplicasAnnotation) == true)
            deploy.Metadata.Annotations.Remove(PreImportReplicasAnnotation);

        await k8s.UpdateAsync(deploy, ct);

        logger.LogInformation(
            "Restored Deployment '{DeployName}' to {Replicas} replicas after import",
            deployName, originalReplicas);

        await events.PublishAsync(entity, "DeploymentRestored",
            $"Restored '{deployName}' to {originalReplicas} replicas after import", ct: ct);
    }

    private async Task EnsureFinalizerAsync(
        MavenRepositoryImportV1Alpha1 entity,
        string ns,
        CancellationToken ct)
    {
        entity.Metadata.Finalizers ??= [];
        if (entity.Metadata.Finalizers.Contains(ImportCleanupFinalizer))
            return;

        entity.Metadata.Finalizers.Add(ImportCleanupFinalizer);
        await k8s.UpdateAsync(entity, ct);
    }

    private async Task RemoveFinalizerAsync(
        MavenRepositoryImportV1Alpha1 entity,
        string ns,
        CancellationToken ct)
    {
        if (entity.Metadata.Finalizers?.Contains(ImportCleanupFinalizer) != true)
            return;

        entity.Metadata.Finalizers.Remove(ImportCleanupFinalizer);
        await k8s.UpdateAsync(entity, ct);
    }
}

