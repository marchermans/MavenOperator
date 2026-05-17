using k8s;
using MavenOperator.ImportJob.Models;
using MavenOperator.ImportJob.Services;
using MavenOperator.ImportJob.Sinks;
using MavenOperator.ImportJob.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using System.Text.Json;

// ── Bootstrap ────────────────────────────────────────────────────────────────

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddHttpClient("reposilite").AddTransientHttpErrorPolicy(p =>
    p.WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(Math.Pow(2, i))));
services.AddHttpClient("jfrog").AddTransientHttpErrorPolicy(p =>
    p.WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(Math.Pow(2, i))));
services.AddHttpClient("http-sink").AddTransientHttpErrorPolicy(p =>
    p.WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(Math.Pow(2, i))));

var provider   = services.BuildServiceProvider();
var logFactory = provider.GetRequiredService<ILoggerFactory>();
var logger     = logFactory.CreateLogger("ImportJob");

// ── Read environment configuration ───────────────────────────────────────────

var importModeRaw    = Env("IMPORT_MODE",          required: true)!;
var transferModeRaw  = Env("IMPORT_TRANSFER_MODE", required: true)!;
var optionsJson      = Env("IMPORT_OPTIONS_JSON",  required: false) ?? "{}";
var filtersJson      = Env("IMPORT_FILTERS_JSON",  required: false) ?? "{}";
var targetRepository = Env("TARGET_REPOSITORY",    required: true)!;
var targetNamespace  = Env("TARGET_NAMESPACE",     required: true)!;
var importCrName     = Env("IMPORT_CR_NAME",       required: true)!;

var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var options  = JsonSerializer.Deserialize<ImportOptions>(optionsJson,  jsonOpts) ?? new ImportOptions();
var filters  = JsonSerializer.Deserialize<ImportFilters>(filtersJson, jsonOpts)  ?? new ImportFilters();

var importMode = importModeRaw switch
{
    "api-reposilite" => ImportMode.ApiReposilite,
    "api-jfrog"      => ImportMode.ApiJFrog,
    "pvc-snapshot"   => ImportMode.PvcSnapshot,
    "pvc-live"       => ImportMode.PvcLive,
    _                => ImportMode.ApiReposilite,
};

var transferMode = transferModeRaw == "direct-write"
    ? TransferMode.DirectWrite
    : TransferMode.Http;

logger.LogInformation(
    "Import Job starting: mode={Mode} transfer={Transfer} dryRun={DryRun} parallelism={P}",
    importMode, transferMode, options.DryRun, options.Parallelism);

// ── Kubernetes client (optional — for progress reporting) ────────────────────

IKubernetes? kubernetes = null;
try
{
    var k8sCfg = KubernetesClientConfiguration.InClusterConfig();
    kubernetes = new Kubernetes(k8sCfg);
    logger.LogInformation("In-cluster Kubernetes config loaded — progress reporting enabled");
}
catch
{
    logger.LogWarning("Not running in-cluster — progress reporting disabled");
}

var crawler  = new ArtifactCrawler(logFactory.CreateLogger<ArtifactCrawler>());
var reporter = new ProgressReporter(kubernetes, targetNamespace, importCrName,
    $"{importCrName}-import-job", logFactory.CreateLogger<ProgressReporter>());

using var cts        = new CancellationTokenSource();
var httpFactory      = provider.GetRequiredService<IHttpClientFactory>();

// ── Build source ─────────────────────────────────────────────────────────────

IRepositorySource source;
Func<ArtifactDescriptor, CancellationToken, Task<Stream?>>? openStream = null;

switch (importMode)
{
    case ImportMode.ApiReposilite:
    {
        var sourceUrl  = Env("SOURCE_URL",  required: true)!;
        var sourceRepo = Env("SOURCE_REPO", required: true)!;
        var creds      = LoadCredentials();
        var rs         = new ReposiliteApiSource(
            httpFactory, sourceUrl, sourceRepo,
            creds?.Username, creds?.Password,
            logFactory.CreateLogger<ReposiliteApiSource>());
        source     = rs;
        openStream = (a, ct) => rs.OpenStreamAsync(a, ct);
        break;
    }
    case ImportMode.ApiJFrog:
    {
        var sourceUrl  = Env("SOURCE_URL",  required: true)!;
        var sourceRepo = Env("SOURCE_REPO", required: true)!;
        var creds      = LoadCredentials();
        var jf         = new JFrogCloudApiSource(
            httpFactory, sourceUrl, sourceRepo,
            creds?.Token, creds?.Username, creds?.Password,
            includeSignatures: false,
            logFactory.CreateLogger<JFrogCloudApiSource>());
        source     = jf;
        openStream = (a, ct) => jf.OpenStreamAsync(a, ct);
        break;
    }
    case ImportMode.PvcSnapshot:
    case ImportMode.PvcLive:
    {
        var mountPath        = Env("SOURCE_PVC_MOUNT",       required: true)!;
        var reposiliteLayout = Env("SOURCE_REPOSILITE_LAYOUT", required: false) != "false";
        source = new PvcSnapshotSource(
            mountPath, reposiliteLayout, targetRepository,
            logFactory.CreateLogger<PvcSnapshotSource>());
        break;
    }
    default:
        throw new InvalidOperationException($"Unknown import mode: {importMode}");
}

// ── Build sink ───────────────────────────────────────────────────────────────

IRepositorySink sink = transferMode switch
{
    TransferMode.DirectWrite => new DirectPvcSink(
        Env("TARGET_PVC_MOUNT", required: true)!,
        options.DryRun,
        logFactory.CreateLogger<DirectPvcSink>()),
    TransferMode.Http => new HttpSink(
        httpFactory,
        Env("TARGET_HTTP_URL", required: true)!,
        username: null, password: null,
        options.DryRun,
        logFactory.CreateLogger<HttpSink>()),
    _ => throw new InvalidOperationException($"Unknown transfer mode: {transferMode}"),
};

// ── Run ──────────────────────────────────────────────────────────────────────

try
{
    var result = await crawler.RunAsync(
        source, sink, openStream, options, filters, reporter, cts.Token);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        artifactsDiscovered = result.ArtifactsDiscovered,
        artifactsCopied     = result.ArtifactsCopied,
        artifactsFailed     = result.ArtifactsFailed,
        bytesTransferred    = result.BytesTransferred,
        success             = result.IsSuccess,
    }));

    Environment.Exit(result.ArtifactsFailed > 0 ? (result.IsPartial ? 2 : 1) : 0);
}
catch (Exception ex)
{
    logger.LogError(ex, "Import Job failed with unhandled exception");
    Environment.Exit(1);
}

// ── Helpers ──────────────────────────────────────────────────────────────────

static string? Env(string name, bool required)
{
    var val = Environment.GetEnvironmentVariable(name);
    if (required && string.IsNullOrEmpty(val))
        throw new InvalidOperationException($"Required environment variable '{name}' is not set");
    return val;
}

static CredentialsFile? LoadCredentials()
{
    var credsFile = Environment.GetEnvironmentVariable("CREDENTIALS_FILE");
    if (string.IsNullOrEmpty(credsFile) || !File.Exists(credsFile))
        return null;
    var json = File.ReadAllText(credsFile);
    return JsonSerializer.Deserialize<CredentialsFile>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}

internal sealed class CredentialsFile
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Token    { get; set; }
}
