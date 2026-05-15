using MavenOperator.Entities;

namespace MavenOperator.Reconcilers;

/// <summary>
/// Reconciles Hosted repository infrastructure:
/// PVC → htpasswd Secrets → NGINX ConfigMap → Deployment → Service → (Ingress).
/// </summary>
public interface IHostedRepositoryReconciler
{
    Task ReconcileAsync(MavenRepositoryV1Alpha1 entity, CancellationToken cancellationToken);
}

/// <summary>
/// Reconciles Proxy repository infrastructure:
/// htpasswd Secrets → NGINX ConfigMap → Deployment → Service → (Ingress).
/// </summary>
public interface IProxyRepositoryReconciler
{
    Task ReconcileAsync(MavenRepositoryV1Alpha1 entity, CancellationToken cancellationToken);
}

/// <summary>
/// Reconciles Virtual repository infrastructure:
/// C# proxy ConfigMap → proxy Deployment → NGINX ConfigMap → NGINX Deployment → Service → (Ingress).
/// </summary>
public interface IVirtualRepositoryReconciler
{
    Task ReconcileAsync(MavenRepositoryV1Alpha1 entity, CancellationToken cancellationToken);
}

