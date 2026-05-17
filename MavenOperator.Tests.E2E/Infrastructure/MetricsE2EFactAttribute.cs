namespace MavenOperator.Tests.E2E.Infrastructure;

/// <summary>
/// Marks a test as a metrics observability E2E test (Phase 6A).
///
/// These tests require:
///   1. A running operator in the cluster (same as [E2EFact]).
///   2. The <c>ghcr.io/google/mtail:latest</c> image to be pre-loaded into the k3d cluster.
///      Without the image the pod never becomes Ready and the fixture times out.
///
/// The test is skipped unless <b>both</b> of the following env vars are <c>true</c>:
///   <list type="bullet">
///     <item><c>E2E_TESTS=true</c> — general E2E gate.</item>
///     <item><c>METRICS_E2E_TESTS=true</c> — signals that the mtail image is available in the cluster.</item>
///   </list>
///
/// The <c>run-tests.sh</c> script sets <c>METRICS_E2E_TESTS=true</c> automatically when it
/// successfully pre-loads the mtail image into k3d (via <c>k3d image import</c>).
/// If the image cannot be pulled or imported, the variable is left unset and these tests are
/// skipped gracefully instead of timing out.
/// </summary>
public sealed class MetricsE2EFactAttribute : FactAttribute
{
    private static readonly bool IsE2EEnabled =
        string.Equals(Environment.GetEnvironmentVariable("E2E_TESTS"), "true",
                      StringComparison.OrdinalIgnoreCase);

    private static readonly bool IsMetricsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("METRICS_E2E_TESTS"), "true",
                      StringComparison.OrdinalIgnoreCase);

    public MetricsE2EFactAttribute()
    {
        if (!IsE2EEnabled)
        {
            Skip = "Set E2E_TESTS=true to run end-to-end tests. " +
                   "Requires a running operator and accessible repository service.";
        }
        else if (!IsMetricsEnabled)
        {
            Skip = "Set METRICS_E2E_TESTS=true to run metrics observability tests. " +
                   "Requires ghcr.io/google/mtail:latest to be pre-loaded in the k3d cluster. " +
                   "The run-tests.sh script sets this automatically when the image is available.";
        }
    }
}

