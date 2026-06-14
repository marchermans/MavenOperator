using MavenOperator.Entities.Spec;
namespace MavenOperator.Services;
/// <summary>
/// Generates Kubernetes Gateway API resources (HTTPRoute) from GatewaySpec.
/// TLS is handled via cert-manager annotations on the HTTPRoute rather than
/// creating separate Certificate objects, which avoids conflicts when multiple
/// repositories share the same domain.
/// </summary>
public interface IGatewayApiService
{
    /// <summary>
    /// Builds an HTTPRoute resource structure as a dictionary suitable for CustomObjects.
    /// When <see cref="GatewaySpec.CertManager"/> is configured, the appropriate
    /// <c>cert-manager.io/cluster-issuer</c> or <c>cert-manager.io/issuer</c> annotation
    /// is added automatically — no separate Certificate object is created.
    /// </summary>
    Dictionary<string, object?> BuildHttpRoute(
        string name,
        string @namespace,
        string serviceName,
        int servicePort,
        GatewaySpec gatewaySpec,
        string repositoryName,
        string? defaultPathPrefix = null);
}
/// <inheritdoc/>
public sealed class GatewayApiService : IGatewayApiService
{
    private const string HttpRouteApiGroup = "gateway.networking.k8s.io";
    private const string HttpRouteApiVersion = "v1";
    private const string HttpRouteKind = "HTTPRoute";
    public Dictionary<string, object?> BuildHttpRoute(
        string name,
        string @namespace,
        string serviceName,
        int servicePort,
        GatewaySpec gatewaySpec,
        string repositoryName,
        string? defaultPathPrefix = null)
    {
        var path = gatewaySpec.Path ?? RepositoryPathHelper.ResolvePathPrefix(defaultPathPrefix, repositoryName);
        var gatewayNamespace = gatewaySpec.GatewayRef.Namespace ?? @namespace;
        var hostnames = new List<string>();
        if (!string.IsNullOrWhiteSpace(gatewaySpec.Hostname))
        {
            hostnames.Add(gatewaySpec.Hostname);
        }
        var labels = new Dictionary<string, string>
        {
            ["maven.operator.io/managed-by"] = repositoryName,
        };
        foreach (var kv in gatewaySpec.RouteLabels)
        {
            labels[kv.Key] = kv.Value;
        }
        var metadata = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["namespace"] = @namespace,
            ["labels"] = labels,
        };
        // Build annotations: cert-manager annotation is added first so user-provided
        // RouteAnnotations can override it if needed.
        var annotations = new Dictionary<string, string>();
        if (gatewaySpec.CertManager is not null)
        {
            var certAnnotationKey = gatewaySpec.CertManager.IsClusterIssuer
                ? "cert-manager.io/cluster-issuer"
                : "cert-manager.io/issuer";
            annotations[certAnnotationKey] = gatewaySpec.CertManager.IssuerName;
        }
        foreach (var kv in gatewaySpec.RouteAnnotations)
        {
            annotations[kv.Key] = kv.Value;
        }
        if (annotations.Count > 0)
        {
            metadata["annotations"] = annotations;
        }
        var parentRefs = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["name"] = gatewaySpec.GatewayRef.Name,
                ["namespace"] = gatewayNamespace,
            }
        };
        if (!string.IsNullOrWhiteSpace(gatewaySpec.GatewayRef.SectionName))
        {
            parentRefs[0]["sectionName"] = gatewaySpec.GatewayRef.SectionName;
        }
        var rules = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["matches"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = new Dictionary<string, string>
                        {
                            ["type"] = "PathPrefix",
                            ["value"] = path,
                        },
                    },
                },
                ["backendRefs"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = serviceName,
                        ["port"] = servicePort,
                        ["weight"] = 100,
                    },
                },
            },
        };
        if (gatewaySpec.ExtensionRefs.Count > 0)
        {
            rules[0]["filters"] = gatewaySpec.ExtensionRefs
                .Where(x => !string.IsNullOrWhiteSpace(x.Group) &&
                            !string.IsNullOrWhiteSpace(x.Kind) &&
                            !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => new Dictionary<string, object?>
                {
                    ["type"] = "ExtensionRef",
                    ["extensionRef"] = new Dictionary<string, string>
                    {
                        ["group"] = x.Group,
                        ["kind"] = x.Kind,
                        ["name"] = x.Name,
                    },
                })
                .Cast<object?>()
                .ToList();
        }
        var spec = new Dictionary<string, object?>
        {
            ["parentRefs"] = parentRefs,
            ["rules"] = rules,
        };
        // Add hostnames if specified
        if (hostnames.Count > 0)
        {
            spec["hostnames"] = hostnames;
        }
        return new Dictionary<string, object?>
        {
            ["apiVersion"] = $"{HttpRouteApiGroup}/{HttpRouteApiVersion}",
            ["kind"] = HttpRouteKind,
            ["metadata"] = metadata,
            ["spec"] = spec,
        };
    }
}
