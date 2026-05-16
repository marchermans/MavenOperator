using MavenOperator.Entities;

namespace MavenOperator.Services;

/// <summary>
/// Publishes Kubernetes Events for a MavenRepository resource.
/// Events surface operator activity in `kubectl describe` and cluster event logs.
/// </summary>
public interface IKubernetesEventService
{
    /// <summary>
    /// Publishes a Kubernetes Event referencing the given MavenRepository.
    /// Failures are swallowed — event publishing is best-effort.
    /// </summary>
    /// <param name="entity">The MavenRepository that is the subject of the event.</param>
    /// <param name="reason">Machine-readable CamelCase reason, e.g. "ReconcileFailed".</param>
    /// <param name="message">Human-readable description of what happened.</param>
    /// <param name="type">"Normal" or "Warning". Defaults to "Normal".</param>
    /// <param name="ct">Cancellation token.</param>
    Task PublishAsync(
        MavenRepositoryV1Alpha1 entity,
        string reason,
        string message,
        string type = "Normal",
        CancellationToken ct = default);
}

