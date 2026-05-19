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
builder.Services
    .AddKubernetesOperator()
    .AddController<MavenRepositoryController, MavenRepositoryV1Alpha1>()
    .AddController<CredentialSecretController, V1Secret>()
    .AddController<MavenRepositoryImportController, MavenRepositoryImportV1Alpha1>();

// ── Phase 1 services ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<IHtpasswdService, HtpasswdService>();
builder.Services.AddSingleton<IRoleBasedHtpasswdService, RoleBasedHtpasswdService>();
builder.Services.AddSingleton<IAuthProxyConfigRenderer, AuthProxyConfigRenderer>();
builder.Services.AddSingleton<INginxConfigRenderer, NginxConfigRenderer>();
builder.Services.AddSingleton<IKubernetesResourceManager, KubernetesResourceManager>();

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


