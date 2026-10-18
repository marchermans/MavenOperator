using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using k8s.Models;
using MavenOperator.Tests.E2E.Infrastructure;
using Shouldly;

namespace MavenOperator.Tests.E2E.Hosted;

/// <summary>
/// End-to-end tests for the metadata-broker sidecar (plan §9,
/// .agent/plan/maven-metadata/hosted-metadata-broker.md).
///
/// Validates against a live hosted repository in the cluster:
///   1. Race simulation — parallel version uploads + concurrent maven-metadata.xml
///      PUTs must not clobber each other; a single GET lists ALL versions (the
///      exact failure mode that motivated the broker).
///   2. Import-job path — a version dir written directly on the PVC (no HTTP PUT)
///      is picked up by the next metadata GET (disk = source of truth).
///   3. Fallback — with the broker container stopped, metadata requests return
///      409 + Retry-After while artifact WebDAV traffic keeps working; after the
///      kubelet restarts the container, metadata serving recovers from disk.
///   4. ETag/304 — conditional GET on a listing returns 304 with a matching tag.
///
/// Run with: ./scripts/run-tests.sh e2e  (or E2E_TESTS=true dotnet test --filter Category=E2E)
/// </summary>
// Per-test scoping: xUnit instantiates this class once per test method, so the
// field below is a fresh E2EFixture for every test — each one provisions its own
// repository and tears it down afterwards (no shared state between tests).
[Trait("Category", "E2E")]
public sealed class MetadataBrokerE2ETests : IAsyncLifetime
{
    private readonly E2EFixture e2e = new();

    public async Task InitializeAsync() => await e2e.InitializeAsync();
    public async Task DisposeAsync()  => await e2e.DisposeAsync();

    // Must match E2EFixture.OperatorNamespace.
    private const string Namespace = "maven-e2e";

    // Coordinates shared by every test in this class (distinct artifacts → no cross-test state).
    private const string GroupPath = "io/mavenoperator/fixture";

    [E2EFact]
    public async Task Concurrent_VersionUploads_SingleGet_ListsBothVersions()
    {
        const string artifact = "concurrent-race";
        var jarA  = $"{GroupPath}/{artifact}/1.0.0/{artifact}-1.0.0.jar";
        var jarB  = $"{GroupPath}/{artifact}/2.0.0/{artifact}-2.0.0.jar";
        var meta  = $"{GroupPath}/{artifact}/maven-metadata.xml";

        // The metadata PUT body is whatever a Maven client would have written —
        // the broker drains and discards it, so both bodies may be stale/incomplete.
        var staleMetaA = $"""
            <metadata>
              <groupId>io.mavenoperator.fixture</groupId>
              <artifactId>{artifact}</artifactId>
              <versioning><versions><version>1.0.0</version></versions></versioning>
            </metadata>
            """;
        var staleMetaB = $"""
            <metadata>
              <groupId>io.mavenoperator.fixture</groupId>
              <artifactId>{artifact}</artifactId>
              <versioning><versions><version>2.0.0</version></versions></versioning>
            </metadata>
            """;

        // Fire all four requests in parallel: two version dirs + two metadata PUTs,
        // exactly the interleaving that lost updates under plain WebDAV dav writes.
        var results = await Task.WhenAll(
        [
            e2e.PutArtifactAsync(jarA,  Encoding.UTF8.GetBytes("jar-A"), e2e.UploadUser, e2e.UploadPassword),
            e2e.PutArtifactAsync(jarB,  Encoding.UTF8.GetBytes("jar-B"), e2e.UploadUser, e2e.UploadPassword),
            e2e.PutArtifactAsync(meta,  Encoding.UTF8.GetBytes(staleMetaA), e2e.UploadUser, e2e.UploadPassword),
            e2e.PutArtifactAsync(meta,  Encoding.UTF8.GetBytes(staleMetaB), e2e.UploadUser, e2e.UploadPassword),
        ]);

        foreach (var putStatus in results)
        {
            new[] { HttpStatusCode.Created, HttpStatusCode.NoContent }
                .ShouldContain(putStatus, $"Concurrent upload/PUT should return 201 or 204, got {putStatus}");
        }

        // One GET must see BOTH versions — with dav writes one stale metadata body
        // would have won and hidden the other version.
        var (status, rawBody) = await e2e.GetArtifactAsync(meta, e2e.DownloadUser, e2e.DownloadPassword);
        status.ShouldBe(HttpStatusCode.OK);
        rawBody.ShouldNotBeNull("Listing GET should return a body");
        var body = Encoding.UTF8.GetString(rawBody!);
        body.Contains("<version>1.0.0</version>").ShouldBeTrue("Version A missing from regenerated listing:\n" + body);
        body.Contains("<version>2.0.0</version>").ShouldBeTrue("Version B missing from regenerated listing:\n" + body);
        body.Contains($"<latest>2.0.0</latest>").ShouldBeTrue("<latest> should be the highest version:\n" + body);
    }

    [E2EFact]
    public async Task DirectPvcWrite_WithoutHttpPut_IsListedOnNextGet()
    {
        // Simulates the import job: it writes artifacts straight onto the PVC with
        // no metadata PUT, so the broker must regenerate from disk on the next GET.
        const string artifact = "direct-write";
        var versionDir = $"/var/maven/repository/{GroupPath}/{artifact}/3.0.0";
        var pom = "<project xmlns=\"http://maven.apache.org/POM/4.0.0\"><modelVersion>4.0.0</modelVersion>" +
                  $"<groupId>io.mavenoperator.fixture</groupId><artifactId>{artifact}</artifactId>" +
                  "<version>3.0.0</version></project>";

        var pod = await FindNginxPodNameAsync();
        var script = $"mkdir -p {versionDir} && printf %s '{pom}' > {versionDir}/{artifact}-3.0.0.pom";
        var (exit, output) = await KubectlExec(pod, "nginx", ["sh", "-c", script]);
        exit.ShouldBe(0, $"Direct PVC write failed:\n{output}");

        var (status, rawBody) = await e2e.GetArtifactAsync(
            $"{GroupPath}/{artifact}/maven-metadata.xml", e2e.DownloadUser, e2e.DownloadPassword);

        status.ShouldBe(HttpStatusCode.OK);
        rawBody.ShouldNotBeNull();
        var listing = Encoding.UTF8.GetString(rawBody!);
        listing.Contains("<version>3.0.0</version>").ShouldBeTrue(
            "Version written directly to the PVC must appear in the regenerated listing:\n" + listing);
    }

    [E2EFact]
    public async Task BrokerDown_MetadataRequests_Return409WithRetryAfter_ArtifactsUnaffected()
    {
        const string artifact = "fallback-probe";
        var jar   = $"{GroupPath}/{artifact}/1.0.0/{artifact}-1.0.0.jar";
        var meta  = $"{GroupPath}/{artifact}/maven-metadata.xml";

        // Warm up: make sure the broker is currently serving this artifact's listing.
        await e2e.PutArtifactAsync(jar, Encoding.UTF8.GetBytes("jar"), e2e.UploadUser, e2e.UploadPassword);
        var (warmStatus, warmRaw) = await e2e.GetArtifactAsync(meta, e2e.DownloadUser, e2e.DownloadPassword);
        warmStatus.ShouldBe(HttpStatusCode.OK, "Broker should serve the listing before we stop it");
        Encoding.UTF8.GetString(warmRaw!).ShouldContain("<version>1.0.0</version>");

        var pod = await FindNginxPodNameAsync();

        // Kill PID 1 (dotnet) inside the broker container → kubelet restarts it after a short window.
        // The down window is only ~5-20 s, so retry the kill/catch loop if the broker recovers first.
        var caught409 = false;
        string? downBody = null;
        for (var attempt = 1; attempt <= 8 && !caught409; attempt++)
        {
            await KubectlExec(pod, "metadata-broker", ["kill", "1"]); // best-effort; stream may drop when the container dies

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!cts.IsCancellationRequested && !caught409)
            {
                var (status, body, retryAfter, _) = await RawGetAsync(meta, e2e.DownloadUser, e2e.DownloadPassword);
                if (status == HttpStatusCode.Conflict && retryAfter is not null)
                {
                    caught409 = true;
                    downBody = body;
                }
                else
                {
                    //We explicitly do not pass the CTS token here
                    await Task.Delay(250, CancellationToken.None).ConfigureAwait(false); // 200 → broker already back; try again next attempt
                }
            }
        }

        caught409.ShouldBeTrue("Never observed the broker-down 409 fallback across 8 kill attempts");

        downBody!.Contains("retry").ShouldBeTrue($"Fallback body should tell dav clients to retry, got: {downBody}");

        // Metadata PUT while down → 409 + Retry-After (no partial write via dav).
        var (putStatus, putRetry) = await RawPutAsync(
            meta, Encoding.UTF8.GetBytes("<metadata/>"), e2e.UploadUser, e2e.UploadPassword);
        // The broker may have recovered between the two requests; accept 409 or a healthy ack.
        if (putStatus == HttpStatusCode.Conflict)
        {
            putRetry.ShouldNotBeNull("Metadata PUT during outage must carry Retry-After");
        }
        else
        {
            new[] { HttpStatusCode.Created, HttpStatusCode.NoContent }.ShouldContain(putStatus);
        }

        // Artifact WebDAV traffic is on a separate location and unaffected by the broker.
        var artifactPut = await e2e.PutArtifactAsync(
            $"{GroupPath}/{artifact}/1.1.0/{artifact}-1.1.0.jar",
            Encoding.UTF8.GetBytes("jar-110"), e2e.UploadUser, e2e.UploadPassword);
        new[] { HttpStatusCode.Created, HttpStatusCode.NoContent }
            .ShouldContain(artifactPut, $"Artifact PUT must not be affected by broker outage, got {artifactPut}");

        // Wait for the kubelet to bring the broker back; metadata serving recovers from disk.
        using var recoverCts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        string? recoveredBody = null;
        while (!recoverCts.IsCancellationRequested)
        {
            var (status, raw) = await e2e.GetArtifactAsync(meta, e2e.DownloadUser, e2e.DownloadPassword);
            if (status == HttpStatusCode.OK && raw is not null) { recoveredBody = Encoding.UTF8.GetString(raw); break; }
            await Task.Delay(1000, recoverCts.Token);
        }

        recoveredBody.ShouldNotBeNull("Broker did not recover within 180 s");
        recoveredBody!.Contains("<version>1.0.0</version>").ShouldBeTrue("Old version lost after broker restart:\n" + recoveredBody);
        recoveredBody.Contains("<version>1.1.0</version>").ShouldBeTrue("Version uploaded during outage missing after recovery:\n" + recoveredBody);
    }

    [E2EFact]
    public async Task ConditionalGet_WithMatchingETag_Returns304()
    {
        const string artifact = "etag-conditional";
        var jar  = $"{GroupPath}/{artifact}/1.0.0/{artifact}-1.0.0.jar";
        var meta = $"{GroupPath}/{artifact}/maven-metadata.xml";

        await e2e.PutArtifactAsync(jar, Encoding.UTF8.GetBytes("jar"), e2e.UploadUser, e2e.UploadPassword);

        // First GET must carry a fingerprint-based ETag from the broker.
        var (status1, _, _, etag) = await RawGetAsync(meta, e2e.DownloadUser, e2e.DownloadPassword);
        status1.ShouldBe(HttpStatusCode.OK);
        etag.ShouldNotBeNullOrEmpty("Broker listing responses must include an ETag");

        // Conditional GET with the same tag → 304 (no body re-sent).
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/repository/{e2e.RepositoryName}/{meta}");
        var cred = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{e2e.DownloadUser}:{e2e.DownloadPassword}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", cred);
        req.Headers.TryAddWithoutValidation("If-None-Match", etag!);

        using var resp = await e2e.HttpClient.SendAsync(req);
        resp.StatusCode.ShouldBe(HttpStatusCode.NotModified,
            $"Conditional GET with matching ETag should return 304, got {(int)resp.StatusCode}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>GET that always reads the body and surfaces ETag / Retry-After headers.</summary>
    private async Task<(HttpStatusCode Status, string? Body, string? RetryAfter, string? ETag)> RawGetAsync(
        string repoRelPath, string user, string password)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/repository/{e2e.RepositoryName}/{repoRelPath}");
        var cred = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{password}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", cred);

        using var resp = await e2e.HttpClient.SendAsync(req);
        var body = (await resp.Content.ReadAsStringAsync()).ToString();
        return (resp.StatusCode, body,
            resp.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(),
            resp.Headers.ETag?.Tag);
    }

    /// <summary>PUT that surfaces the Retry-After header on error responses.</summary>
    private async Task<(HttpStatusCode Status, string? RetryAfter)> RawPutAsync(
        string repoRelPath, byte[] content, string user, string password)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/repository/{e2e.RepositoryName}/{repoRelPath}");
        req.Content = new ByteArrayContent(content);
        var cred = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{password}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", cred);

        using var resp = await e2e.HttpClient.SendAsync(req);
        _ = await resp.Content.ReadAsStringAsync(); // drain
        return (resp.StatusCode, resp.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
    }

    private async Task<string> FindNginxPodNameAsync()
    {
        var pods = await e2e.Client.ListAsync<V1Pod>(
            Namespace, labelSelector: $"app={e2e.RepositoryName}-nginx");

        var pod = pods.FirstOrDefault(p => p.Status?.Phase == "Running") ?? pods.FirstOrDefault();
        pod.ShouldNotBeNull($"No NGINX pod found for '{e2e.RepositoryName}' in namespace '{Namespace}'");
        return pod!.Metadata.Name;
    }

    /// <summary>Runs a command inside the repository pod via kubectl (ArgumentList → no shell quoting).</summary>
    private static async Task<(int ExitCode, string Output)> KubectlExec(string pod, string container, IReadOnlyList<string> command)
    {
        var psi = new ProcessStartInfo
        {
            FileName          = "kubectl",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute    = false,
        };
        var kubectlArgs = new[] { "-n", Namespace, "exec", pod, "-c", container, "--" };
        foreach (var arg in kubectlArgs)
            psi.ArgumentList.Add(arg);
        foreach (var arg in command)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            process.Start();
            var exitTask = process.WaitForExitAsync(cts.Token);
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await exitTask; // throws OperationCanceledException if the token fires first

            return (process.ExitCode, stdout + stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return (-1, "kubectl exec timed out");
        }
        catch (Exception ex)
        {
            return (-1, $"kubectl exec failed to run: {ex.Message}");
        }
    }
}
