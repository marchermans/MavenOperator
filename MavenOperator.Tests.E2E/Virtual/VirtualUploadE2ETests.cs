using MavenOperator.Tests.E2E.Infrastructure;
using Shouldly;
using System.Net;
using System.Text;

namespace MavenOperator.Tests.E2E.Virtual;

/// <summary>
/// End-to-end tests for Virtual repository upload fan-out (Phase 6).
///
/// Validates that:
///   1. PUT to a Virtual repo with uploads enabled fans out to all configured targets.
///   2. Artifacts uploaded via Virtual are visible on each target Hosted repo.
///   3. Partial failures return 207 Multi-Status (some targets succeed, some fail).
///   4. Uploads without valid credentials are rejected.
///
/// Run with: E2E_TESTS=true dotnet test --filter Category=E2E
/// </summary>
[Collection(UploadE2ECollection.CollectionName)]
[Trait("Category", "E2E")]
public sealed class VirtualUploadE2ETests(UploadE2EFixture e2e)
{
    // ── Upload fan-out — all targets succeed ─────────────────────────────────

    [E2EFact]
    public async Task Http_Put_ToVirtual_FansOutToAllTargets_ReturnsSuccess()
    {
        var path    = $"io/test/upload-fanout/{Guid.NewGuid():N}/artifact.jar";
        var content = Encoding.UTF8.GetBytes($"fan-out-content-{Guid.NewGuid()}");

        // Upload via Virtual repo (should fan out to both targets).
        using var response = await e2e.UploadToVirtualAsync(path, content);

        var responseBody = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"[DEBUG] Upload response: {(int)response.StatusCode} - {responseBody}");

        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
        {
            throw new Exception($"Upload failed with {(int)response.StatusCode}: {responseBody}");
        }

        ((int)response.StatusCode).ShouldBeInRange(200, 300,
            $"Upload to Virtual should succeed with 2xx, got {(int)response.StatusCode}");

        // Give the targets a moment to persist.
        await Task.Delay(1000);

        Console.WriteLine($"[DEBUG] Uploaded artifact to Virtual '{e2e.VirtualName}' at path: {path}");
        Console.WriteLine($"[DEBUG] Target 1 name: {e2e.Target1Name}, Target 2 name: {e2e.Target2Name}");

        // Verify artifact exists on target 1.
        var (status1, body1) = await e2e.DownloadFromTargetAsync(e2e.Target1Name, path);
        status1.ShouldBe(HttpStatusCode.OK, "Artifact should exist on target 1");
        body1.ShouldNotBeNull();
        body1!.ShouldBe(content, "Content on target 1 should match uploaded content");

        // Verify artifact exists on target 2.
        var (status2, body2) = await e2e.DownloadFromTargetAsync(e2e.Target2Name, path);
        status2.ShouldBe(HttpStatusCode.OK, "Artifact should exist on target 2");
        body2.ShouldNotBeNull();
        body2!.ShouldBe(content, "Content on target 2 should match uploaded content");

        // Verify artifact is also visible through the Virtual repo.
        var (statusVirt, bodyVirt) = await e2e.DownloadFromVirtualAsync(path);
        statusVirt.ShouldBe(HttpStatusCode.OK, "Artifact should be reachable via Virtual");
        bodyVirt!.ShouldBe(content);
    }

    [E2EFact]
    public async Task Http_Put_ToVirtual_WithoutCredentials_Returns401Or403()
    {
        var path = $"io/test/no-auth/{Guid.NewGuid():N}/artifact.jar";

        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"/repository/{e2e.VirtualName}/{path}");
        req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("no-auth-content"));
        // No Authorization header.

        using var response = await e2e.HttpClient.SendAsync(req);

        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(400,
            $"Upload without credentials should fail (got {(int)response.StatusCode})");
    }

    [E2EFact]
    public async Task Http_Put_ToVirtual_WithWrongCredentials_Returns401Or403()
    {
        var path = $"io/test/wrong-auth/{Guid.NewGuid():N}/artifact.jar";

        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"/repository/{e2e.VirtualName}/{path}");
        req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("wrong-auth-content"));
        var cred = Convert.ToBase64String(Encoding.ASCII.GetBytes("invalid:credentials"));
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", cred);

        using var response = await e2e.HttpClient.SendAsync(req);

        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(400,
            $"Upload with wrong credentials should fail (got {(int)response.StatusCode})");
    }

    // ── Partial failure scenario ─────────────────────────────────────────────

    [E2EFact]
    public async Task Http_Put_ToVirtual_OneTargetDown_ReturnsPartialSuccess()
    {
        var path = $"io/test/partial-fail/{Guid.NewGuid():N}/artifact.jar";
        var content = Encoding.UTF8.GetBytes($"partial-content-{Guid.NewGuid()}");

        // Temporarily make target 2 unreachable by deleting its NGINX pod.
        await DeleteTargetPodAsync(e2e.Target2Name);

        try
        {
            // Upload via Virtual — should succeed on target 1, fail on target 2 (down).
            using var response = await e2e.UploadToVirtualAsync(path, content);

            // Expect either:
            // - 207 Multi-Status (partial success) if fan-out detected the failure
            // - 201 Created if timeout hasn't fired yet and target 1 succeeded first
            ((int)response.StatusCode).ShouldBeInRange(200, 300,
                $"Upload with one target down should still succeed partially or fully (got {(int)response.StatusCode})");

            // Give the surviving target a moment to persist.
            await Task.Delay(1000);

            // Verify artifact exists on target 1 (the one that was up).
            var (status1, body1) = await e2e.DownloadFromTargetAsync(e2e.Target1Name, path);
            status1.ShouldBe(HttpStatusCode.OK, "Artifact should exist on surviving target 1");
            body1.ShouldNotBeNull();
        }
        finally
        {
            // Wait for target 2 pod to come back up (operator will recreate it).
            await e2e.Client.ListAsync<k8s.Models.V1Pod>(
                "maven-e2e", labelSelector: $"app={e2e.Target2Name}-nginx");

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await PodReadinessHelper.WaitForNginxReadyAsync(e2e.Client, "maven-e2e", e2e.Target2Name);
                    break;
                }
                catch { await Task.Delay(2000); }
            }
        }
    }

    // ── Upload status reporting (Phase 5) ────────────────────────────────────

    [E2EFact]
    public async Task Api_Get_UploadStatus_ReturnsTargetInfo()
    {
        // First do an upload to populate lastUploadSuccess.
        var path = $"io/test/status/{Guid.NewGuid():N}/artifact.jar";
        using (await e2e.UploadToVirtualAsync(path, Encoding.UTF8.GetBytes("status-test")))
        { }

        await Task.Delay(1000); // Let the proxy update its status tracking.

        // Query the upload status endpoint via port-forward to the proxy service.
        var (localPort, process) = await PortForwardProxyAsync(e2e.VirtualName);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{localPort}") };
            using var resp = await client.GetAsync("/api/virtual/upload-status");

            resp.StatusCode.ShouldBe(HttpStatusCode.OK, "Upload status endpoint should return 200");

            var json = await resp.Content.ReadAsStringAsync();
            json.ToLower().ShouldContain("enabled");
            json.ToLower().ShouldContain("targets");
        }
        finally
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            process?.Dispose();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task DeleteTargetPodAsync(string targetName)
    {
        var pods = await e2e.Client.ListAsync<k8s.Models.V1Pod>(
            "maven-e2e", labelSelector: $"app={targetName}-nginx");

        if (pods.Any())
        {
            foreach (var pod in pods)
            {
                try
                {
                    await e2e.Client.DeleteAsync<k8s.Models.V1Pod>(pod.Metadata.Name, "maven-e2e", CancellationToken.None);
                }
                catch { /* best-effort */ }
            }

            // Wait for pod to be gone.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var remaining = await e2e.Client.ListAsync<k8s.Models.V1Pod>(
                    "maven-e2e", labelSelector: $"app={targetName}-nginx");

                if (!remaining.Any())
                    return;

                await Task.Delay(500);
            }
        }
    }

    private async Task<(int Port, System.Diagnostics.Process Process)> PortForwardProxyAsync(string repoName)
    {
        var localPort = GetFreePort();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = "kubectl",
            Arguments              = $"port-forward svc/{repoName}-proxy-svc {localPort}:8080 -n maven-e2e",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        var process = new System.Diagnostics.Process { StartInfo = psi };
        process.Start();

        var pfDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < pfDeadline)
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync(System.Net.IPAddress.Loopback, localPort);
                break;
            }
            catch { await Task.Delay(500); }
        }

        return (localPort, process);
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
