using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using MavenOperator.Entities;
using MavenOperator.Entities.Spec;
using MavenOperator.Tests.E2E.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace MavenOperator.Tests.E2E.Proxy;

/// <summary>
/// E2E coverage for Phase U proxy upload forwarding (nginx limit_except gate +
/// per-method upstream credential map), validated against an in-cluster Reposilite
/// that acts as the remote upstream.
///
/// In-cluster topology:
///   client --PUT/GET--> {proxy}-svc (nginx) --> http://{rs}.maven-e2e.svc.cluster.local:8082/releases-pup
///
/// Provisioned repositories (one collection fixture):
///   pup-up-*    — upload enabled, Passthrough + Authenticated client gate, metrics ON  (tests a/b/g)
///   pup-ro-*    — upload disabled (read-only cache)                                     (test d)
///   pup-xexp-*  — externally-exposed annotation, no forceAllowOnExternal               (test e)
///   pup-force-* — externally-exposed annotation + forceAllowOnExternal: true           (test f)
///   pup-ov-*    — upload enabled in Override mode (no client auth gate)                (test c)
/// </summary>
[Collection(ProxyUploadE2ECollection.CollectionName)]
[Trait("Category", "E2E")]
public sealed class ProxyUploadE2ETests : IDisposable
{
    private readonly ProxyUploadE2EFixture _fx;

    public ProxyUploadE2ETests(ProxyUploadE2EFixture fx) => _fx = fx;

    // ── a: passthrough with dedicated creds → forwarded to upstream, artifact lands there.
    [E2EFact]
    public async Task Passthrough_ForwardedWithDedicatedCreds_ArtifactLandsInUpstream()
    {
        const string jarPath = "com/example/widget/1.0.0/widget-1.0.0.jar";
        const string pomPath = "com/example/widget/1.0.0/widget-1.0.0.pom";

        var jarBytes = System.Text.Encoding.UTF8.GetBytes($"PK-fake-widget-jar-{_fx.Suffix}");
        var pomBytes = System.Text.Encoding.UTF8.GetBytes(
            $"<?xml version=\"1.0\"?><project><groupId>com.example</groupId><artifactId>widget</artifactId><version>1.0.0</version></project>");

        // Operator must report upload forwarding as active with creds loaded.
        var ready = await _fx.WaitForConditionAsync(_fx.UploadRepoName, "UploadReady", isTrue: true);
        Assert.NotNull(ready);
        Assert.Equal("UploadForwardingEnabled", ready.Reason);

        var repo = await _fx.GetRepositoryAsync(_fx.UploadRepoName);
        Assert.True(repo.Status?.Upload?.Proxy?.Enabled == true, "status.upload.proxy.enabled should be true");
        Assert.True(repo.Status?.Upload?.Proxy?.UpstreamCredentialsConfigured == true,
            "status.upload.proxy.upstreamCredentialsConfigured should be true");

        // Upload through the proxy with valid client credentials (Passthrough gate).
        foreach (var (path, bytes) in new[] { (jarPath, jarBytes), (pomPath, pomBytes) })
        {
            var resp = await _fx.PutViaProxyAsync(_fx.UploadRepoName, path, bytes);
            Assert.True(resp.StatusCode is >= HttpStatusCode.OK and <= HttpStatusCode.MultiStatus,
                $"PUT /repository/{_fx.UploadRepoName}/{path} → {(int)resp.StatusCode}");
            resp.Dispose();
        }

        // Artifact must be retrievable directly from the upstream (Reposilite) — proof of forwarding.
        foreach (var (path, bytes) in new[] { (jarPath, jarBytes), (pomPath, pomBytes) })
        {
            var (status, body) = await _fx.DownloadFromUpstreamAsync(path);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(bytes, body ?? Array.Empty<byte>());
        }

        // Reads through the proxy must still work and serve the uploaded artifact.
        foreach (var (path, bytes) in new[] { (jarPath, jarBytes), (pomPath, pomBytes) })
        {
            var (status, body) = await _fx.GetViaProxyAsync(_fx.UploadRepoName, path);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(bytes, body ?? Array.Empty<byte>());
        }
    }

    // ── b: wrong client credentials against the Passthrough gate → 401.
    [E2EFact]
    public async Task Passthrough_WrongClientCreds_Returns401()
    {
        var body = System.Text.Encoding.UTF8.GetBytes($"PK-fake-wrong-{_fx.Suffix}");

        await _fx.WaitForConditionAsync(_fx.UploadRepoName, "UploadReady", isTrue: true);

        using var resp = await _fx.PutViaProxyAsync(
            _fx.UploadRepoName,
            "com/example/wrong/1.0.0/wrong-1.0.0.jar",
            body,
            username: "definitely-not-a-user",
            password: "nope");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── c: Override mode skips the client auth gate entirely (migration mode).
    [E2EFact]
    public async Task OverrideMode_PutWithoutClientAuth_SucceedsAndLandsInUpstream()
    {
        var ready = await _fx.WaitForConditionAsync(_fx.OverrideRepoName, "UploadReady", isTrue: true);
        Assert.NotNull(ready);

        const string jarPath = "com/example/override/1.0.0/override-1.0.0.jar";
        var jarBytes = System.Text.Encoding.UTF8.GetBytes($"PK-fake-override-{_fx.Suffix}");

        // No client credentials at all — the Override gate is absent, so PUT goes through.
        using (var resp = await _fx.PutViaProxyAsync(
                   _fx.OverrideRepoName, jarPath, jarBytes,
                   username: null, password: null))
        {
            Assert.True(resp.StatusCode is >= HttpStatusCode.OK and <= HttpStatusCode.MultiStatus,
                $"PUT /repository/{_fx.OverrideRepoName}/{jarPath} without client creds → {(int)resp.StatusCode}");
        }

        var (status, body) = await _fx.DownloadFromUpstreamAsync(jarPath);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(jarBytes, body ?? Array.Empty<byte>());
    }

    // ── d: upload disabled → proxy is a read-only cache; PUT rejected with 405.
    [E2EFact]
    public async Task ReadOnlyProxy_PutReturns405()
    {
        var body = System.Text.Encoding.UTF8.GetBytes($"PK-fake-ro-{_fx.Suffix}");

        // Give the reconciler a moment to render the read-only variant.
        await _fx.WaitForConditionAsync(_fx.ReadOnlyRepoName, "Available", isTrue: true);

        using var resp = await _fx.PutViaProxyAsync(
            _fx.ReadOnlyRepoName,
            "com/example/ro/1.0.0/ro-1.0.0.jar",
            body,
            username: ProxyUploadE2EFixture.ClientUser,
            password: ProxyUploadE2EFixture.ClientPassword);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    // ── e: externally-exposed proxy without forceAllowOnExternal → uploads blocked.
    [E2EFact]
    public async Task ExternallyExposed_BlocksUploads_UntilForced()
    {
        var forbidden = await _fx.WaitForConditionAsync(_fx.ExposedRepoName, "UploadForbidden", isTrue: true);
        Assert.NotNull(forbidden);
        Assert.Equal("ExternallyExposedProxy", forbidden.Reason);

        // Operator must have emitted a Warning event about the block.
        var warningEvent = await _fx.WaitForWarningEventAsync(
            _fx.ExposedRepoName, "UploadBlockedExternallyExposed");
        Assert.NotNull(warningEvent);

        using var resp = await _fx.PutViaProxyAsync(
            _fx.ExposedRepoName,
            "com/example/xexp/1.0.0/xexp-1.0.0.jar",
            System.Text.Encoding.UTF8.GetBytes($"PK-fake-xexp-{_fx.Suffix}"),
            username: ProxyUploadE2EFixture.ClientUser,
            password: ProxyUploadE2EFixture.ClientPassword);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    // ── f: same annotation + forceAllowOnExternal: true → uploads allowed again.
    [E2EFact]
    public async Task ForceAllowOnExternal_AllowsUploads()
    {
        var ready = await _fx.WaitForConditionAsync(_fx.ForcedRepoName, "UploadReady", isTrue: true);
        Assert.NotNull(ready);

        // UploadForbidden must never be present for this repo.
        var forced = await _fx.GetRepositoryAsync(_fx.ForcedRepoName);
        Assert.Null(forced.Status?.Conditions?.FirstOrDefault(c => c.Type == "UploadForbidden"));

        const string jarPath = "com/example/forced/1.0.0/forced-1.0.0.jar";
        var jarBytes = System.Text.Encoding.UTF8.GetBytes($"PK-fake-forced-{_fx.Suffix}");

        using (var resp = await _fx.PutViaProxyAsync(
                   _fx.ForcedRepoName, jarPath, jarBytes,
                   username: ProxyUploadE2EFixture.ClientUser,
                   password: ProxyUploadE2EFixture.ClientPassword))
        {
            Assert.True(resp.StatusCode is >= HttpStatusCode.OK and <= HttpStatusCode.MultiStatus,
                $"PUT /repository/{_fx.ForcedRepoName}/{jarPath} → {(int)resp.StatusCode}");
        }

        var (status, body) = await _fx.DownloadFromUpstreamAsync(jarPath);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(jarBytes, body ?? Array.Empty<byte>());
    }

    // ── g: successful uploads are visible in mtail metrics.
    [E2EFact]
    public async Task UploadProducesMtailMetrics()
    {
        const string jarPath = "com/example/metrics/1.0.0/metrics-1.0.0.jar";
        var jarBytes = System.Text.Encoding.UTF8.GetBytes($"PK-fake-metrics-{_fx.Suffix}");

        await _fx.WaitForConditionAsync(_fx.UploadRepoName, "UploadReady", isTrue: true);

        using (var resp = await _fx.PutViaProxyAsync(
                   _fx.UploadRepoName, jarPath, jarBytes,
                   username: ProxyUploadE2EFixture.ClientUser,
                   password: ProxyUploadE2EFixture.ClientPassword))
        {
            Assert.True(resp.StatusCode is >= HttpStatusCode.OK and <= HttpStatusCode.MultiStatus,
                $"PUT /repository/{_fx.UploadRepoName}/{jarPath} → {(int)resp.StatusCode}");
        }

        // mtail tails the JSON access log; poll until the counter appears.
        var value = await _fx.PollMtailUploadCounterAsync(_fx.UploadRepoName);
        Assert.True(value > 0, $"maven_upload_bytes_total for repo '{_fx.UploadRepoName}' should be > 0");
    }

    // ── h: CRD CEL validation rejects upload without upstream credentials.
    [E2EFact]
    public async Task CelValidation_RejectsUploadWithoutUpstreamCredentials()
    {
        var ex = await _fx.ExpectCelRejectionAsync($"pup-cel-{_fx.Suffix}");

        // The API server must reject the object with 422 and name the offending field.
        Assert.Contains("UnprocessableEntity", ex.Message);
        Assert.Contains("upstreamCredentialsRef", ex.Message);

        // And it must not exist in the cluster as a result.
        MavenRepositoryV1Alpha1? repo = null;
        try { repo = await _fx.GetRepositoryAsync($"pup-cel-{_fx.Suffix}"); } catch { /* 404 → absent */ }
        Assert.Null(repo);
    }

    public void Dispose() => _fx.DisposeLeases();
}

[CollectionDefinition(ProxyUploadE2ECollection.CollectionName)]
public sealed class ProxyUploadE2ECollection : ICollectionFixture<ProxyUploadE2EFixture>
{
    public const string CollectionName = "ProxyUploadE2E";
}

/// <summary>
/// Provisioning fixture: one in-cluster Reposilite as the remote upstream plus five
/// Proxy-type MavenRepository CRs covering every Phase U upload scenario.
/// Self-heals leaked resources from crashed prior runs at start of InitializeAsync
/// (xUnit v2 skips DisposeAsync when InitializeAsync throws).
/// </summary>
public sealed class ProxyUploadE2EFixture : IAsyncLifetime
{
    private const string OperatorNamespace = "maven-e2e";

    /// <summary>Reposilite hosted repository id — must match the last path segment of every proxy's spec.upstream.url.</summary>
    public const string RepoId       = "releases-pup";
    public const string ClientUser     = "pup-deployer";
    public const string ClientPassword = "pup-s3cr3t";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..6];
    public string Suffix         => _suffix;
    /// <summary>Upload-enabled proxy with metrics ON (tests a/b/f).</summary>
    public string UploadRepoName   => $"pup-up-{_suffix}";
    /// <summary>Read-only proxy — upload disabled (test c).</summary>
    public string ReadOnlyRepoName => $"pup-ro-{_suffix}";
    /// <summary>Externally-exposed annotation, no forceAllowOnExternal (test d).</summary>
    public string ExposedRepoName  => $"pup-xexp-{_suffix}";
    /// <summary>Externally-exposed annotation + forceAllowOnExternal: true (test f).</summary>
    public string ForcedRepoName   => $"pup-force-{_suffix}";
    /// <summary>Override-mode upload proxy — no client auth gate (test c).</summary>
    public string OverrideRepoName => $"pup-ov-{_suffix}";

    private const int RsPort = 8082;
    private IKubernetesClient _client = null!;
    private Kubernetes _raw = null!;
    private readonly List<Process> _pfProcesses = [];

    // Reposilite token created at boot via `-t <name>:<secret>` (full permissions).
    private const string RsUser     = "pup-uploader";
    private const string RsPassword = "rsil-pup-s3cret";

    private static string RsImage =>
        Environment.GetEnvironmentVariable("REPOSILITE_IMAGE")
            ?? "ghcr.io/dzikoysk/reposilite:3.2.4";

    public async Task InitializeAsync()
    {
        var config = KubernetesClientConfiguration.BuildDefaultConfig();
        _client = new KubernetesClient(config);
        _raw    = new Kubernetes(config);

        await CleanupStaleRunsAsync(); // self-heal leaked resources (xUnit skips Dispose on init failure)

        try
        {
            await EnsureNamespaceAsync(OperatorNamespace);

            var upstreamUrl  = $"http://{RsAppName}.{OperatorNamespace}.svc.cluster.local:{RsPort}/{RepoId}";

            // Secrets: read token for spec.upstream.auth, dedicated upload creds (same
            // Reposilite token values), client-side credentials for the nginx PUT gate.
            foreach (var repo in RepoNames)
            {
                await EnsureCredentialSecretAsync($"{repo}-upstream", RsUser, RsPassword);
                if (HasUploadCreds(repo))
                    await EnsureCredentialSecretAsync($"{repo}-upload-creds", RsUser, RsPassword);
            }
            await EnsureCredentialSecretAsync("pup-up-users", ClientUser, ClientPassword);

            // ── In-cluster Reposilite (the remote upstream) ────────────────────
            var rsConfCm = $"{RsAppName}-conf";
            await CreateReposiliteConfigMapAsync(rsConfCm);
            await CreateReposiliteDeploymentAsync(RsImage, rsConfCm);
            await CreateServiceAsync(RsAppName, RsPort, RsPort);
            await WaitForPodReadyAsync($"app={RsAppName}", timeoutSeconds: 600);
            await WaitUntilUpstreamRespondsAsync();

            // ── The four proxy repositories ────────────────────────────────────
            foreach (var repo in RepoNames)
                await CreateProxyRepositoryAsync(repo, upstreamUrl);

            foreach (var repo in RepoNames)
                await PodReadinessHelper.WaitForNginxReadyAsync(_client, OperatorNamespace, repo);
        }
        catch
        {
            DisposeLeases(); // don't leak port-forwards on init failure
            throw;
        }
    }

    public Task DisposeAsync()
    {
        DisposeLeases();
        return Task.CompletedTask;
    }

    private string[] RepoNames => [UploadRepoName, ReadOnlyRepoName, ExposedRepoName, ForcedRepoName, OverrideRepoName];

    /// <summary>Repos whose upload spec requires the dedicated {name}-upload-creds Secret.</summary>
    private bool HasUploadCreds(string name)
        => name == UploadRepoName || name == ExposedRepoName || name == ForcedRepoName || name == OverrideRepoName;
    private string RsAppName   => $"rs-pup-{_suffix}";

    /// <summary>Kills any lingering kubectl port-forward processes (safe to call repeatedly).</summary>
    public void DisposeLeases()
    {
        foreach (var pf in _pfProcesses)
        {
            try { if (!pf.HasExited) pf.Kill(entireProcessTree: true); } catch { }
            pf.Dispose();
        }
        _pfProcesses.Clear();
    }

    // ── Test-facing helpers ───────────────────────────────────────────────────

    public async Task<HttpResponseMessage> PutViaProxyAsync(
        string repoName, string path, byte[] content,
        string? username = null, string? password = null, CancellationToken ct = default)
    {
        var user     = username ?? ClientUser;
        var pass     = password ?? ClientPassword;
        var cred     = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{user}:{pass}"));

        var (port, pf) = await PortForwardServiceAsync($"{repoName}-svc", 80);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}"), Timeout = TimeSpan.FromSeconds(30) };
            using var req = new HttpRequestMessage(HttpMethod.Put, $"/repository/{repoName}/{path}")
            {
                Content   = new ByteArrayContent(content),
                Headers   = { Authorization = new AuthenticationHeaderValue("Basic", cred) },
            };
            return await client.SendAsync(req, ct); // caller disposes
        }
        finally { KillPf(pf); }
    }

    public async Task<(HttpStatusCode Status, byte[]? Body)> GetViaProxyAsync(string repoName, string path, CancellationToken ct = default)
    {
        var (port, pf) = await PortForwardServiceAsync($"{repoName}-svc", 80);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            using var resp = await client.GetAsync($"/repository/{repoName}/{path}", ct);
            return (resp.StatusCode, resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(ct) : null);
        }
        finally { KillPf(pf); }
    }

    public async Task<(HttpStatusCode Status, byte[]? Body)> DownloadFromUpstreamAsync(string path, CancellationToken ct = default)
    {
        var (port, pf) = await PortForwardServiceAsync(RsAppName, RsPort);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            var cred = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{RsUser}:{RsPassword}"));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/{RepoId}/{path}")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Basic", cred) },
            };
            using var resp = await client.SendAsync(req, ct);
            return (resp.StatusCode, resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(ct) : null);
        }
        finally { KillPf(pf); }
    }

    public Task<MavenRepositoryV1Alpha1> GetRepositoryAsync(string repoName) =>
        _client.GetAsync<MavenRepositoryV1Alpha1>(repoName, OperatorNamespace, CancellationToken.None)!;

    /// <summary>Polls status.conditions until the given type has the expected truth value (or null if it must be absent).</summary>
    public async Task<Entities.Status.RepositoryCondition?> WaitForConditionAsync(string repoName, string conditionType, bool isTrue, int timeoutSeconds = 120)
    {
        var wantStatus = isTrue ? "True" : "False";
        var deadline   = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var repo = await GetRepositoryAsync(repoName);
            var cond = repo.Status?.Conditions?.FirstOrDefault(c => c.Type == conditionType);
            if (cond is not null && cond.Status == wantStatus)
                return cond;
            // Condition absent: only meaningful for isTrue:false — caller should gate on a positive signal first.
            if (!isTrue && cond is null && repo.Status?.Conditions?.Any(c => c.Type == "Available" && c.Status == "True") == true)
                return null;
            await Task.Delay(2_000);
        }
        throw new TimeoutException($"Condition '{conditionType}={wantStatus}' not observed on {repoName} within {timeoutSeconds}s.");
    }

    /// <summary>Polls cluster events for a Warning event with the given reason against the named object.</summary>
    public async Task<Corev1Event?> WaitForWarningEventAsync(string involvedObjectName, string reason, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var core = (k8s.ICoreV1Operations)_raw;
            var events = await k8s.CoreV1OperationsExtensions.ListNamespacedEventAsync(core, OperatorNamespace, cancellationToken: CancellationToken.None);
            var match = events.Items.FirstOrDefault(e =>
                e.InvolvedObject?.Name == involvedObjectName &&
                e.Reason == reason &&
                string.Equals(e.Type, "Warning", StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
            await Task.Delay(2_000);
        }
        return null;
    }

    /// <summary>
    /// Polls the mtail /metrics endpoint for maven_upload_bytes_total{repo="…"} and returns its value.
    /// Returns 0 if it never appears within the timeout.
    /// </summary>
    public async Task<long> PollMtailUploadCounterAsync(string repoName, int timeoutSeconds = 90)
    {
        var (port, pf) = await PortForwardServiceAsync($"{repoName}-svc", 3903);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}"), Timeout = TimeSpan.FromSeconds(15) };
            var pattern = new Regex(
                @"^maven_upload_bytes_total\{[^}]*repo=""" + Regex.Escape(repoName) + @"""[^}]*\}\s+([0-9.eE+-]+)\s*$",
                RegexOptions.Multiline);

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var resp  = await client.GetAsync("/metrics");
                    var body        = await resp.Content.ReadAsStringAsync();
                    var match       = pattern.Match(body);
                    if (match.Success && double.TryParse(match.Groups[1].Value, out var value))
                        return (long)value;
                }
                catch { /* mtail not scraping yet */ }
                await Task.Delay(3_000);
            }
            return 0;
        }
        finally { KillPf(pf); }
    }

    // ── Provisioning internals ────────────────────────────────────────────────

    private static string SharedConfigJson => $$"""
        {
          "maven" : {
            "repositories" : [ {
              "id" : "{{RepoId}}",
              "visibility" : "PUBLIC",
              "storageProvider" : { "type" : "fs", "quota" : "100%", "mount" : "" },
              "redeployment" : false,
              "preserveSnapshots" : false,
              "proxied" : [ ]
            } ]
          }
        }
        """;

    private static string RsOpts => $"-wd /data -p {RsPort} -sc /data/conf/configuration.shared.json -t {RsUser}:{RsPassword}";

    private async Task CreateReposiliteConfigMapAsync(string name)
    {
        await EnsureObjectAsync(new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = OperatorNamespace },
            Data     = new Dictionary<string, string> { ["configuration.shared.json"] = SharedConfigJson },
        });
    }

    private async Task CreateReposiliteDeploymentAsync(string image, string confCmName)
    {
        await EnsureObjectAsync(new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind       = "Deployment",
            Metadata   = new V1ObjectMeta { Name = RsAppName, NamespaceProperty = OperatorNamespace },
            Spec = new V1DeploymentSpec
            {
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = RsAppName } },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string> { ["app"] = RsAppName } },
                    Spec     = new V1PodSpec
                    {
                        Containers =
                        [
                            new V1Container
                            {
                                Name    = "reposilite",
                                Image   = image,
                                Ports   = [new V1ContainerPort { ContainerPort = RsPort }],
                                Env     = [new V1EnvVar { Name = "REPOSILITE_OPTS", Value = RsOpts }],
                                ReadinessProbe = new V1Probe
                                {
                                    // "/" (dashboard) is the only unauthenticated 2xx path.
                                    HttpGet          = new V1HTTPGetAction { Path = "/", Port = RsPort },
                                    InitialDelaySeconds = 5,
                                    PeriodSeconds       = 10,
                                    FailureThreshold    = 60,
                                },
                                VolumeMounts =
                                [
                                    new V1VolumeMount { Name = "data", MountPath = "/data" },
                                    new V1VolumeMount { Name = "conf", MountPath = "/data/conf" },
                                ],
                            },
                        ],
                        Volumes =
                        [
                            new V1Volume { Name = "data", EmptyDir = new V1EmptyDirVolumeSource() },
                            new V1Volume { Name = "conf", ConfigMap = new V1ConfigMapVolumeSource { Name = confCmName } },
                        ],
                    },
                },
            },
        });
    }

    private async Task CreateServiceAsync(string name, int port, int targetPort)
    {
        await EnsureObjectAsync(new V1Service
        {
            ApiVersion = "v1",
            Kind       = "Service",
            Metadata   = new V1ObjectMeta { Name = name, NamespaceProperty = OperatorNamespace },
            Spec = new V1ServiceSpec
            {
                Selector  = new Dictionary<string, string> { ["app"] = name },
                Ports     = [new V1ServicePort { Port = port, TargetPort = targetPort }],
            },
        });
    }

    private async Task CreateProxyRepositoryAsync(string name, string upstreamUrl)
    {
        var isExposed   = name == ExposedRepoName || name == ForcedRepoName;
        var isOverride  = name == OverrideRepoName;
        var uploadCreds = HasUploadCreds(name);

        await EnsureObjectAsync(new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta
            {
                Name              = name,
                NamespaceProperty = OperatorNamespace,
                Annotations       = isExposed
                    ? new Dictionary<string, string> { ["maven.operator.io/externally-exposed"] = "true" }
                    : null,
            },
            Spec = new MavenRepositorySpec
            {
                Type    = RepositoryType.Proxy,
                Metrics = name == UploadRepoName
                    // mtail + exporter sidecars for the metrics test (test f).
                    ? new MetricsSpec { Enabled = true, ExporterPort = 9113, MtailPort = 3903 }
                    : new MetricsSpec { Enabled = false },
                Upstream = new UpstreamSpec
                {
                    Url  = upstreamUrl,
                    Auth = new UpstreamAuthSpec { SecretRef = $"{name}-upstream" },
                    Upload = uploadCreds ? new ProxyUploadSpec
                        {
                            Enabled                  = true,
                            Mode                     = isOverride ? ProxyUploadMode.Override : ProxyUploadMode.Passthrough,
                            ForceAllowOnExternal     = name == ForcedRepoName,
                            UpstreamCredentialsRef   = new LocalObjectReference { Name = $"{name}-upload-creds" },
                        }
                        : new ProxyUploadSpec { Enabled = false },
                },
                Auth = new AuthSpec
                {
                    Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                    // Override mode skips the client gate entirely (migration mode).
                    Upload   = uploadCreds && !isOverride ? new AuthPolicySpec
                        {
                            Policy  = AuthPolicy.Authenticated,
                            Users   = [new UserRef { SecretRef = "pup-up-users", Role = UserRole.Deployer }],
                        }
                        : new AuthPolicySpec { Policy = AuthPolicy.Anonymous },
                },
            },
        });
    }

    private async Task EnsureCredentialSecretAsync(string name, string username, string password)
    {
        await EnsureObjectAsync(new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = OperatorNamespace },
            Type     = "Opaque",
            Data     = new Dictionary<string, byte[]>
            {
                ["username"] = System.Text.Encoding.UTF8.GetBytes(username),
                ["password"] = System.Text.Encoding.UTF8.GetBytes(password),
            },
        });
    }

    private async Task EnsureNamespaceAsync(string name)
    {
        try
        {
            await _client.CreateAsync<V1Namespace>(new V1Namespace { Metadata = new V1ObjectMeta { Name = name } }, CancellationToken.None);
        }
        catch { /* already exists */ }
    }

    private async Task EnsureObjectAsync<T>(T body) where T : IKubernetesObject<V1ObjectMeta>
    {
        try { await _client.CreateAsync(body, CancellationToken.None); }
        catch { /* AlreadyExists from a crashed prior run — proceed */ }
    }

    /// <summary>
    /// Creates a CR that violates the CEL rule "upload.enabled=true requires
    /// upstreamCredentialsRef.name != ''" and returns the 422 rejection exception from the API server.
    /// </summary>
    public async Task<k8s.Autorest.HttpOperationException> ExpectCelRejectionAsync(string crName)
    {
        var invalid = new MavenRepositoryV1Alpha1
        {
            ApiVersion = "maven.operator.io/v1alpha1",
            Kind       = "MavenRepository",
            Metadata   = new V1ObjectMeta { Name = crName, NamespaceProperty = OperatorNamespace },
            Spec = new MavenRepositorySpec
            {
                Type = RepositoryType.Proxy,
                // Explicit anonymous download auth so ONLY the upload CEL rule fires.
                Auth = new AuthSpec { Download = new AuthPolicySpec { Policy = AuthPolicy.Anonymous } },
                Upstream = new UpstreamSpec
                {
                    Url  = $"http://{RsAppName}.{OperatorNamespace}.svc.cluster.local:{RsPort}/{RepoId}",
                    Auth = new UpstreamAuthSpec { SecretRef = $"{crName}-upstream" },
                    Upload = new ProxyUploadSpec
                    {
                        Enabled                  = true,
                        Mode                     = ProxyUploadMode.Passthrough,
                        // Empty name — the CEL rule demands a non-empty upstreamCredentialsRef.name.
                        UpstreamCredentialsRef   = new LocalObjectReference { Name = "" },
                    },
                },
            },
        };

        return await Assert.ThrowsAsync<k8s.Autorest.HttpOperationException>(
            () => _client.CreateAsync(invalid, CancellationToken.None));
    }

    private async Task WaitForPodReadyAsync(string labelSelector, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var pods = await _client.ListAsync<V1Pod>(OperatorNamespace,
                labelSelector: labelSelector, cancellationToken: CancellationToken.None);
            if (pods.Any(p => p.Status?.Phase == "Running"
                              && p.Status.ContainerStatuses is { Count: > 0 } cs && cs.All(c => c.Ready)))
                return;
            await Task.Delay(2_000);
        }
        throw new TimeoutException($"Pod(s) '{labelSelector}' in '{OperatorNamespace}' not Ready within {timeoutSeconds}s.");
    }

    /// <summary>
    /// The kubelet reports the container Ready as soon as it starts — well before the JVM
    /// binds. Poll a port-forwarded HTTP probe until any response arrives (401/404 count).
    /// </summary>
    private async Task WaitUntilUpstreamRespondsAsync()
    {
        var (port, pf) = await PortForwardServiceAsync(RsAppName, RsPort);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            var deadline = DateTime.UtcNow.AddSeconds(180);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var resp = await client.GetAsync($"/{RepoId}/", CancellationToken.None);
                    return; // any HTTP response means the server is up
                }
                catch
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("Reposilite did not start listening in time.");
                    await Task.Delay(2_000);
                }
            }
        }
        finally { KillPf(pf); }
    }

    private async Task<(int Port, Process Process)> PortForwardServiceAsync(string svcName, int remotePort)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var psi = new ProcessStartInfo
        {
            FileName               = "kubectl",
            Arguments              = $"port-forward svc/{svcName} {localPort}:{remotePort} -n {OperatorNamespace}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        var process = new Process { StartInfo = psi };
        process.Start();
        _pfProcesses.Add(process);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, localPort);
                return (localPort, process);
            }
            catch { await Task.Delay(500); }
        }
        throw new TimeoutException($"port-forward to svc/{svcName} did not come up.");
    }

    private static void KillPf(Process? pf)
    {
        try { if (pf is not null && !pf.HasExited) pf.Kill(entireProcessTree: true); } catch { }
        pf?.Dispose();
    }

    /// <summary>Best-effort removal of resources leaked by crashed prior runs.</summary>
    private async Task CleanupStaleRunsAsync()
    {
        var ct = CancellationToken.None;

        // Stale MavenRepository CRs from earlier runs (random suffixes — match on prefix).
        var repos = await _client.ListAsync<MavenRepositoryV1Alpha1>(OperatorNamespace, cancellationToken: ct);
        foreach (var repo in repos.Where(r => r.Metadata.Name is { } n && n.StartsWith("pup-")))
            try { await _client.DeleteAsync(repo, ct); } catch { /* best-effort */ }

        // Stale Secrets (owner refs may not cover manually-created ones).
        var secrets = await _client.ListAsync<V1Secret>(OperatorNamespace, cancellationToken: ct);
        foreach (var s in secrets.Where(x => x.Metadata.Name is { } n && n.StartsWith("pup-")))
            try { await _client.DeleteAsync(s, ct); } catch { /* best-effort */ }

        // Stale Reposilite deployments/services/configmaps.
        foreach (var d in (await _raw.ListDeploymentForAllNamespacesAsync(cancellationToken: ct)).Items)
            if (d.Metadata.Name is { } n && n.StartsWith("rs-pup-"))
                try { await _raw.DeleteNamespacedDeploymentAsync(n, d.Metadata.NamespaceProperty ?? OperatorNamespace, cancellationToken: ct); } catch { }
        foreach (var s in (await _raw.ListServiceForAllNamespacesAsync(cancellationToken: ct)).Items)
            if (s.Metadata.Name is { } n && n.StartsWith("rs-pup-"))
                try { await _raw.DeleteNamespacedServiceAsync(n, s.Metadata.NamespaceProperty ?? OperatorNamespace, cancellationToken: ct); } catch { }
        foreach (var c in (await _raw.ListConfigMapForAllNamespacesAsync(cancellationToken: ct)).Items)
            if (c.Metadata.Name is { } n && n.StartsWith("rs-pup-"))
                try { await _raw.DeleteNamespacedConfigMapAsync(n, c.Metadata.NamespaceProperty ?? OperatorNamespace, cancellationToken: ct); } catch { }
    }
}
