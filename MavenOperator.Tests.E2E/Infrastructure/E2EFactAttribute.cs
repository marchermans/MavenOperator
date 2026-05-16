namespace MavenOperator.Tests.E2E.Infrastructure;

/// <summary>
/// Marks a test as an E2E test.
/// E2E tests are skipped unless E2E_TESTS=true is set.
/// They require a running operator in the cluster AND network access to the repository service.
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    private static readonly bool IsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("E2E_TESTS"), "true",
                      StringComparison.OrdinalIgnoreCase);

    public E2EFactAttribute()
    {
        if (!IsEnabled)
            Skip = "Set E2E_TESTS=true to run end-to-end tests. " +
                   "Requires a running operator and accessible repository service.";
    }
}

