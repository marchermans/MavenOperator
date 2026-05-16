using KubeOps.Operator;
using MavenOperator.Controllers;
using MavenOperator.Entities;
using MavenOperator.Reconcilers;
using MavenOperator.Services;

var builder = Host.CreateApplicationBuilder(args);

// ── Logging ──────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();


// ── KubeOps operator ─────────────────────────────────────────────────────────
builder.Services
    .AddKubernetesOperator()
    .AddController<MavenRepositoryController, MavenRepositoryV1Alpha1>();

// ── Phase 1 services ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<IHtpasswdService, HtpasswdService>();
builder.Services.AddSingleton<INginxConfigRenderer, NginxConfigRenderer>();
builder.Services.AddSingleton<IKubernetesResourceManager, KubernetesResourceManager>();

// ── Type-specific reconcilers ─────────────────────────────────────────────────
builder.Services.AddSingleton<IHostedRepositoryReconciler, HostedRepositoryReconciler>();
builder.Services.AddSingleton<IProxyRepositoryReconciler,  ProxyRepositoryReconciler>();
builder.Services.AddSingleton<IVirtualRepositoryReconciler, VirtualRepositoryReconciler>();

using var host = builder.Build();
await host.RunAsync();


