namespace MavenOperator.Tests.E2E.Infrastructure;

/// <summary>
/// Marks a test as a Gateway API data-plane E2E test (Phase 8).
///
/// These tests require:
///   1. A running operator in the cluster (same as [E2EFact]).
///   2. An Envoy Gateway installation providing the Gateway API data plane
///      (<c>helm install gateway-helm oci://.../gateway-helm</c>, namespace <c>envoy-gateway</c>)
///      with a Gateway named <c>gateway-helm</c>.
///   3. The Gateway API CRDs (installed by the Envoy Gateway helm chart).
///
/// The test is skipped unless <b>both</b> of the following env vars are <c>true</c>:
///   <list type="bullet">
///     <item><c>E2E_TESTS=true</c> — general E2E gate.</item>
///     <item><c>GATEWAY_E2E_TESTS=true</c> — signals that Envoy Gateway is installed.
///       run-tests.sh sets this automatically when it installs the chart itself.</item>
///   </list>
/// </summary>
public sealed class GatewayE2EFactAttribute : FactAttribute
{
    private static readonly bool IsE2EEnabled =
        string.Equals(Environment.GetEnvironmentVariable("E2E_TESTS"), "true",
                      StringComparison.OrdinalIgnoreCase);

    private static readonly bool IsGatewayEnabled =
        string.Equals(Environment.GetEnvironmentVariable("GATEWAY_E2E_TESTS"), "true",
                      StringComparison.OrdinalIgnoreCase);

    public GatewayE2EFactAttribute()
    {
        if (!IsE2EEnabled)
        {
            Skip = "Set E2E_TESTS=true to run end-to-end tests. " +
                   "Requires a running operator and accessible repository service.";
        }
        else if (!IsGatewayEnabled)
        {
            Skip = "Set GATEWAY_E2E_TESTS=true when Envoy Gateway (gateway-helm chart, " +
                   "namespace envoy-gateway) is installed in the cluster. " +
                   "run-tests.sh sets this automatically for self-managed clusters.";
        }
    }
}
