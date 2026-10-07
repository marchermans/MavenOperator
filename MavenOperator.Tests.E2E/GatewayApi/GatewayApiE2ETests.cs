using System.Diagnostics;
using System.Net.Http;
using k8s;
using k8s.Autorest;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging.Abstractions;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Reconcilers;
using MavenOperator.Tests.E2E.Infrastructure;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace MavenOperator.Tests.E2E.GatewayApi;

/// <summary>
/// Gateway API end-to-end tests (Phase 8 §15). These require a working Gateway
/// implementation in the cluster — run via `./scripts/run-tests.sh e2e`, which
/// installs Envoy Gateway and exports GATEWAY_E2E_TESTS=true. The suite creates:
///   1. A Gateway object (gatewayClassName=envoy-gateway) with an HTTP listener
///      for host gw-e2e.test,
///   2. A hosted repository with spec.gateway enabled pointing at it,
/// and then exercises the full path through Envoy. Envoy Gateway deploys a
/// per-Gateway data plane (deployment + Service, found via the owning-gateway
/// label) in its own namespace; we port-forward that service's HTTP listener
/// (port 80): upload a jar and download it back.
/// </summary>
public class GatewayApiE2ETests : IAsyncLifetime
{
    private const string Hostname = "gw-e2e.test";

    private readonly ITestOutputHelper _output;
    private IKubernetesClient _client = null!;
    private k8s.IKubernetes _raw = null!;
    private string _namespace = "";
    private Process? _portForward;
    private HttpClient _http = null!;
    private string _baseUrl = "";

    private readonly string _suffix;
    private readonly string _repo;
    private readonly string _path;

    public GatewayApiE2ETests(ITestOutputHelper output)
    {
        _output = output;
        _suffix = Guid.NewGuid().ToString("N")[..8];
        _repo   = $"gw-e2e-{_suffix}";
        _path   = "com/example/gatewe2e/1.0/gatewe2e-1.0.jar";
    }

    public async Task InitializeAsync()
    {
        var config = k8s.KubernetesClientConfiguration.BuildDefaultConfig();
        _raw = new k8s.Kubernetes(config);
        _client = new KubernetesClient(config);

        _namespace = $"maven-e2e-gw-{_suffix}";
        try
        {
            await _client.CreateAsync(new V1Namespace
            {
                ApiVersion = "v1",
                Kind       = "Namespace",
                Metadata   = new V1ObjectMeta { Name = _namespace },
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { /* already exists */ }
        _output.WriteLine($"[GatewayApiE2ETests] namespace={_namespace}");

        await CleanupStaleNamespacesAsync().ConfigureAwait(false);

        // 1. The Gateway data plane: the Envoy Gateway chart does not create a
        //    Gateway object itself — our route's parentRef must point at one.
        await CreateGatewayAsync().ConfigureAwait(false);

        // 2. Hosted repository exposed through it (anonymous auth so the test
        //    client needs no credentials).
        var entity = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta
            {
                Name              = _repo,
                NamespaceProperty = _namespace
            },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec
                {
                    Size         = "2Gi",
                    AccessMode   = "ReadWriteOnce",
                    DeletionPolicy = DeletionPolicy.Delete,
                },
                Metrics = new MetricsSpec { Enabled = false },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
                Gateway = new GatewaySpec
                {
                    Enabled    = true,
                    Hostname   = Hostname,
                    Path       = $"/repository/{_repo}",
                    GatewayRef = new GatewayRefSpec { Name = "gateway-e2e" },
                },
            },
        };

        await _client.CreateAsync(entity, CancellationToken.None).ConfigureAwait(false);

        // Wait for the operator to bring the repository Ready and attach the route.
        MavenRepositoryV1Alpha1? ready = null;
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            ready = await _client.GetAsync<MavenRepositoryV1Alpha1>(_repo, _namespace, CancellationToken.None).ConfigureAwait(false);
            if (ready?.Status?.Phase == MavenOperator.Entities.Status.RepositoryPhase.Ready) break;
            await Task.Delay(3000, CancellationToken.None).ConfigureAwait(false);
        }

        ready.ShouldNotBeNull();
        ready!.Status.Phase.ShouldBe(MavenOperator.Entities.Status.RepositoryPhase.Ready,
            $"repository did not become Ready (last status: {ready?.Status?.Phase}, url={ready?.Status?.Url})");
        _output.WriteLine($"[GatewayApiE2ETests] repository Ready; status.url={ready!.Status.Url}");

        // Envoy Gateway deploys one data-plane deployment + Service per Gateway
        // (in the EG namespace); locate it by owning-gateway label and forward
        // its HTTP listener (port 80).
        var envoySvc = await WaitForGatewayServiceAsync().ConfigureAwait(false);
        _output.WriteLine($"[GatewayApiE2ETests] data plane service: envoy-gateway/{envoySvc}");

        var localPort = GetFreeTcpPort();
        _portForward = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "kubectl",
                Arguments = $"-n envoy-gateway port-forward svc/{envoySvc} {localPort}:80",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        _portForward.Start();
        var errTask = _portForward.StandardError.ReadToEndAsync();

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _baseUrl = $"http://127.0.0.1:{localPort}";
        await WaitForPortForwardAsync(_baseUrl, Hostname).ConfigureAwait(false);
        _ = errTask;
    }

    /// <summary>
    /// Self-heal: xUnit v2 does not call DisposeAsync when InitializeAsync throws, so
    /// failed runs leave maven-e2e-gw-* namespaces behind. Their orphaned per-Gateway
    /// Envoy data planes carry the same owning-gateway-name label and would otherwise
    /// be picked up by <see cref="WaitForGatewayServiceAsync"/> (404 for everything).
    /// </summary>
    private async Task CleanupStaleNamespacesAsync()
    {
        V1NamespaceList? stale;
        try
        {
            stale = await _raw.CoreV1.ListNamespaceAsync(cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[GatewayApiE2ETests] could not list namespaces for cleanup: {ex.Message}");
            return;
        }

        foreach (var ns in stale.Items.Where(n => n.Metadata?.Name is string nm
                                                && nm.StartsWith("maven-e2e-gw-") && nm != _namespace))
        {
            var name = ns.Metadata!.Name!;
            try
            {
                await _raw.CoreV1.DeleteNamespaceAsync(name, cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                _output.WriteLine($"[GatewayApiE2ETests] deleted stale namespace {name}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[GatewayApiE2ETests] failed to delete stale namespace {name}: {ex.Message}");
            }
        }
    }

    private async Task<string> WaitForGatewayServiceAsync()
    {
        // Both labels are required: name alone would match data planes of other
        // (leaked) namespaces that host a Gateway with the same 'gateway-e2e' name.
        var selector = "gateway.envoyproxy.io/owning-gateway-name=gateway-e2e" +
                       $",gateway.envoyproxy.io/owning-gateway-namespace={_namespace}";
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var list = await _raw.CoreV1.ListNamespacedServiceAsync(
                    "envoy-gateway", labelSelector: selector, cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                if (list.Items.Count > 0) return list.Items[0].Metadata!.Name!;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[GatewayApiE2ETests] data-plane service not ready yet ({ex.Message})");
            }

            await Task.Delay(2000, CancellationToken.None).ConfigureAwait(false);
        }

        throw new TimeoutException("Envoy Gateway per-Gateway data plane Service did not appear within 3 min");
    }

    private async Task CreateGatewayAsync()
    {
        var gatewayBody = new Dictionary<string, object?>
        {
            ["apiVersion"] = "gateway.networking.k8s.io/v1",
            ["kind"] = "Gateway",
            ["metadata"] = new Dictionary<string, object> { ["name"] = "gateway-e2e", ["namespace"] = _namespace },
            ["spec"] = new Dictionary<string, object>
            {
                ["gatewayClassName"] = "envoy-gateway",
                ["listeners"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = "http",
                        ["hostname"] = Hostname,
                        ["port"] = 80,
                        ["protocol"] = "HTTP"
                    }
                }
            }
        };

        try
        {
            // Extension method (k8s namespace): body comes first in v19.
            await _raw.CustomObjects.CreateNamespacedCustomObjectAsync(
                gatewayBody, "gateway.networking.k8s.io", "v1", _namespace, "gateways")
                .ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 409)
        {
            // Already created by a previous run in this namespace.
        }
    }

    private static async Task WaitForPortForwardAsync(string baseUrl, string host)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 30; i++)
        {
            try
            {
                // Envoy only routes requests whose Host matches an HTTPRoute; send the
                // test hostname so the probe gets a real response instead of an empty reply.
                using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl);
                request.Headers.Host = host;
                // Any response — even 404/503 — proves the listener is reachable.
                using var _ = await http.SendAsync(request).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (i < 29)
            {
                await Task.Delay(1000, CancellationToken.None);
            }
        }

        throw new TimeoutException("Envoy Gateway port-forward did not become reachable");
    }

    [GatewayE2EFact(DisplayName = "Upload then download through the Envoy Gateway data plane")]
    public async Task UploadAndDownloadThroughGateway()
    {
        var jarBytes = Convert.FromBase64String(
            "UEsDBBQAAAAIAA=="); // minimal zip/jar header is fine for this test

        // The route only covers the repository prefix — requests outside it get an
        // Envoy 404, so the artifact path must be rooted at /repository/{repo}.
        var repoPrefix = $"{_baseUrl.TrimEnd('/')}/repository/{_repo}";
        var uploadUrl = $"{repoPrefix}/{_path}";

        // The repository flips to Ready as soon as the HTTPRoute object exists, but
        // Envoy needs a moment to program it into the data plane. Until then the
        // gateway answers 404/503 — retry until the route is live.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        int putStatus;
        while (true)
        {
            var uploadReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(jarBytes) };
            uploadReq.Headers.Host = Hostname;
            using (var resp = await _http.SendAsync(uploadReq).ConfigureAwait(false))
            {
                putStatus = (int)resp.StatusCode;
            }

            if (putStatus is not (404 or 503) || DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(1_000, CancellationToken.None).ConfigureAwait(false);
        }

        _output.WriteLine($"[GatewayApiE2ETests] PUT through gateway -> {putStatus}");
        putStatus.ShouldBe(201);

        var downloadUrl = $"{repoPrefix}/{_path}";
        using var dlReq = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        dlReq.Headers.Host = Hostname;
        using var resp2 = await _http.SendAsync(dlReq).ConfigureAwait(false);
        ((int)resp2.StatusCode).ShouldBe(200);
        var body = resp2.Content.ReadAsByteArrayAsync().Result;
        body.ShouldBe(jarBytes);
    }

    [GatewayE2EFact(DisplayName = "Requests for non-matching hosts are not routed to the repository")]
    public async Task NonMatchingHostIsNotRouted()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/com/example/whatever/1.0/x.jar");
        req.Headers.Host = "some-other-host.example.com";
        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        ((int)resp.StatusCode).ShouldNotBe(200);
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_portForward is not null)
            {
                _portForward.Kill(entireProcessTree: true);
                await _portForward.WaitForExitAsync().ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(_namespace))
            {
                // Delete the Gateway first so no finalizer/route attachments linger,
                // then let namespace deletion sweep the rest.
                try
                {
                    await _raw.CustomObjects.DeleteNamespacedCustomObjectAsync(
                        "gateway.networking.k8s.io", "v1", _namespace, "gateways", "gateway-e2e",
                        cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (HttpOperationException)
                {
                    // not present — fine.
                }

                await _raw.CoreV1.DeleteNamespaceAsync(_namespace, cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _http?.Dispose();
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
