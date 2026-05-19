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
/// xUnit fixture for E2E tests.
/// Provisions a MavenRepository CRD, waits for the operator to bring it Ready,
/// and exposes the repository's HTTP URL for Maven/Gradle clients.
///
/// When E2E_REPO_BASE_URL is not set the fixture automatically starts a
/// <c>kubectl port-forward</c> tunnel to the ClusterIP Service so the tests
/// can reach the in-cluster NGINX from the developer workstation.
/// </summary>
public sealed class E2EFixture : IAsyncLifetime
{
    private const string OperatorNamespace = "maven-e2e";

    public IKubernetesClient Client       { get; private set; } = null!;
    public string   RepositoryName        { get; private set; } = string.Empty;
    public string   RepositoryUrl         { get; private set; } = string.Empty;
    public string   UploadUser            { get; } = "deployer";
    public string   UploadPassword        { get; } = "e2eDeployS3cr3t!";
    public string   DownloadUser          { get; } = "reader";
    public string   DownloadPassword      { get; } = "e2eReadS3cr3t!";
    public HttpClient HttpClient          { get; private set; } = null!;

    // Port-forward process kept alive for the lifetime of the fixture.
    private Process? _portForwardProcess;
    private int      _localPort;

    public async Task InitializeAsync()
    {
        var config = KubernetesClientConfiguration.BuildDefaultConfig();
        Client = new KubernetesClient(config);

        RepositoryName = $"e2e-hosted-{Guid.NewGuid():N}"[..22];

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

        await CreateSecretAsync($"{RepositoryName}-upload-cred",   UploadUser,   UploadPassword);
        await CreateSecretAsync($"{RepositoryName}-download-cred", DownloadUser, DownloadPassword);
        // The upload user also needs download access so Maven deploy can read maven-metadata.xml
        // before writing (it does a GET first). We store this as an extra download credential.
        await CreateSecretAsync($"{RepositoryName}-upload-dl-cred", UploadUser, UploadPassword);

        var repo = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata = new V1ObjectMeta
            {
                Name              = RepositoryName,
                NamespaceProperty = OperatorNamespace,
            },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Hosted,
                Storage = new StorageSpec
                {
                    Size = "2Gi",
                    AccessMode = "ReadWriteOnce",
                    DeletionPolicy = DeletionPolicy.Delete
                },
                // Disable metrics sidecars in E2E tests — the nginx-exporter and mtail
                // images require internet pulls on cold runners which add 60-90 s of
                // latency and are the primary cause of pod-readiness timeouts.
                Metrics = new MetricsSpec { Enabled = false },
                Auth    = new AuthSpec
                {
                    Download = new AuthPolicySpec
                    {
                        Policy     = AuthPolicy.Authenticated,
                        // Both reader and deployer can download (deployer needs it for maven-metadata)
                        Users =
                        [
                            new UserRef { SecretRef = $"{RepositoryName}-download-cred", Role = UserRole.Reader },
                            new UserRef { SecretRef = $"{RepositoryName}-upload-dl-cred", Role = UserRole.Deployer },
                        ],
                    },
                    Upload = new AuthPolicySpec
                    {
                        Policy     = AuthPolicy.Authenticated,
                        Users =
                        [
                            new UserRef { SecretRef = $"{RepositoryName}-upload-cred", Role = UserRole.Deployer },
                        ],
                    },
                },
            },
        };

        await Client.CreateAsync<MavenRepositoryV1Alpha1>(repo, CancellationToken.None);

        // Block until the operator has created the ClusterIP Service.
        await WaitForServiceAsync();

        // Resolve the base URL — port-forward if needed.
        var baseUrl = await ResolveBaseUrlAsync();
        RepositoryUrl = $"{baseUrl.TrimEnd('/')}/repository/{RepositoryName}";
        HttpClient    = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        HttpClient?.Dispose();

        // Stop the port-forward tunnel first.
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

    // ── HTTP helpers ──────────────────────────────────────────────────────

    public async Task<System.Net.HttpStatusCode> PutArtifactAsync(
        string path, byte[] content,
        string username, string password,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"/repository/{RepositoryName}/{path}");
        req.Content = new ByteArrayContent(content);
        var cred = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", cred);
        var resp = await HttpClient.SendAsync(req, ct);
        return resp.StatusCode;
    }

    public async Task<(System.Net.HttpStatusCode Status, byte[]? Body)> GetArtifactAsync(
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

    // ── Build-tool runners ────────────────────────────────────────────────

    /// <summary>
    /// Runs Maven in the given fixture directory.
    /// Injects repo URL, credentials and a mirror that forces all resolution
    /// through the operator-managed repository (no Maven Central fallback).
    /// </summary>
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
            $"{goals} -s {settingsPath} -Drepo.url={RepositoryUrl} --batch-mode --no-transfer-progress",
            fixtureDir, ct);
    }

    /// <summary>
    /// Runs Gradle in the given fixture directory.
    /// Injects repo URL and credentials as project properties (-P flags).
    /// </summary>
    public async Task<(int ExitCode, string Output)> RunGradleAsync(
        string fixtureDir,
        string tasks,
        CancellationToken ct = default)
    {
        var gradlew = Path.Combine(fixtureDir, "gradlew");
        if (!File.Exists(gradlew)) gradlew = "gradle";

        if (File.Exists(gradlew))
            File.SetUnixFileMode(gradlew,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        return await RunProcessAsync(
            gradlew,
            $"{tasks} -PrepoUrl={RepositoryUrl} -PrepoUser={UploadUser} -PrepoPass={UploadPassword}" +
            $" -PrepoDownloadUser={DownloadUser} -PrepoDownloadPass={DownloadPassword} --no-daemon",
            fixtureDir, ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task CreateSecretAsync(string name, string username, string password)
    {
        var secret = new V1Secret
        {
            ApiVersion = "v1",
            Kind       = "Secret",
            Metadata = new V1ObjectMeta
            {
                Name              = name,
                NamespaceProperty = OperatorNamespace,
                Labels = new Dictionary<string, string>
                    { ["maven.operator.io/credential"] = "true" },
            },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>
            {
                ["username"] = Encoding.UTF8.GetBytes(username),
                ["password"] = Encoding.UTF8.GetBytes(password),
            },
        };
        await Client.CreateAsync<V1Secret>(secret, CancellationToken.None);
    }

    private async Task WaitForServiceAsync()
    {
        await PodReadinessHelper.WaitForNginxReadyAsync(
            Client, OperatorNamespace, RepositoryName);
    }

    /// <summary>
    /// Returns the HTTP origin (scheme + host + port) for the repository service.
    /// When E2E_REPO_BASE_URL is set, uses it directly (it should be just the origin,
    /// e.g. http://ingress.local or http://localhost:8080).
    /// Otherwise starts a <c>kubectl port-forward</c> to the ClusterIP Service
    /// on an ephemeral local port and returns <c>http://localhost:{port}</c>.
    /// </summary>
    private async Task<string> ResolveBaseUrlAsync()
    {
        var explicitBase = Environment.GetEnvironmentVariable("E2E_REPO_BASE_URL");
        if (!string.IsNullOrEmpty(explicitBase))
            return explicitBase.TrimEnd('/');

        // Pick an available local port.
        _localPort = GetFreePort();

        var svcName = $"{RepositoryName}-svc";
        var psi = new ProcessStartInfo
        {
            FileName               = "kubectl",
            Arguments              = $"port-forward svc/{svcName} {_localPort}:80 -n {OperatorNamespace}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        _portForwardProcess = new Process { StartInfo = psi };
        _portForwardProcess.Start();

        // Wait until the port is actually listening (up to 30 s).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, _localPort);
                break; // connected — tunnel is up
            }
            catch
            {
                await Task.Delay(500);
            }
        }

        return $"http://localhost:{_localPort}";
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string BuildMavenSettings() => $"""
        <settings xmlns="http://maven.apache.org/SETTINGS/1.0.0"
                  xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                  xsi:schemaLocation="http://maven.apache.org/SETTINGS/1.0.0
                                      http://maven.apache.org/xsd/settings-1.0.0.xsd">
            <servers>
                <server>
                    <id>maven-operator-hosted</id>
                    <username>{UploadUser}</username>
                    <password>{UploadPassword}</password>
                </server>
                <server>
                    <id>maven-operator-hosted-download</id>
                    <username>{DownloadUser}</username>
                    <password>{DownloadPassword}</password>
                </server>
            </servers>
        </settings>
        """;

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


[CollectionDefinition(E2ECollection.CollectionName)]
public sealed class E2ECollection : ICollectionFixture<E2EFixture>
{
    public const string CollectionName = "E2E";
}

