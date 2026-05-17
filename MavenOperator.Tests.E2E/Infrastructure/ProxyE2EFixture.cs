using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace MavenOperator.Tests.E2E.Infrastructure;

/// <summary>
/// xUnit fixture for Proxy repository E2E tests.
/// Provisions a Proxy MavenRepository CRD pointing at Maven Central,
/// waits for the operator to bring up the NGINX pod, then port-forwards
/// the ClusterIP Service so tests can reach it from the dev workstation.
/// </summary>
public sealed class ProxyE2EFixture : IAsyncLifetime
{
    private const string OperatorNamespace = "maven-e2e";
    private const string UpstreamUrl       = "https://repo1.maven.org/maven2";

    public IKubernetesClient Client        { get; private set; } = null!;
    public string   RepositoryName         { get; private set; } = string.Empty;
    public string   RepositoryUrl          { get; private set; } = string.Empty;
    public HttpClient HttpClient           { get; private set; } = null!;

    private Process? _portForwardProcess;

    public async Task InitializeAsync()
    {
        var config = KubernetesClientConfiguration.BuildDefaultConfig();
        Client = new KubernetesClient(config);

        RepositoryName = $"e2e-proxy-{Guid.NewGuid():N}"[..22];

        // Ensure the E2E namespace exists.
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

        // Create the Proxy repository CRD — Anonymous download, no upstream auth.
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
                Type     = RepositoryType.Proxy,
                Upstream = new UpstreamSpec
                {
                    Url      = UpstreamUrl,
                    CacheTtl = "1d",
                },
                // Disable metrics sidecars in E2E tests to avoid slow image pulls.
                Metrics = new MetricsSpec { Enabled = false },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous, SecretRefs = [] },
                    Upload   = new AuthPolicySpec { Policy = AuthPolicy.Anonymous, SecretRefs = [] },
                },
            },
        };

        await Client.CreateAsync<MavenRepositoryV1Alpha1>(repo, CancellationToken.None);

        await WaitForServiceAsync();

        var baseUrl = await ResolveBaseUrlAsync();
        RepositoryUrl = $"{baseUrl.TrimEnd('/')}/repository/{RepositoryName}";
        HttpClient    = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            // Proxy downloads from Maven Central can be slow on first fetch.
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    public async Task DisposeAsync()
    {
        HttpClient?.Dispose();
        if (_portForwardProcess is not null)
        {
            try { _portForwardProcess.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            _portForwardProcess.Dispose();
            _portForwardProcess = null;
        }
        try
        {
            await Client.DeleteAsync<MavenRepositoryV1Alpha1>(
                RepositoryName, OperatorNamespace, CancellationToken.None);
        }
        catch { /* best-effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    public async Task<(HttpStatusCode Status, byte[]? Body)> GetArtifactAsync(
        string path,
        string? username = null, string? password = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/repository/{RepositoryName}/{path}");
        if (username is not null)
        {
            var cred = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", cred);
        }
        var resp = await HttpClient.SendAsync(req, ct);
        var body = resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(ct) : null;
        return (resp.StatusCode, body);
    }

    public async Task<(int ExitCode, string Output)> RunMavenAsync(
        string fixtureDir,
        string goals,
        string? settingsXml  = null,
        CancellationToken ct = default)
    {
        var settings     = settingsXml ?? BuildMavenSettings();
        var settingsPath = Path.Combine(Path.GetTempPath(), $"mvn-settings-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(settingsPath, settings, ct);

        var mvnw = Path.Combine(fixtureDir, "mvnw");
        if (!File.Exists(mvnw)) mvnw = "mvn";

        if (File.Exists(mvnw))
            File.SetUnixFileMode(mvnw,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        return await RunProcessAsync(
            mvnw,
            $"{goals} -s {settingsPath} --batch-mode --no-transfer-progress",
            fixtureDir, ct);
    }

    private string BuildMavenSettings() => $"""
        <settings xmlns="http://maven.apache.org/SETTINGS/1.0.0"
                  xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                  xsi:schemaLocation="http://maven.apache.org/SETTINGS/1.0.0
                                      http://maven.apache.org/xsd/settings-1.0.0.xsd">
            <profiles>
                <profile>
                    <id>proxy-e2e</id>
                    <repositories>
                        <repository>
                            <id>maven-operator-proxy</id>
                            <url>{RepositoryUrl}</url>
                            <releases><enabled>true</enabled></releases>
                            <snapshots><enabled>false</enabled></snapshots>
                        </repository>
                    </repositories>
                </profile>
            </profiles>
            <activeProfiles><activeProfile>proxy-e2e</activeProfile></activeProfiles>
        </settings>
        """;

    private async Task WaitForServiceAsync()
    {
        await PodReadinessHelper.WaitForNginxReadyAsync(
            Client, OperatorNamespace, RepositoryName);
    }

    private async Task<string> ResolveBaseUrlAsync()
    {
        var explicitBase = Environment.GetEnvironmentVariable("E2E_REPO_BASE_URL");
        if (!string.IsNullOrEmpty(explicitBase))
            return explicitBase.TrimEnd('/');

        var localPort = GetFreePort();
        var svcName   = $"{RepositoryName}-svc";

        var psi = new ProcessStartInfo
        {
            FileName               = "kubectl",
            Arguments              = $"port-forward svc/{svcName} {localPort}:80 -n {OperatorNamespace}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        _portForwardProcess = new Process { StartInfo = psi };
        _portForwardProcess.Start();

        var pfDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < pfDeadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, localPort);
                break;
            }
            catch { await Task.Delay(500); }
        }

        return $"http://localhost:{localPort}";
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string executable, string arguments, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = executable,
            Arguments              = arguments,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, output.ToString());
    }
}

[CollectionDefinition(ProxyE2ECollection.CollectionName)]
public sealed class ProxyE2ECollection : ICollectionFixture<ProxyE2EFixture>
{
    public const string CollectionName = "ProxyE2E";
}

