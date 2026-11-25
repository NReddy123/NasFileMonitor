using Microsoft.Extensions.Options;
using NasFileMonitor.Configuration;
using NasFileMonitor.Matching;
using NasFileMonitor.Persistence;
using NasFileMonitor.Services;
using NasFileMonitor.Verification;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("monitors.json", optional: false, reloadOnChange: true);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "NasFileMonitorService";
});

builder.Services
    .AddOptions<MonitorConfig>()
    .Bind(builder.Configuration)
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<MonitorConfig>, MonitorConfigValidator>();
builder.Services.AddSingleton<ITargetFileMatcher, RegexTargetMatcher>();
builder.Services.AddSingleton<INamedQueryRegistry, NamedQueryRegistry>();
builder.Services.AddSingleton<IStateLedgerRepository, SqlStateLedgerRepository>();

builder.Services.AddSingleton<FolderToFolderVerifier>();
builder.Services.AddSingleton<FolderToDbVerifier>();
builder.Services.AddSingleton<HybridVerifier>();
builder.Services.AddSingleton<IFileVerifierResolver, FileVerifierResolver>();
builder.Services.AddSingleton<ConfigDryRunValidator>();

if (args.Contains("--validate-config", StringComparer.OrdinalIgnoreCase))
{
    using var host = builder.Build();
    var dryRun = host.Services.GetRequiredService<ConfigDryRunValidator>();
    var exitCode = await dryRun.RunAsync(CancellationToken.None);
    Environment.Exit(exitCode);
}

builder.Services.AddHostedService<NasMonitorWorker>();

var app = builder.Build();
await app.RunAsync();
