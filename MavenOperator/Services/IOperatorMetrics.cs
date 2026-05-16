using Prometheus;

namespace MavenOperator.Services;

/// <summary>
/// Operator-level Prometheus metrics.
/// All metrics are labelled so dashboards can slice by repo name/type.
/// </summary>
public interface IOperatorMetrics
{
    /// <summary>
    /// Records one reconcile loop execution.
    /// </summary>
    void RecordReconcile(string repoName, string repoType, bool success, double durationSeconds);

    /// <summary>
    /// Records a child-resource creation/update (e.g. "Deployment", "ConfigMap").
    /// </summary>
    void RecordResourceApply(string repoName, string repoType, string resourceKind);

    /// <summary>
    /// Sets the current count of MavenRepository CRDs by type & phase.
    /// </summary>
    void SetRepositoryCount(string repoType, string phase, int count);
}

/// <summary>
/// Prometheus-backed implementation of <see cref="IOperatorMetrics"/>.
/// All counters / histograms are registered once (singleton) on construction.
/// </summary>
public sealed class OperatorMetrics : IOperatorMetrics
{
    // reconcile_duration_seconds{repo,type,success}
    private readonly Histogram _reconcileDuration = Metrics.CreateHistogram(
        "mavenoperator_reconcile_duration_seconds",
        "Duration of a single reconcile loop execution.",
        new HistogramConfiguration
        {
            LabelNames = ["repo_name", "repo_type", "success"],
            Buckets = Histogram.ExponentialBuckets(0.01, 2, 10)
        });

    // reconcile_total{repo,type,success}
    private readonly Counter _reconcileTotal = Metrics.CreateCounter(
        "mavenoperator_reconcile_total",
        "Total number of reconcile loop executions.",
        new CounterConfiguration
        {
            LabelNames = ["repo_name", "repo_type", "success"]
        });

    // resource_apply_total{repo,type,resource_kind}
    private readonly Counter _resourceApplyTotal = Metrics.CreateCounter(
        "mavenoperator_resource_apply_total",
        "Total number of Kubernetes child-resource server-side applies.",
        new CounterConfiguration
        {
            LabelNames = ["repo_name", "repo_type", "resource_kind"]
        });

    // repository_count{type,phase}
    private readonly Gauge _repositoryCount = Metrics.CreateGauge(
        "mavenoperator_repository_count",
        "Current number of MavenRepository CRDs by type and phase.",
        new GaugeConfiguration
        {
            LabelNames = ["repo_type", "phase"]
        });

    /// <inheritdoc/>
    public void RecordReconcile(string repoName, string repoType, bool success, double durationSeconds)
    {
        var successStr = success ? "true" : "false";
        _reconcileTotal.WithLabels(repoName, repoType, successStr).Inc();
        _reconcileDuration.WithLabels(repoName, repoType, successStr).Observe(durationSeconds);
    }

    /// <inheritdoc/>
    public void RecordResourceApply(string repoName, string repoType, string resourceKind)
        => _resourceApplyTotal.WithLabels(repoName, repoType, resourceKind).Inc();

    /// <inheritdoc/>
    public void SetRepositoryCount(string repoType, string phase, int count)
        => _repositoryCount.WithLabels(repoType, phase).Set(count);
}

