using MavenOperator.Entities.Spec;

namespace MavenOperator.Services;

/// <summary>
/// Generates Kubernetes Gateway API resources (HTTPRoute, Certificate) from GatewaySpec.
/// </summary>
public interface IGatewayApiService
{
    /// <summary>
    /// Builds an HTTPRoute resource structure as a dictionary suitable for CustomObjects.
    /// </summary>
    Dictionary<string, object?> BuildHttpRoute(
        string name,
        string @namespace,
        string serviceName,
        int servicePort,
        GatewaySpec gatewaySpec,
        string repositoryName);

    /// <summary>
    /// Builds a CertManager Certificate resource structure as a dictionary.
    /// Returns null if CertManager is not configured or disabled.
    /// </summary>
    Dictionary<string, object?>? BuildCertificate(
        string name,
        string @namespace,
        string hostname,
        string? email,
        CertManagerSpec certManager);
}

/// <inheritdoc/>
public sealed class GatewayApiService : IGatewayApiService
{
    private const string HttpRouteApiGroup = "gateway.networking.k8s.io";
    private const string HttpRouteApiVersion = "v1";
    private const string HttpRouteKind = "HTTPRoute";

    private const string CertificateApiGroup = "cert-manager.io";
    private const string CertificateApiVersion = "v1";
    private const string CertificateKind = "Certificate";

    public Dictionary<string, object?> BuildHttpRoute(
        string name,
        string @namespace,
        string serviceName,
        int servicePort,
        GatewaySpec gatewaySpec,
        string repositoryName)
    {
        var path = gatewaySpec.Path ?? $"/repository/{repositoryName}";
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

        if (gatewaySpec.RouteAnnotations.Count > 0)
        {
            metadata["annotations"] = new Dictionary<string, string>(gatewaySpec.RouteAnnotations);
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

    public Dictionary<string, object?>? BuildCertificate(
        string name,
        string @namespace,
        string hostname,
        string? email,
        CertManagerSpec certManager)
    {
        if (certManager is null)
        {
            return null;
        }

        if (!certManager.AutoCreate)
        {
            return null;
        }

        var issuerRef = new Dictionary<string, string>
        {
            ["name"] = certManager.IssuerName,
            ["kind"] = certManager.IsClusterIssuer ? "ClusterIssuer" : "Issuer",
        };

        // Secret name should match the HTTPRoute's TLS secret reference
        var secretName = $"{name}-tls";

        var labels = new Dictionary<string, string>
        {
            ["maven.operator.io/managed-by"] = name,
        };

        var metadata = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["namespace"] = @namespace,
            ["labels"] = labels,
        };

        var spec = new Dictionary<string, object?>
        {
            ["secretName"] = secretName,
            ["issuerRef"] = issuerRef,
            ["commonName"] = hostname,
            ["dnsNames"] = new[] { hostname },
            ["renewBefore"] = certManager.RenewBefore,
        };

        if (!string.IsNullOrWhiteSpace(email))
        {
            spec["emailAddresses"] = new[] { email };
        }

        return new Dictionary<string, object?>
        {
            ["apiVersion"] = $"{CertificateApiGroup}/{CertificateApiVersion}",
            ["kind"] = CertificateKind,
            ["metadata"] = metadata,
            ["spec"] = spec,
        };
    }

}





