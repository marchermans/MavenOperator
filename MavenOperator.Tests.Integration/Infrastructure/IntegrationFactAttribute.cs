namespace MavenOperator.Tests.Integration.Infrastructure;

/// <summary>
/// Marks a test as an integration test.
/// These tests require a real Kubernetes cluster and are skipped if
/// the INTEGRATION_TESTS environment variable is not set to "true".
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    private static readonly bool IsEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("INTEGRATION_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public IntegrationFactAttribute()
    {
        if (!IsEnabled)
            Skip = "Set INTEGRATION_TESTS=true to run integration tests against a real cluster.";
    }
}

/// <summary>Integration Theory — same skip logic.</summary>
public sealed class IntegrationTheoryAttribute : TheoryAttribute
{
    private static readonly bool IsEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("INTEGRATION_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public IntegrationTheoryAttribute()
    {
        if (!IsEnabled)
            Skip = "Set INTEGRATION_TESTS=true to run integration tests against a real cluster.";
    }
}

