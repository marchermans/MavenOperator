using KubeOps.Operator;
using MavenOperator.Controllers;
using MavenOperator.Entities;
using MavenOperator.Reconcilers;

var builder = Host.CreateApplicationBuilder(args);

// ── Logging ──────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// ── KubeOps operator ─────────────────────────────────────────────────────────
builder.Services
    .AddKubernetesOperator()
    .AddController<MavenRepositoryController, MavenRepositoryV1Alpha1>();

// ── Type-specific reconcilers (injected into the controller) ─────────────────
builder.Services.AddSingleton<IHostedRepositoryReconciler, HostedRepositoryReconciler>();
builder.Services.AddSingleton<IProxyRepositoryReconciler, ProxyRepositoryReconciler>();
builder.Services.AddSingleton<IVirtualRepositoryReconciler, VirtualRepositoryReconciler>();

using var host = builder.Build();
await host.RunAsync();
