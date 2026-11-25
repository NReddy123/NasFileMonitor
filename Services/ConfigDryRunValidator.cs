using Microsoft.Extensions.Options;
using NasFileMonitor.Configuration;

namespace NasFileMonitor.Services;

public sealed class ConfigDryRunValidator
{
    private readonly IOptions<MonitorConfig> _options;

    public ConfigDryRunValidator(IOptions<MonitorConfig> options)
    {
        _options = options;
    }

    public Task<int> RunAsync(CancellationToken ct)
    {
        try
        {
            var config = _options.Value;
            Console.WriteLine($"Schema validation passed for {config.Monitors.Count} monitor rule(s).");

            var failed = false;
            foreach (var rule in config.Monitors.Where(r => r.Enabled))
            {
                var sourceOk = Directory.Exists(rule.SourcePath);
                Console.WriteLine($"  [{(sourceOk ? "OK" : "FAIL")}] {rule.RuleId} -> SourcePath: {rule.SourcePath}");
                if (!sourceOk) failed = true;

                if (rule.FolderCheck is not null && !rule.FolderCheck.TargetPath.Contains('{'))
                {
                    var targetOk = Directory.Exists(rule.FolderCheck.TargetPath);
                    Console.WriteLine($"  [{(targetOk ? "OK" : "FAIL")}] {rule.RuleId} -> TargetPath: {rule.FolderCheck.TargetPath}");
                    if (!targetOk) failed = true;
                }
            }

            return Task.FromResult(failed ? 1 : 0);
        }
        catch (OptionsValidationException ex)
        {
            Console.Error.WriteLine("Configuration validation failed:");
            foreach (var failure in ex.Failures)
                Console.Error.WriteLine($"  - {failure}");
            return Task.FromResult(1);
        }
    }
}
