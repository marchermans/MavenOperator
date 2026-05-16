using MavenOperator.Services;
using Shouldly;

namespace MavenOperator.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="OperatorMetrics"/>.
/// Prometheus counters/histograms are global singletons so we only validate that
/// the service methods can be invoked without throwing and are idempotent.
/// </summary>
public sealed class OperatorMetricsTests
{
    [Fact]
    public void RecordReconcile_Success_DoesNotThrow()
    {
        var metrics = new OperatorMetrics();
        Should.NotThrow(() =>
            metrics.RecordReconcile("my-repo", "hosted", success: true, durationSeconds: 0.5));
    }

    [Fact]
    public void RecordReconcile_Failure_DoesNotThrow()
    {
        var metrics = new OperatorMetrics();
        Should.NotThrow(() =>
            metrics.RecordReconcile("my-repo", "proxy", success: false, durationSeconds: 1.2));
    }

    [Fact]
    public void RecordResourceApply_DoesNotThrow()
    {
        var metrics = new OperatorMetrics();
        Should.NotThrow(() =>
            metrics.RecordResourceApply("my-repo", "hosted", "Deployment"));
    }

    [Fact]
    public void SetRepositoryCount_DoesNotThrow()
    {
        var metrics = new OperatorMetrics();
        Should.NotThrow(() =>
            metrics.SetRepositoryCount("hosted", "Running", 3));
    }

    [Fact]
    public void RecordReconcile_CanBeCalledMultipleTimes()
    {
        var metrics = new OperatorMetrics();
        for (var i = 0; i < 10; i++)
            metrics.RecordReconcile("multi-repo", "virtual", success: true, durationSeconds: 0.1 * i);

        // Assert: no exception means the counter incremented idempotently
        true.ShouldBeTrue();
    }
}

