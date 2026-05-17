using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;

namespace MavenOperator.Tests.E2E.Infrastructure;

/// <summary>
/// xUnit fixture that provisions a MavenRepository with <c>spec.metrics.enabled: true</c>
/// and exposes port-forwarded URLs to the main HTTP port (80), the
/// nginx-prometheus-exporter port (9113), and the mtail port (3903).
///
/// Used by the Phase 6A observability E2E tests.
/// </summary>
public sealed class MetricsE2EFixture : IAsyncLifetime
{
    private const string OperatorNamespace = "maven-e2e";

    public IKubernetesClient Client           { get; private set; } = null!;
    public string   RepositoryName            { get; private set; } = string.Empty;

    /// <summary>Base URL for the NGINX repository HTTP port (e.g. http://localhost:PORT).</summary>
    public string   HttpBaseUrl               { get; private set; } = string.Empty;

    /// <summary>Base URL for the nginx-prometheus-exporter /metrics endpoint.</summary>
    public string   ExporterMetricsUrl        { get; private set; } = string.Empty;

    /// <summary>Base URL for the mtail /metrics endpoint.</summary>
    public string   MtailMetricsUrl           { get; private set; } = string.Empty;

    public HttpClient HttpClient              { get; private set; } = null!;

    private Process? _pfHttp;
    private Process? _pfExporter;
    private Process? _pfMtail;
    private int      _httpPort;
    private int      _exporterPort;
    private int      _mtailPort;

    public async Task InitializeAsync()
    {
        var config = KubernetesClientConfiguration.BuildDefaultConfig();
        Client         = new KubernetesClient(config);
        RepositoryName = $"e2e-metrics-{Guid.NewGuid():N}"[..22];

        // Ensure namespace exists
        try
        {
            await Client.CreateAsync<V1Namespace>(
                new V1Namespace
                {
                    ApiVersion = "v1",
                    Kind       = "Namespace",
                    Metadata   = new V1ObjectMeta { Name = OperatorNamespace },
                },
                CancellationToken.None);
        }
        catch { /* already exists */ }

        // Create MavenRepository with metrics enabled
        var repo = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta
            {
                Name              = RepositoryName,
                NamespaceProperty = OperatorNamespace,
            },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec { Size = "1Gi", DeletionPolicy = DeletionPolicy.Delete },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                // ── Enable deep observability sidecars ─────────────────────────
                Metrics = new MetricsSpec
                {
                    Enabled      = true,
                    ExporterPort = 9113,
                    MtailPort    = 3903,
                },
            },
        };
        await Client.CreateAsync<MavenRepositoryV1Alpha1>(repo, CancellationToken.None);

        // Wait for the Service and pod (all three containers must be Ready)
        await PodReadinessHelper.WaitForNginxReadyAsync(Client, OperatorNamespace, RepositoryName);

        // Port-forward http
        _httpPort    = GetFreePort();
        _pfHttp      = StartPortForward($"svc/{RepositoryName}-svc", _httpPort, 80);
        await WaitForPortAsync(_httpPort);
        HttpBaseUrl  = $"http://localhost:{_httpPort}";
        HttpClient   = new HttpClient { BaseAddress = new Uri(HttpBaseUrl) };

        // Port-forward nginx-exporter
        _exporterPort      = GetFreePort();
        _pfExporter        = StartPortForward($"svc/{RepositoryName}-svc", _exporterPort, 9113);
        await WaitForPortAsync(_exporterPort);
        ExporterMetricsUrl = $"http://localhost:{_exporterPort}";

        // Port-forward mtail
        _mtailPort      = GetFreePort();
        _pfMtail        = StartPortForward($"svc/{RepositoryName}-svc", _mtailPort, 3903);
        await WaitForPortAsync(_mtailPort);
        MtailMetricsUrl = $"http://localhost:{_mtailPort}";
    }

    public async Task DisposeAsync()
    {
        HttpClient?.Dispose();

        foreach (var pf in new[] { _pfHttp, _pfExporter, _pfMtail })
        {
            if (pf is null) continue;
            try { pf.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            pf.Dispose();
        }

        try
        {
            await Client.DeleteAsync<MavenRepositoryV1Alpha1>(
                RepositoryName, OperatorNamespace, CancellationToken.None);
        }
        catch { /* best-effort */ }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Process StartPortForward(string resource, int localPort, int remotePort)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "kubectl",
            Arguments              = $"port-forward {resource} {localPort}:{remotePort} -n {OperatorNamespace}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        var p = new Process { StartInfo = psi };
        p.Start();
        return p;
    }

    private static async Task WaitForPortAsync(int port, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
        throw new TimeoutException($"Port {port} did not become reachable within {timeoutSeconds}s");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>xUnit collection for Phase 6A metrics E2E tests.</summary>
[CollectionDefinition(MetricsE2ECollection.CollectionName)]
public sealed class MetricsE2ECollection : ICollectionFixture<MetricsE2EFixture>
{
    public const string CollectionName = "MetricsE2E";
}


