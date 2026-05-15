using MavenOperator.Entities;
using MavenOperator.Entities.Status;

namespace MavenOperator.Reconcilers;

/// <summary>
/// Phase 1 implementation — stub that updates status and logs.
/// Full implementation (PVC, NGINX config, Deployment, Service) is Phase 1 work.
/// </summary>
public sealed class HostedRepositoryReconciler(ILogger<HostedRepositoryReconciler> logger)
    : IHostedRepositoryReconciler
{
    public Task ReconcileAsync(MavenRepositoryV1Alpha1 entity, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[Hosted] Reconciling {Namespace}/{Name}",
            entity.Metadata.NamespaceProperty, entity.Metadata.Name);

        // TODO Phase 1: EnsurePvcAsync, EnsureHtpasswdSecretsAsync,
        //               EnsureNginxConfigMapAsync, EnsureDeploymentAsync,
        //               EnsureServiceAsync, EnsureIngressAsync

        entity.Status.SetCondition(
            type: "Available",
            isTrue: true,
            reason: "Pending",
            message: "Hosted reconciler not yet fully implemented (Phase 1)");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Phase 2 implementation — stub.
/// </summary>
public sealed class ProxyRepositoryReconciler(ILogger<ProxyRepositoryReconciler> logger)
    : IProxyRepositoryReconciler
{
    public Task ReconcileAsync(MavenRepositoryV1Alpha1 entity, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[Proxy] Reconciling {Namespace}/{Name}",
            entity.Metadata.NamespaceProperty, entity.Metadata.Name);

        // TODO Phase 2: EnsureHtpasswdSecretsAsync, EnsureNginxConfigMapAsync,
        //               EnsureDeploymentAsync, EnsureServiceAsync, EnsureIngressAsync

        entity.Status.SetCondition(
            type: "Available",
            isTrue: true,
            reason: "Pending",
            message: "Proxy reconciler not yet fully implemented (Phase 2)");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Phase 3 implementation — stub.
/// </summary>
public sealed class VirtualRepositoryReconciler(ILogger<VirtualRepositoryReconciler> logger)
    : IVirtualRepositoryReconciler
{
    public Task ReconcileAsync(MavenRepositoryV1Alpha1 entity, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[Virtual] Reconciling {Namespace}/{Name}",
            entity.Metadata.NamespaceProperty, entity.Metadata.Name);

        // TODO Phase 3: EnsureHtpasswdSecretsAsync, EnsureProxyConfigMapAsync,
        //               EnsureProxyDeploymentAsync, EnsureNginxConfigMapAsync,
        //               EnsureNginxDeploymentAsync, EnsureServiceAsync, EnsureIngressAsync

        entity.Status.SetCondition(
            type: "Available",
            isTrue: true,
            reason: "Pending",
            message: "Virtual reconciler not yet fully implemented (Phase 3)");

        return Task.CompletedTask;
    }
}
