using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NasFileMonitor.Verification;

namespace NasFileMonitor.Configuration;

public sealed class MonitorConfigValidator : IValidateOptions<MonitorConfig>
{
    private readonly INamedQueryRegistry _queryRegistry;

    public MonitorConfigValidator(INamedQueryRegistry queryRegistry)
    {
        _queryRegistry = queryRegistry;
    }

    public ValidateOptionsResult Validate(string? name, MonitorConfig options)
    {
        var errors = new List<string>();

        if (options.PollingDefaults.ScanIntervalSeconds < 5)
            errors.Add("PollingDefaults.ScanIntervalSeconds must be >= 5.");

        if (options.PollingDefaults.StabilityChecksRequired < 1)
            errors.Add("PollingDefaults.StabilityChecksRequired must be >= 1.");

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in options.Monitors)
        {
            if (string.IsNullOrWhiteSpace(rule.RuleId))
            {
                errors.Add("Every monitor rule requires a non-empty RuleId.");
                continue;
            }

            if (!seenIds.Add(rule.RuleId))
                errors.Add($"Duplicate RuleId '{rule.RuleId}'.");

            if (!rule.SourcePath.StartsWith(@"\", StringComparison.Ordinal))
                errors.Add($"Rule '{rule.RuleId}': SourcePath must be a UNC path (\\server\share), not a mapped drive.");

            if (rule.VerificationMode is VerificationMode.FolderToFolder or VerificationMode.Hybrid)
                ValidateFolderCheck(rule, errors);

            if (rule.VerificationMode is VerificationMode.FolderToDb or VerificationMode.Hybrid)
                ValidateDbCheck(rule, errors);
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateFolderCheck(MonitorRule rule, List<string> errors)
    {
        if (rule.FolderCheck is null)
        {
            errors.Add($"Rule '{rule.RuleId}': FolderCheck is required for {rule.VerificationMode}.");
            return;
        }

        if (!rule.FolderCheck.TargetPath.StartsWith(@"\", StringComparison.Ordinal))
            errors.Add($"Rule '{rule.RuleId}': FolderCheck.TargetPath must be a UNC path.");

        try
        {
            var probePattern = rule.FolderCheck.TargetFileNameRegex
                .Replace("{FileName}", "sample\.txt", StringComparison.Ordinal)
                .Replace("{BaseName}", "sample", StringComparison.Ordinal)
                .Replace("{Ext}", "\.txt", StringComparison.Ordinal);

            _ = new Regex(probePattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException ex)
        {
            errors.Add($"Rule '{rule.RuleId}': Invalid TargetFileNameRegex: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(rule.FolderCheck.SourceExtractRegex))
        {
            try
            {
                _ = new Regex(rule.FolderCheck.SourceExtractRegex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                errors.Add($"Rule '{rule.RuleId}': Invalid SourceExtractRegex: {ex.Message}");
            }
        }
    }

    private void ValidateDbCheck(MonitorRule rule, List<string> errors)
    {
        if (rule.DbCheck is null)
        {
            errors.Add($"Rule '{rule.RuleId}': DbCheck is required for {rule.VerificationMode}.");
            return;
        }

        if (!_queryRegistry.ContainsKey(rule.DbCheck.QueryKey))
            errors.Add($"Rule '{rule.RuleId}': QueryKey '{rule.DbCheck.QueryKey}' is not registered in NamedQueryRegistry.");

        if (rule.DbCheck.SuccessValues.Count == 0)
            errors.Add($"Rule '{rule.RuleId}': DbCheck.SuccessValues must contain at least one value.");
    }
}
