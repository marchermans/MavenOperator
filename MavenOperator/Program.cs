using KubeOps.Abstractions.Builder;
using KubeOps.Operator;
using MavenOperator.Controllers;
using MavenOperator.Entities;
using MavenOperator.Reconcilers;
using MavenOperator.Services;
using k8s.Models;
using Prometheus;

var builder = Host.CreateApplicationBuilder(args);

// ── Logging ──────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();


// ── KubeOps operator ─────────────────────────────────────────────────────────
// ByResourceVersion (not the default ByGeneration): annotation-only updates must
// still trigger reconciliation — e.g. toggling maven.operator.io/externally-exposed
// on a live proxy repo does not bump metadata.generation and would otherwise be
// silently ignored by KubeOps' watch handler.
builder.Services
    .AddKubernetesOperator(o => o.WithReconcileStrategy(ReconcileStrategy.ByResourceVersion))
    .AddController<MavenRepositoryController, MavenRepositoryV1Alpha1>()
    .AddController<CredentialSecretController, V1Secret>()
    .AddController<MavenRepositoryImportController, MavenRepositoryImportV1Alpha1>();

// ── Phase 1 services ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<IHtpasswdService, HtpasswdService>();
builder.Services.AddSingleton<IRoleBasedHtpasswdService, RoleBasedHtpasswdService>();
builder.Services.AddSingleton<IAuthProxyConfigRenderer, AuthProxyConfigRenderer>();
builder.Services.AddSingleton<INginxConfigRenderer, NginxConfigRenderer>();
builder.Services.AddSingleton<IKubernetesResourceManager, KubernetesResourceManager>();

// ── Phase 8 Gateway API services ──────────────────────────────────────────────
builder.Services.AddSingleton<IGatewayApiService, GatewayApiService>();

// ── Phase 4 services ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<IKubernetesEventService, KubernetesEventService>();

// ── Phase 5 — Prometheus metrics ──────────────────────────────────────────────
builder.Services.AddSingleton<IOperatorMetrics, OperatorMetrics>();
// KubeOps uses a .NET Generic Host; expose /metrics via a standalone HTTP server on port 9090
builder.Services.AddMetricServer(options => options.Port = 9090);

// ── Type-specific reconcilers ─────────────────────────────────────────────────
builder.Services.AddSingleton<IHostedRepositoryReconciler, HostedRepositoryReconciler>();
builder.Services.AddSingleton<IProxyRepositoryReconciler,  ProxyRepositoryReconciler>();
builder.Services.AddSingleton<IVirtualRepositoryReconciler, VirtualRepositoryReconciler>();

// ── Phase 7 — Import & Migration services ─────────────────────────────────────
builder.Services.AddSingleton<IPvcAccessChecker, PvcAccessChecker>();
builder.Services.AddSingleton<IImportJobBuilder, ImportJobBuilder>();

using var host = builder.Build();
await host.RunAsync();


