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
        await PublishCoreAsync(
            entityName:      entity.Metadata.Name!,
            entityNamespace: entity.Metadata.NamespaceProperty!,
            entityUid:       entity.Metadata.Uid,
            entityVersion:   entity.Metadata.ResourceVersion,
            kind:            "MavenRepository",
            reason:          reason,
            message:         message,
            type:            type,
            ct:              ct);
    }

    public async Task PublishAsync(
        MavenRepositoryImportV1Alpha1 entity,
        string reason,
        string message,
        string type = "Normal",
        CancellationToken ct = default)
    {
        await PublishCoreAsync(
            entityName:      entity.Metadata.Name!,
            entityNamespace: entity.Metadata.NamespaceProperty!,
            entityUid:       entity.Metadata.Uid,
            entityVersion:   entity.Metadata.ResourceVersion,
            kind:            "MavenRepositoryImport",
            reason:          reason,
            message:         message,
            type:            type,
            ct:              ct);
    }

    private async Task PublishCoreAsync(
        string entityName,
        string entityNamespace,
        string? entityUid,
        string? entityVersion,
        string kind,
        string reason,
        string message,
        string type,
        CancellationToken ct)
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
                    Name              = $"{entityName}.{now.Ticks:x16}",
                    NamespaceProperty = entityNamespace,
                },
                InvolvedObject = new V1ObjectReference
                {
                    ApiVersion        = "maven.operator.io/v1alpha1",
                    Kind              = kind,
                    Name              = entityName,
                    NamespaceProperty = entityNamespace,
                    Uid               = entityUid,
                    ResourceVersion   = entityVersion,
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
            logger.LogDebug("Published {Type} event '{Reason}' for {Namespace}/{Name} ({Kind})",
                type, reason, entityNamespace, entityName, kind);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to publish {Type} event '{Reason}' for {Namespace}/{Name}",
                type, reason, entityNamespace, entityName);
        }
    }
}

