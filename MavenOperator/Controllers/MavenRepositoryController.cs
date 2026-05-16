using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;
using MavenOperator.Reconcilers;
using MavenOperator.Services;

namespace MavenOperator.Controllers;

/// <summary>
/// Main KubeOps controller for MavenRepository CRDs.
/// Dispatches reconciliation to the appropriate type-specific reconciler.
/// All reconciler steps are idempotent (safe to run multiple times).
/// </summary>
[EntityRbac(typeof(MavenRepositoryV1Alpha1), Verbs = RbacVerb.All)]
public sealed class MavenRepositoryController(
    IHostedRepositoryReconciler hostedReconciler,
    IProxyRepositoryReconciler proxyReconciler,
    IVirtualRepositoryReconciler virtualReconciler,
    IKubernetesEventService events,
    IKubernetesResourceManager resources,
    ILogger<MavenRepositoryController> logger)
    : IEntityController<MavenRepositoryV1Alpha1>
{
    public async Task<ReconciliationResult<MavenRepositoryV1Alpha1>> ReconcileAsync(
        MavenRepositoryV1Alpha1 entity,
        CancellationToken cancellationToken)
    {
        var ns = entity.Metadata.NamespaceProperty;
        var name = entity.Metadata.Name;

        logger.LogInformation(
            "Reconciling MavenRepository {Namespace}/{Name} (type={Type}, generation={Generation})",
            ns, name, entity.Spec.Type, entity.Metadata.Generation);

        await events.PublishAsync(entity, "ReconcileStarted",
            $"Reconciling {entity.Spec.Type} repository '{name}' (generation {entity.Metadata.Generation})",
            ct: cancellationToken);

        // Mark as provisioning immediately so status reflects in-progress state.
        entity.Status.Phase = RepositoryPhase.Provisioning;

        try
        {
            await (entity.Spec.Type switch
            {
                Entities.Spec.RepositoryType.Hosted  => hostedReconciler.ReconcileAsync(entity, cancellationToken),
                Entities.Spec.RepositoryType.Proxy   => proxyReconciler.ReconcileAsync(entity, cancellationToken),
                Entities.Spec.RepositoryType.Virtual => virtualReconciler.ReconcileAsync(entity, cancellationToken),
                _ => throw new InvalidOperationException(
                    $"Unknown repository type '{entity.Spec.Type}'. Valid values: Hosted, Proxy, Virtual."),
            });

            entity.Status.Phase = RepositoryPhase.Ready;
            entity.Status.ObservedGeneration = entity.Metadata.Generation ?? 0;

            logger.LogInformation(
                "MavenRepository {Namespace}/{Name} reconciled successfully", ns, name);

            await events.PublishAsync(entity, "ReconcileSucceeded",
                $"{entity.Spec.Type} repository '{name}' reconciled successfully",
                ct: cancellationToken);

            return ReconciliationResult<MavenRepositoryV1Alpha1>.Success(entity);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Reconciliation failed for MavenRepository {Namespace}/{Name}", ns, name);

            entity.Status.Phase = RepositoryPhase.Failed;
            entity.Status.SetCondition(
                type: "Available",
                isTrue: false,
                reason: "ReconciliationFailed",
                message: ex.Message);

            await events.PublishAsync(entity, "ReconcileFailed",
                $"Reconciliation failed for '{name}': {ex.Message}",
                type: "Warning",
                ct: cancellationToken);

            // Return failure — KubeOps will requeue with exponential back-off.
            return ReconciliationResult<MavenRepositoryV1Alpha1>.Failure(entity, ex.Message, ex);
        }
    }

    public async Task<ReconciliationResult<MavenRepositoryV1Alpha1>> DeletedAsync(
        MavenRepositoryV1Alpha1 entity,
        CancellationToken cancellationToken)
    {
        var ns   = entity.Metadata.NamespaceProperty!;
        var name = entity.Metadata.Name!;

        logger.LogInformation(
            "MavenRepository {Namespace}/{Name} deleted — child resources will be GC'd by owner references",
            ns, name);

        // For Hosted repos with DeletionPolicy=Delete, explicitly remove the PVC
        // (PVCs do NOT have an owner reference when DeletionPolicy=Retain, the default).
        if (entity.Spec.Type == Entities.Spec.RepositoryType.Hosted
            && entity.Spec.Storage?.DeletionPolicy == DeletionPolicy.Delete)
        {
            var pvcName = $"{name}-pvc";
            logger.LogInformation(
                "DeletionPolicy=Delete: deleting PVC {Namespace}/{PvcName}", ns, pvcName);
            await resources.DeletePvcIfExistsAsync(pvcName, ns, cancellationToken);
        }

        // For Proxy repos with a PVC cache, also clean it up on delete.
        if (entity.Spec.Type == Entities.Spec.RepositoryType.Proxy
            && !string.IsNullOrWhiteSpace(entity.Spec.Upstream?.CachePvcSize))
        {
            var cachePvcName = $"{name}-cache-pvc";
            logger.LogInformation(
                "Deleting proxy cache PVC {Namespace}/{PvcName}", ns, cachePvcName);
            await resources.DeletePvcIfExistsAsync(cachePvcName, ns, cancellationToken);
        }

        return ReconciliationResult<MavenRepositoryV1Alpha1>.Success(entity);
    }
}
