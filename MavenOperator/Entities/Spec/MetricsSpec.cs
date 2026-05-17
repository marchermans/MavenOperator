namespace MavenOperator.Entities.Spec;

/// <summary>
/// Configuration for Prometheus metrics sidecars injected into NGINX pods.
/// </summary>
public sealed class MetricsSpec
{
    /// <summary>
    /// When true the operator injects the nginx-prometheus-exporter and mtail sidecars.
    /// Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum number of distinct artifact label combinations tracked by mtail (LRU).
    /// Default: 10 000. Setting this lower reduces Prometheus cardinality.
    /// </summary>
    public int MaxLabelCardinality { get; set; } = 10_000;

    /// <summary>
    /// Internal port on which NGINX exposes stub_status (only reachable from the pod).
    /// Default: 9080.
    /// </summary>
    public int StubStatusPort { get; set; } = 9080;

    /// <summary>
    /// Port on which the nginx-prometheus-exporter sidecar exposes /metrics.
    /// Default: 9113.
    /// </summary>
    public int ExporterPort { get; set; } = 9113;

    /// <summary>
    /// Port on which the mtail sidecar exposes /metrics.
    /// Default: 3903.
    /// </summary>
    public int MtailPort { get; set; } = 3903;

    /// <summary>
    /// Image for the nginx-prometheus-exporter sidecar.
    /// Default: nginx/nginx-prometheus-exporter:1.4
    /// </summary>
    public string NginxExporterImage { get; set; } = "nginx/nginx-prometheus-exporter:1.4";

    /// <summary>
    /// Image for the mtail sidecar.
    /// Default: ghcr.io/google/mtail:latest
    /// </summary>
    public string MtailImage { get; set; } = "ghcr.io/google/mtail:latest";

    /// <summary>
    /// When true the operator creates a PodMonitor resource (requires prometheus-operator).
    /// Default: false.
    /// </summary>
    public bool PodMonitorEnabled { get; set; } = false;
}

