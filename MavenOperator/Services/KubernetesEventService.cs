using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;

namespace MavenOperator.Services;

/// <inheritdoc/>
public sealed class KubernetesEventService(
    IKubernetesClient client,
    ILogger<KubernetesEventService> logger)
    : IKubernetesEventService
{
    private const string ReportingComponent = "maven-operator";

    public async Task PublishAsync(
        MavenRepositoryV1Alpha1 entity,
        string reason,
        string message,
        string type = "Normal",
        CancellationToken ct = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            var ev = new Corev1Event
            {
                ApiVersion = "v1",
                Kind       = "Event",
                Metadata   = new V1ObjectMeta
                {
                    // Name must be unique per event; combine entity name + timestamp ticks.
                    Name              = $"{entity.Metadata.Name!}.{now.Ticks:x16}",
                    NamespaceProperty = entity.Metadata.NamespaceProperty,
                },
                InvolvedObject = new V1ObjectReference
                {
                    ApiVersion        = "maven.operator.io/v1alpha1",
                    Kind              = "MavenRepository",
                    Name              = entity.Metadata.Name,
                    NamespaceProperty = entity.Metadata.NamespaceProperty,
                    Uid               = entity.Metadata.Uid,
                    ResourceVersion   = entity.Metadata.ResourceVersion,
                },
                Reason             = reason,
                Message            = message,
                Type               = type,
                EventTime          = now,
                FirstTimestamp     = now,
                LastTimestamp      = now,
                Count              = 1,
                Action             = reason,
                ReportingComponent = ReportingComponent,
                ReportingInstance  = Environment.MachineName,
                Source             = new V1EventSource { Component = ReportingComponent },
            };

            await client.CreateAsync(ev, ct);
            logger.LogDebug("Published {Type} event '{Reason}' for {Namespace}/{Name}",
                type, reason, entity.Metadata.NamespaceProperty, entity.Metadata.Name);
        }
        catch (Exception ex)
        {
            // Event publishing is best-effort — never let it fail reconciliation.
            logger.LogWarning(ex,
                "Failed to publish {Type} event '{Reason}' for {Namespace}/{Name}",
                type, reason, entity.Metadata.NamespaceProperty, entity.Metadata.Name);
        }
    }
}


