# Plan: Metrics Support for Virtual Repositories

## Goal

Enable `spec.metrics.enabled: true` on Virtual repositories so that the `nginx-prometheus-exporter` and `mtail` sidecars are injected into the single pod (same pattern as Hosted and Proxy repos). Currently, Virtual pods have exactly 2 containers (`nginx` + `proxy`) regardless of the metrics setting — no metrics collectors are injected.

---

## Current State (Before)

| Component | Containers |
|---|---|
| Virtual pod (metrics **disabled**) | `nginx`, `proxy` |
| Virtual pod (metrics **enabled**) | `nginx`, `proxy` (no-op — sidecars not injected) |

**Code location:** `MavenOperator/Reconcilers/VirtualRepositoryReconciler.cs` → `BuildVirtualPodSpec()` returns exactly 2 containers. No `if (spec.Metrics.Enabled)` branch exists.

The NGINX config block also lacks a `listen <stub_status_port>` + `stub_status` block — the inline `RenderNginxVirtualConfig()` method writes the config as a C# string rather than using the Scriban template (which already handles `stub_status` when `metrics_enabled` is true).

---

## Changes

### 1. `VirtualRepositoryReconciler.BuildVirtualPodSpec()` — add metrics sidecars

**File:** `MavenOperator/Reconcilers/VirtualRepositoryReconciler.cs`

Update `BuildVirtualPodSpec(string name, MavenRepositorySpec spec)` to accept the metrics config (currently unused) and conditionally add:

#### When `spec.Metrics.Enabled == true`:

**New volumes:**
- `nginx-logs` (emptyDir) — shared between NGINX (to write JSON logs) and mtail (to tail them)
- `mtail-config` (ConfigMap from `{name}-mtail-cm`) — mounts the mtail program

**New NGINX container volume mount:**
- `/var/log/nginx` on the `nginx-logs` emptyDir (so NGINX writes JSON-format logs there)

**New containers:**

| Container | Purpose | Port | Resources (fixed defaults) |
|---|---|---|---|
| `nginx-exporter` | Scrapes NGINX `stub_status` endpoint, re-exposes as Prometheus metrics | 9113 (from `spec.Metrics.ExporterPort`) | cpu 50m/10m, memory 32Mi/16Mi |
| `mtail` | Tails NGINX access JSON logs, emits `maven_artifact_requests_total` etc. | 3903 (from `spec.Metrics.MtailPort`) | cpu 100m/20m, memory 64Mi/32Mi |

**Container details:**

- **nginx-exporter**: Uses `spec.Metrics.NginxExporterImage` (default `nginx/nginx-prometheus-exporter:1.4`), args: `--nginx.scrape-uri=http://127.0.0.1:<StubStatusPort>/stub_status`. No volume mounts. Security context: `AllowPrivilegeEscalation: false`, `ReadOnlyRootFilesystem: true`, drop ALL capabilities.
- **mtail**: Uses `spec.Metrics.MtailImage` (default `ghcr.io/google/mtail:latest`), args: `--progs=/etc/mtail/maven.mtail --logs=/var/log/nginx/access.json --port=<MtailPort> --expired_metrics_gc_interval=168h --logtostderr`. Mounts: `nginx-logs` at `/var/log/nginx` (read-only), `mtail-config` at `/etc/mtail` (read-only). Security context: `AllowPrivilegeEscalation: false`, `ReadOnlyRootFilesystem: false` (writes runtime state under `/tmp`), drop ALL capabilities.

**Important:** The `proxy` sidecar's `/health` endpoint on port 8080 is **unaffected** — it continues to serve the operator's `upload-status` polling. The metrics ports (9113, 3903) are separate and exposed on the Service.

**Container count after change:**
- Metrics **disabled** (default): 2 containers (`nginx`, `proxy`)
- Metrics **enabled**: 4 containers (`nginx`, `proxy`, `nginx-exporter`, `mtail`)

### 2. `VirtualRepositoryReconciler.BuildVirtualPodSpec()` — add `stub_status` to NGINX config

**File:** `MavenOperator/Reconcilers/VirtualRepositoryReconciler.cs`

In `RenderNginxVirtualConfig()`, when `spec.Metrics.Enabled` is true, add:

```nginx
server {
    listen <StubStatusPort>;
    server_name _;

    location /healthz {
        access_log off;
        return 200 "OK\n";
        add_header Content-Type text/plain;
    }

    location /stub_status {
        stub_status;
    }
}
```

This block sits outside the main `server { listen 80; ... }` block, matching the pattern in both `nginx-hosted.conf.scriban` and `nginx-proxy.conf.scriban` (which already render this block conditionally when `metrics_enabled` is true).

### 3. `VirtualRepositoryReconciler.ReconcileAsync()` — ensure mtail ConfigMap

**File:** `MavenOperator/Reconcilers/VirtualRepositoryReconciler.cs`

In the reconciliation pipeline, after the NGINX ConfigMap is created:

```csharp
if (spec.Metrics.Enabled)
{
    var mtailConfig = nginx.RenderMtailConfig();
    await resources.EnsureConfigMapAsync(entity, $"{name}-mtail-cm",
        new Dictionary<string, string> { ["maven.mtail"] = mtailConfig }, ct);
}
```

This creates a `mtail-config` ConfigMap with the maven.mtail program — the same call Hosted and Proxy repos make.

### 4. Service — expose metrics ports on the NGINX Service

**File:** `MavenOperator/Reconcilers/VirtualRepositoryReconciler.cs`

In the method that creates the NGINX Service (`{name}-svc`), add metrics ports when metrics are enabled (matching `BuildServicePorts` from Hosted/Proxy):

```csharp
var ports = new List<V1ServicePort>
{
    new() { Name = "http", Port = 80, TargetPort = 80 },
};
if (spec.Metrics.Enabled)
{
    ports.Add(new V1ServicePort { Name = "nginx-metrics", Port = spec.Metrics.ExporterPort, TargetPort = spec.Metrics.ExporterPort });
    ports.Add(new V1ServicePort { Name = "mtail-metrics",  Port = spec.Metrics.MtailPort,    TargetPort = spec.Metrics.MtailPort });
}
```

The proxy's status Service (`{name}-proxy-svc` on port 8080) is **unaffected**.

### 5. Cleanup — remove obsolete mtail resources when metrics is disabled

**File:** `VirtualRepositoryReconciler.cs`

In `CleanupObsoleteResourcesAsync()`, when `!spec.Metrics.Enabled`:

```csharp
await resources.DeleteCustomResourceIfExistsAsync(
    "monitoring.coreos.com", "v1", "podmonitors", $"{name}-metrics", ns, ct);
await resources.DeleteResourceIfExistsAsync<V1ConfigMap>($"{name}-mtail-cm", ns, ct);
```

This ensures that disabling metrics on an existing repo cleans up the sidecar ConfigMap and PodMonitor.

---

## Test Changes

### A. Integration Tests — `MavenOperator.Tests.Integration/Virtual/VirtualRepositoryReconcilerTests.cs`

**Existing test to update:**

```csharp
[IntegrationFact]
public async Task Reconcile_CreatesSingleMergedDeployment_WithNginxAndProxySidecar()
```

Currently asserts `containers.Count.ShouldBe(2)`. Add a new test (or parameterize the existing one):

```csharp
[IntegrationFact]
public async Task Reconcile_MetricsEnabled_Deployment_HasFourContainers()
{
    var name   = $"int-virt-m6a-{Guid.NewGuid().ToString("N")[..6]}";
    var entity = await BuildEntityAsync(name, members: ["releases"])
        with { Spec = { Metrics = new MetricsSpec { Enabled = true } } };

    // Build a new reconciler with metrics enabled
    await BuildVirtualReconcilerForMetrics().ReconcileAsync(entity, CancellationToken.None);

    var deploy = await cluster.Client.GetAsync<V1Deployment>($"{name}-nginx", cluster.Namespace, CancellationToken.None);
    deploy.ShouldNotBeNull();
    var containers = deploy.Spec!.Template.Spec!.Containers!.ToList();
    containers.Count.ShouldBe(4,
        "Metrics-enabled Virtual deployment must have nginx + proxy + nginx-exporter + mtail containers");

    containers.ShouldContain(c => c.Name == "nginx");
    containers.ShouldContain(c => c.Name == "proxy");
    containers.ShouldContain(c => c.Name == "nginx-exporter");
    containers.ShouldContain(c => c.Name == "mtail");

    // Verify proxy-sidecar port 8080 is still present
    var proxyContainer = containers.Single(c => c.Name == "proxy");
    proxyContainer.Ports.ShouldContain(p => p.ContainerPort == 8080);
}
```

**New tests to add:**

```csharp
[IntegrationFact]
public async Task Reconcile_MetricsEnabled_MtailConfigMap_IsCreated()

[IntegrationFact]
public async Task Reconcile_MetricsEnabled_Service_HasMetricsPorts()

[IntegrationFact]
public async Task Reconcile_MetricsEnabled_Deployment_HasNginxLogsVolume()

[IntegrationFact]
public async Task Reconcile_MetricsDisabled_Service_HasOnlyHttpPort()  // (new)

[IntegrationFact]
public async Task Reconcile_MetricsDisabled_Deployment_HasTwoContainers()  // (new)

[IntegrationFact]
public async Task Reconcile_DisablingMetrics_CleansUpMtailResources()  // (new)
```

### B. Integration Tests — `MavenOperator.Tests.Integration/Hardening/MetricsSidecarIntegrationTests.cs`

**New file:** `MavenOperator.Tests.Integration/Virtual/VirtualMetricsSidecarIntegrationTests.cs`

Add a full suite of metrics sidecar tests for Virtual repos, mirroring the Hosted and Proxy tests:

```csharp
namespace MavenOperator.Tests.Integration.Virtual;

public sealed class VirtualMetricsSidecarIntegrationTests(ClusterFixture cluster)
{
    private VirtualRepositoryReconciler BuildReconciler() =>
        new(
            cluster.Client,
            new KubernetesResourceManager(cluster.Client, NullLogger<KubernetesResourceManager>.Instance),
            new HtpasswdService(),
            NSubstitute.Substitute.For<IKubernetesEventService>(),
            NullLogger<VirtualRepositoryReconciler>.Instance
        );

    // Test: 4 containers when metrics enabled (nginx + proxy + nginx-exporter + mtail)
    // Test: 2 containers when metrics disabled (nginx + proxy only)
    // Test: nginx-exporter container uses correct image
    // Test: mtail container uses correct image
    // Test: mtail ConfigMap is created when metrics enabled
    // Test: Service exposes nginx-metrics and mtail-metrics ports when enabled
    // Test: Deployment has nginx-logs emptyDir volume when metrics enabled
    // Test: Service has only http port when metrics disabled
    // Test: Idempotent reconcile (second run doesn't duplicate sidecars)
}
```

### C. E2E Fixture — `MavenOperator.Tests.E2E/Infrastructure/VirtualE2EFixture.cs`

**Change:** Add a **metrics-enabled variant** of the Virtual E2E fixture, or a new `VirtualMetricsE2EFixture` class, mirroring `MetricsE2EFixture`:

- Creates a Virtual repo with `spec.Metrics.Enabled = true`
- Port-forwards the exporter port (9113) and mtail port (3903)
- The `WaitForNginxReadyAsync` call already waits for **all containers** to be Ready — after this change, it will wait for 4 containers instead of 2. The existing 300s timeout already accounts for sidecar image-pull latency.

**Note:** The default VirtualE2EFixture sets `Metrics = new MetricsSpec { Enabled = false }` to avoid sidecar pull latency. The new fixture would use `Enabled = true`.

### D. E2E Tests — `MavenOperator.Tests.E2E/Observability/MetricsObservabilityE2ETests.cs`

**New test file:** `MavenOperator.Tests.E2E/Virtual/VirtualMetricsObservabilityE2ETests.cs`

Tests mirroring `MetricsObservabilityE2ETests` but using a Virtual repository:

```csharp
[VirtualMetricsE2EFact]
public async Task NginxExporter_Endpoint_IsReachable()
// Validates exporter /metrics returns 200

[VirtualMetricsE2EFact]
public async Task MtailExporter_Endpoint_IsReachable()
// Validates mtail /metrics returns 200

[VirtualMetricsE2EFact]
public async Task NginxExporter_Metrics_ContainConnectionMetrics()
// Validates nginx_connections_active, nginx_http_requests_total

[VirtualMetricsE2EFact]
public async Task MtailExporter_Metrics_ContainMavenArtifactMetric()
// Validates maven_artifact_requests_total metric declared

[VirtualMetricsE2EFact]
public async Task Repository_WithMetricsEnabled_StillServesHttpTraffic()
// Validates the Virtual repo still serves artifact traffic normally

[VirtualMetricsE2EFact]
public async Task Repository_WithMetricsEnabled_Returns404_ForNonExistentArtifact()
// Validates 404 behavior unchanged
```

### E. Unit Tests — `MavenOperator.Tests.Unit/Services/NginxConfigRendererTests.cs`

**Note:** The Virtual repo renders its NGINX config as an inline C# string in `RenderNginxVirtualConfig()`, not via the Scriban template system. So there's no separate unit test for virtual NGINX rendering. However, the existing `RenderHosted` and `RenderProxy` unit tests serve as regression tests for the Scriban templates (which already correctly handle `stub_status` when `metrics_enabled` is true).

No unit test changes are needed for the config renderer itself, but you should add a unit test that verifies the **inline rendering** of `stub_status` in `RenderNginxVirtualConfig()` when metrics are enabled — this will be the first unit test for the virtual inline renderer:

```csharp
[Fact]
public void RenderNginxVirtual_WithMetricsEnabled_ContainsStubStatusBlock()
{
    // This is a private method, so test via the reconciler's public API
    // or extract RenderNginxVirtualConfig to the INginxConfigRenderer interface.
}
```

Consider extracting `RenderNginxVirtualConfig` to the `INginxConfigRenderer` interface as a new `RenderVirtual` method, so it can be unit-tested like `RenderHosted` and `RenderProxy`. This would be a good code-organization improvement alongside the feature work.

---

## Files Modified

| File | Change |
|---|---|
| `MavenOperator/Reconcilers/VirtualRepositoryReconciler.cs` | Main implementation: pod spec, config, service ports, cleanup |
| `MavenOperator.Tests.Integration/Virtual/VirtualRepositoryReconcilerTests.cs` | Update container count assertion; add metrics tests |
| `MavenOperator.Tests.Integration/Virtual/VirtualMetricsSidecarIntegrationTests.cs` | **New file** — full metrics sidecar test suite for Virtual |
| `MavenOperator.Tests.E2E/Infrastructure/VirtualMetricsE2EFixture.cs` | **New file** — E2E fixture with metrics-enabled Virtual repo |
| `MavenOperator.Tests.E2E/Virtual/VirtualMetricsObservabilityE2ETests.cs` | **New file** — E2E observability tests for Virtual |
| `MavenOperator.Tests.E2E/Infrastructure/MetricsE2EFactAttribute.cs` | **New file** — `VirtualMetricsE2EFact` attribute |

---

## Implementation Order

1. **`VirtualRepositoryReconciler.BuildVirtualPodSpec()`** — add metrics sidecar containers, volumes, and log mount (the core change)
2. **`VirtualRepositoryReconciler.ReconcileAsync()`** — add mtail ConfigMap creation, update service ports, update cleanup
3. **Update existing integration test** `Reconcile_CreatesSingleMergedDeployment_WithNginxAndProxySidecar` to also verify 4-container pod when metrics enabled
4. **Add integration tests** (both updated existing + new test file)
5. **Add E2E fixture + tests**
6. **Verify unit tests pass**

---

## Risks & Considerations

1. **Image pull latency on first run** — Virtual pods will now need to pull 2 additional images (nginx-exporter + mtail) when metrics is enabled. The existing 300s timeout in `PodReadinessHelper` already accounts for this.

2. **Memory footprint** — With metrics enabled, a Virtual pod uses roughly:
   - `nginx`: 512Mi limit
   - `proxy`: 256Mi limit
   - `nginx-exporter`: 32Mi limit
   - `mtail`: 64Mi limit
   - **Total**: ~864Mi (vs. ~768Mi without metrics)

3. **The `proxy` sidecar's `/health` port (8080)** — must remain accessible for operator upload-status polling. It is **not** affected by these changes; the metrics ports (9113, 3903) are separate and exposed on the Service alongside `http:80`.

4. **ConfigMap hash** — the config hashes stored as pod template annotations (`maven.operator.io/nginx-hash`, `maven.operator.io/proxy-config-hash`) only cover the NGINX config and proxy-config ConfigMap. When metrics changes (enabled → disabled), the sidecar containers in the pod template change, which triggers a rolling restart via the hash annotation change. This is the existing behavior and works correctly.

5. **PodMonitor** — When `spec.Metrics.PodMonitorEnabled` is true, the operator also creates a PodMonitor resource. The Virtual repo already calls `EnsurePodMonitorAsync` (which is a no-op when the CRD isn't installed). No change needed for PodMonitor support — it already works.
