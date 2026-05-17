using MavenOperator.Entities;

namespace MavenOperator.Services;

/// <summary>
/// Publishes Kubernetes Events for operator-managed resources.
/// Events surface operator activity in `kubectl describe` and cluster event logs.
/// </summary>
public interface IKubernetesEventService
{
    /// <summary>Publishes a Kubernetes Event for a MavenRepository.</summary>
    Task PublishAsync(
        MavenRepositoryV1Alpha1 entity,
        string reason,
        string message,
        string type = "Normal",
        CancellationToken ct = default);

    /// <summary>Publishes a Kubernetes Event for a MavenRepositoryImport.</summary>
    Task PublishAsync(
        MavenRepositoryImportV1Alpha1 entity,
        string reason,
        string message,
        string type = "Normal",
        CancellationToken ct = default);
}

