using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Entities.Status;

namespace MavenOperator.Services;

/// <inheritdoc/>
public sealed class PvcAccessChecker(
    IKubernetesClient k8s,
    ILogger<PvcAccessChecker> logger)
    : IPvcAccessChecker
{
    public async Task<ResolvedTransferMode> ResolveTransferModeAsync(
        MavenRepositoryV1Alpha1 target,
        string namespaceName,
        ImportOptionsSpec options,
        CancellationToken ct)
    {
        if (options.TransferMode == ImportTransferMode.Http)
            return ResolvedTransferMode.Http;

        if (options.TransferMode == ImportTransferMode.DirectWrite)
            return ResolvedTransferMode.DirectWrite;

        // Auto mode: probe PVC access modes
        var targetPvcName = $"{target.Metadata.Name}-pvc";
        var storage = target.Spec.Storage ?? new StorageSpec();

        // If the StorageSpec says RWX, trust it (PVC may not yet be provisioned)
        if (storage.AccessMode == "ReadWriteMany")
        {
            logger.LogDebug(
                "Target PVC {PvcName} has AccessMode=ReadWriteMany — using DirectWrite",
                targetPvcName);
            return ResolvedTransferMode.DirectWrite;
        }

        // Check actual PVC to see if it's RWO-bound (NGINX already holds the claim)
        var pvc = await k8s.GetAsync<V1PersistentVolumeClaim>(targetPvcName, namespaceName, ct);
        if (pvc is null)
        {
            // PVC not yet created — default to DirectWrite
            logger.LogDebug("Target PVC {PvcName} not found yet — defaulting to DirectWrite", targetPvcName);
            return ResolvedTransferMode.DirectWrite;
        }

        var accessModes = pvc.Spec?.AccessModes ?? [];
        if (accessModes.Contains("ReadWriteMany"))
        {
            logger.LogDebug("Target PVC {PvcName} is RWX — using DirectWrite", targetPvcName);
            return ResolvedTransferMode.DirectWrite;
        }

        // RWO PVC: check if NGINX pod already holds it
        var isBound = await IsPvcRwoBoundToRunningPodAsync(targetPvcName, namespaceName, ct);
        if (isBound)
        {
            logger.LogWarning(
                "Target PVC {PvcName} is RWO and currently claimed by a running pod — falling back to Http",
                targetPvcName);
            return ResolvedTransferMode.Http;
        }

        logger.LogDebug("Target PVC {PvcName} is RWO but not currently bound — using DirectWrite", targetPvcName);
        return ResolvedTransferMode.DirectWrite;
    }

    public async Task<bool> IsPvcRwoBoundToRunningPodAsync(
        string pvcName,
        string namespaceName,
        CancellationToken ct)
    {
        var pods = await k8s.ListAsync<V1Pod>(namespaceName, cancellationToken: ct);
        foreach (var pod in pods)
        {
            if (pod.Status?.Phase is not ("Running" or "Pending"))
                continue;

            var volumes = pod.Spec?.Volumes ?? [];
            var usesPvc = volumes.Any(v => v.PersistentVolumeClaim?.ClaimName == pvcName);
            if (usesPvc)
            {
                logger.LogDebug(
                    "PVC {PvcName} is in use by pod {PodName} (phase={Phase})",
                    pvcName, pod.Metadata.Name, pod.Status?.Phase);
                return true;
            }
        }

        return false;
    }
}

