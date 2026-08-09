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
[EntityRbac(typeof(V1ServiceAccount),               Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch | RbacVerb.Create)]
// Note: V1ClusterRoleBinding permissions are granted via Helm chart RBAC, not EntityRbac.
// Using EntityRbac here causes KubeOps to try validating a binding named after the controller.
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

    // Image is injected by Helm at deployment time.
    private static readonly string ImportJobImage =
        Environment.GetEnvironmentVariable("IMPORT_JOB_IMAGE")
            ?? "ghcr.io/marchermans/maven-import-job:latest";

    // ClusterRole name for import jobs — configurable to support both dev and Helm deployments.
    // Dev (config/rbac/import-job.yaml): "maven-operator-import"
    // Helm: "{fullname}-import-job" (e.g., "maven-operator-maven-operator-import-job")
    private static readonly string ImportJobClusterRoleName =
        Environment.GetEnvironmentVariable("IMPORT_JOB_CLUSTER_ROLE_NAME")
            ?? "maven-operator-import";

    // Read imagePullSecrets from both the operator's Pod spec (via env var) and ServiceAccount.
    // This covers all deployment patterns: Helm imagePullSecrets on pod, SA-level secrets, or both.
    private Task<List<V1LocalObjectReference>?>? _operatorImagePullSecretsTask;

    private async Task<List<V1LocalObjectReference>?> GetOperatorImagePullSecretsAsync()
    {
        if (_operatorImagePullSecretsTask is not null)
            return await _operatorImagePullSecretsTask;

        var task = ResolveOperatorImagePullSecretsAsync();
        _operatorImagePullSecretsTask = task;
        return await task;
    }

    private async Task<List<V1LocalObjectReference>?> ResolveOperatorImagePullSecretsAsync()
    {
        var saName = Environment.GetEnvironmentVariable("OPERATOR_SERVICE_ACCOUNT_NAME")
                     ?? "maven-operator";
        var ns     = Environment.GetEnvironmentVariable("OPERATOR_NAMESPACE")
                     ?? "maven-operator-system";

        logger.LogInformation(
            "Resolving imagePullSecret for import jobs (SA={SaName}, NS={Namespace})", saName, ns);

        // 1. Read from env var (pod-level imagePullSecrets injected by Helm)
        var podLevelSecrets = ParseImagePullSecretsFromEnv();

        // 2. Read from ServiceAccount
        List<V1LocalObjectReference>? saLevelSecrets;
        try
        {
            var sa = await k8s.GetAsync<V1ServiceAccount>(saName, ns);
            if (sa?.ImagePullSecrets is not null && sa.ImagePullSecrets.Count > 0)
            {
                logger.LogInformation(
                    "Read {Count} imagePullSecret from operator SA '{SaName}' in namespace '{Namespace}'",
                    sa.ImagePullSecrets.Count, saName, ns);
                saLevelSecrets = new List<V1LocalObjectReference>(sa.ImagePullSecrets);
            }
            else
            {
                logger.LogInformation("No imagePullSecret on operator SA '{SaName}' in namespace '{Namespace}'", saName, ns);
                saLevelSecrets = null;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read operator ServiceAccount '{SaName}' in namespace '{Namespace}'", saName, ns);
            saLevelSecrets = null;
        }

        // 3. Merge both sources, deduplicating by name (pod-level takes precedence for same name)
        var merged = new List<V1LocalObjectReference>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (saLevelSecrets is not null)
        {
            foreach (var s in saLevelSecrets)
            {
                if (seen.Add(s.Name))
                    merged.Add(s);
            }
        }

        if (podLevelSecrets is not null)
        {
            foreach (var s in podLevelSecrets)
            {
                if (seen.Add(s.Name))
                    merged.Add(s);
            }
        }

        if (merged.Count > 0)
        {
            logger.LogInformation("Resolved {Count} imagePullSecret for import jobs: [{Names}] (pod={Pod}, sa={Sa})",
                merged.Count, string.Join(", ", merged.Select(s => s.Name)), podLevelSecrets?.Count ?? 0, saLevelSecrets?.Count ?? 0);
        }
        else
        {
            logger.LogInformation("No imagePullSecret resolved for import jobs");
        }

        return merged.Count > 0 ? merged : null;
    }

    private static List<V1LocalObjectReference>? ParseImagePullSecretsFromEnv()
    {
        var json = Environment.GetEnvironmentVariable("IMAGE_PULL_SECRETS_JSON");
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var secrets = System.Text.Json.JsonSerializer.Deserialize<List<V1LocalObjectReference>>(json);
            if (secrets is not null && secrets.Count > 0)
                Console.WriteLine($"[INFO] Read {secrets.Count} imagePullSecret from IMAGE_PULL_SECRETS_JSON");
            return secrets;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Failed to parse IMAGE_PULL_SECRETS_JSON: {ex.Message}");
            return null;
        }
    }

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
            logger.LogInformation("Checking for existing import Job '{JobName}'", jobName);

            // Check if Job already exists (GetAsync throws on 404 for built-in resources)
            V1Job? existingJob;
            try
            {
                existingJob = await k8s.GetAsync<V1Job>(jobName, ns, cancellationToken);
                logger.LogInformation("Found existing import Job '{JobName}'", jobName);
            }
            catch (k8s.Autorest.HttpOperationException ex)
                when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                existingJob = null;
                logger.LogInformation("No existing import Job found for '{JobName}'", jobName);
            }

            if (existingJob is not null)
            {
                // Sync status from running/completed Job
                await SyncJobStatusAsync(entity, existingJob, ns, cancellationToken);
                await k8s.UpdateStatusAsync(entity, cancellationToken);
                return ReconciliationResult<MavenRepositoryImportV1Alpha1>.Success(entity);
            }

            // --- Pending → Running path ---

            // 1. Validate target repository
            logger.LogInformation("Fetching target MavenRepository '{Target}'", entity.Spec.TargetRepository);
            var target = await k8s.GetAsync<MavenRepositoryV1Alpha1>(
                entity.Spec.TargetRepository, ns, cancellationToken);
            logger.LogInformation("Fetched target: {Name}, phase={Phase}", 
                target?.Metadata.Name ?? "null", target?.Status.Phase.ToString() ?? "null");

            if (target is null)
            {
                entity.Status.SetCondition("TargetAvailable", false,
                    "TargetNotFound",
                    $"Target repository '{entity.Spec.TargetRepository}' not found in namespace '{ns}'");
                entity.Status.Phase = ImportPhase.Failed;

                logger.LogError(
                    "MavenRepositoryImport {Namespace}/{Name} failed: target repository '{Target}' not found in namespace '{Namespace}'",
                    ns, name, entity.Spec.TargetRepository, ns);

                await events.PublishAsync(entity, "TargetNotFound",
                    $"Target repository '{entity.Spec.TargetRepository}' not found", type: "Warning",
                    ct: cancellationToken);
                await k8s.UpdateStatusAsync(entity, cancellationToken);
                return ReconciliationResult<MavenRepositoryImportV1Alpha1>.Failure(entity,
                    $"Target repository '{entity.Spec.TargetRepository}' not found");
            }

            if (target.Status.Phase != RepositoryPhase.Ready)
            {
                entity.Status.SetCondition("TargetAvailable", false,
                    "TargetNotReady",
                    $"Target repository '{entity.Spec.TargetRepository}' is in phase '{target.Status.Phase}' (need Ready)");

                logger.LogInformation(
                    "Requeuing MavenRepositoryImport {Namespace}/{Name}: target repository '{Target}' is not Ready (phase={Phase}). Will retry when target becomes Ready.",
                    ns, name, entity.Spec.TargetRepository, target.Status.Phase);

                // Persist the condition so it's visible in kubectl get.
                await k8s.UpdateStatusAsync(entity, cancellationToken);

                // Return Failure with explicit requeue — we haven't created the Job yet,
                // so reconciliation is incomplete. Use a fixed interval (not exponential backoff)
                // since this is an expected wait for the target to become Ready.
                var result = ReconciliationResult<MavenRepositoryImportV1Alpha1>.Failure(entity,
                    $"Target repository is not Ready (phase={target.Status.Phase})");
                result.RequeueAfter = TimeSpan.FromSeconds(30);
                return result;
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

                    logger.LogError(
                        "MavenRepositoryImport {Namespace}/{Name} failed: source PVC '{Pvc}' is RWO-bound to a running pod. Stop all pods using this PVC before importing.",
                        ns, name, snapshot.ClaimName);

                    await events.PublishAsync(entity, "SourcePvcConflict",
                        $"Source PVC '{snapshot.ClaimName}' is RWO-bound — cannot mount for import",
                        type: "Warning", ct: cancellationToken);
                    await k8s.UpdateStatusAsync(entity, cancellationToken);
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

            // 4a. Ensure import-job ServiceAccount exists in this namespace (lazy init)
            await EnsureImportJobServiceAccountAsync(ns, cancellationToken);

            // 5. Build and create the Job (imagePullSecrets on pod spec)
            // Resolve imagePullSecrets from operator deployment and copy them to the import namespace.
            var desiredSecrets = await GetOperatorImagePullSecretsAsync();

            if (desiredSecrets is null || desiredSecrets.Count == 0)
            {
                logger.LogWarning(
                    "No imagePullSecret resolved for import jobs — Job may fail to pull from private registry");
            }
            else
            {
                foreach (var secretRef in desiredSecrets)
                {
                    await EnsureImagePullSecretCopiedAsync(secretRef.Name, ns, cancellationToken);
                }
            }

            var job = await jobBuilder.BuildJobAsync(
                entity, target, transferMode, ImportJobImage, desiredSecrets, cancellationToken);

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

            await k8s.UpdateStatusAsync(entity, cancellationToken);
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

            try
            {
                await k8s.UpdateStatusAsync(entity, cancellationToken);
            }
            catch (Exception patchEx)
            {
                logger.LogWarning(patchEx, "Failed to patch status for MavenRepositoryImport {Namespace}/{Name}", ns, name);
            }

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

    /// <summary>
    /// Lazily creates the import-job ServiceAccount and ClusterRoleBinding in the given namespace.
    /// This is called before creating an import Job to ensure it has a valid SA to run as.
    /// Idempotent — safe to call every reconciliation.
    /// </summary>
    private async Task EnsureImportJobServiceAccountAsync(
        string ns,
        CancellationToken ct)
    {
        const string saName = "maven-operator-import";

        // Check if SA already exists
        try
        {
            var existing = await k8s.GetAsync<V1ServiceAccount>(saName, ns, ct);
            if (existing is not null)
                return; // already present
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // SA doesn't exist — create it below
        }

        logger.LogInformation(
            "Creating import-job ServiceAccount '{SaName}' in namespace '{Namespace}'", saName, ns);

        var sa = new V1ServiceAccount
        {
            ApiVersion = "v1",
            Kind       = "ServiceAccount",
            Metadata   = new V1ObjectMeta { Name = saName, NamespaceProperty = ns },
        };

        try
        {
            await k8s.CreateAsync(sa, ct);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            logger.LogDebug("ServiceAccount '{SaName}' already exists in namespace '{Namespace}' (race)", saName, ns);
            return;
        }

        // Create ClusterRoleBinding to the cluster-wide import-job ClusterRole.
        var crb = new V1ClusterRoleBinding
        {
            ApiVersion = "rbac.authorization.k8s.io/v1",
            Kind       = "ClusterRoleBinding",
            Metadata   = new V1ObjectMeta { Name = $"{saName}-{ns}" },
            RoleRef    = new V1RoleRef
            {
                ApiGroup = "rbac.authorization.k8s.io",
                Kind     = "ClusterRole",
                Name     = ImportJobClusterRoleName, // configurable via IMPORT_JOB_CLUSTER_ROLE_NAME env var
            },
            Subjects = new List<Rbacv1Subject>
            {
                new()
                {
                    Kind              = "ServiceAccount",
                    Name              = saName,
                    NamespaceProperty = ns,
                },
            },
        };

        try
        {
            await k8s.CreateAsync(crb, ct);
            logger.LogInformation(
                "Created ClusterRoleBinding '{CrbName}' for import-job SA in namespace '{Namespace}'", crb.Metadata.Name, ns);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            logger.LogDebug("ClusterRoleBinding '{CrbName}' already exists (race)", crb.Metadata.Name);
        }
    }

    /// <summary>
    /// Ensures the given Secret exists in the target namespace by copying it from the operator's namespace.
    /// ImagePullSecrets are namespace-scoped — we cannot cross-reference them across namespaces.
    /// Idempotent: if the secret already exists in the target namespace, no action is taken.
    /// </summary>
    private async Task EnsureImagePullSecretCopiedAsync(
        string secretName,
        string targetNamespace,
        CancellationToken ct)
    {
        var operatorNs = Environment.GetEnvironmentVariable("OPERATOR_NAMESPACE")
                         ?? "maven-operator-system";

        logger.LogInformation(
            "Ensuring imagePullSecret '{SecretName}' exists in namespace '{TargetNamespace}' (source: '{OperatorNamespace}')",
            secretName, targetNamespace, operatorNs);

        // Check if already present in target namespace using ListAsync to avoid potential caching issues with GetAsync.
        var allSecrets = await k8s.ListAsync<V1Secret>(targetNamespace, cancellationToken: ct);
        var exists = allSecrets?.Any(s => s.Metadata.Name == secretName) ?? false;
        if (exists)
        {
            logger.LogInformation(
                "imagePullSecret '{SecretName}' already exists in namespace '{TargetNamespace}' — skipping copy",
                secretName, targetNamespace);
            return; // already exists — nothing to do
        }

        logger.LogInformation(
            "imagePullSecret '{SecretName}' not found in namespace '{TargetNamespace}' — will copy from '{OperatorNamespace}'",
            secretName, targetNamespace, operatorNs);

        // Read from operator namespace
        V1Secret source;
        try
        {
            source = await k8s.GetAsync<V1Secret>(secretName, operatorNs, ct);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogWarning(
                "imagePullSecret '{SecretName}' not found in operator namespace '{OperatorNamespace}' — import jobs may fail to pull images",
                secretName, operatorNs);
            return;
        }

        // Create a copy in the target namespace (strip owner references)
        var copy = new V1Secret
        {
            ApiVersion = "v1",
            Kind       = "Secret",
            Metadata   = new V1ObjectMeta
            {
                Name              = secretName,
                NamespaceProperty = targetNamespace,
                Labels            = source.Metadata.Labels?.ToDictionary(l => l.Key, l => l.Value),
                Annotations       = new Dictionary<string, string>
                {
                    ["maven.operator.io/copied-from-namespace"] = operatorNs,
                },
            },
            Type    = source.Type,
            Data    = source.Data?.ToDictionary(d => d.Key, d => d.Value),
            StringData = source.StringData?.ToDictionary(d => d.Key, d => d.Value),
        };

        try
        {
            await k8s.CreateAsync(copy, ct);
            logger.LogInformation(
                "Copied imagePullSecret '{SecretName}' from namespace '{OperatorNamespace}' to '{TargetNamespace}'",
                secretName, operatorNs, targetNamespace);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            logger.LogDebug("Secret '{SecretName}' already exists in namespace '{TargetNamespace}' (race)", secretName, targetNamespace);
        }
    }
}

