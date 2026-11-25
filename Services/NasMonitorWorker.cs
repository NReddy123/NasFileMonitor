using System.IO.Enumeration;
using Microsoft.Extensions.Options;
using NasFileMonitor.Configuration;
using NasFileMonitor.Models;
using NasFileMonitor.Persistence;
using NasFileMonitor.Verification;

namespace NasFileMonitor.Services;

public sealed class NasMonitorWorker : BackgroundService
{
    private readonly IOptionsMonitor<MonitorConfig> _configMonitor;
    private readonly IStateLedgerRepository _ledger;
    private readonly IFileVerifierResolver _verifierResolver;
    private readonly ILogger<NasMonitorWorker> _logger;

    public NasMonitorWorker(
        IOptionsMonitor<MonitorConfig> configMonitor,
        IStateLedgerRepository ledger,
        IFileVerifierResolver verifierResolver,
        ILogger<NasMonitorWorker> logger)
    {
        _configMonitor = configMonitor;
        _ledger = ledger;
        _verifierResolver = verifierResolver;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_configMonitor.CurrentValue.PollingDefaults.ScanIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during polling tick.");
            }

            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }

    private async Task RunTickAsync(CancellationToken stoppingToken)
    {
        var config = _configMonitor.CurrentValue;
        var hasLease = await _ledger.TryAcquireOrRenewLeaseAsync(
            "NasMonitorPrimary",
            Environment.MachineName,
            config.PollingDefaults.LeaseDurationSeconds,
            stoppingToken);

        if (!hasLease)
        {
            _logger.LogDebug("Standby mode on {Machine}: another node holds the active lease.", Environment.MachineName);
            return;
        }

        var enabledRules = config.Monitors.Where(r => r.Enabled).ToList();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, config.PollingDefaults.MaxParallelRules),
            CancellationToken = stoppingToken
        };

        await Parallel.ForEachAsync(enabledRules, parallelOptions, async (rule, ct) =>
        {
            using var ruleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ruleCts.CancelAfter(TimeSpan.FromSeconds(config.PollingDefaults.PerRuleTimeoutSeconds));

            await ProcessRuleAsync(rule, config.PollingDefaults, ruleCts.Token);
        });
    }

    private async Task ProcessRuleAsync(MonitorRule rule, PollingDefaults defaults, CancellationToken ct)
    {
        var startedUtc = DateTime.UtcNow;
        var health = "Healthy";
        var pendingCount = 0;

        try
        {
            var isInitialRun = !await _ledger.HasRuleBaselineAsync(rule.RuleId, ct);
            var sourceFiles = EnumerateDirectoryFast(rule.SourcePath, rule.FilePattern, rule.IncludeSubdirectories);
            var activeFiles = (await _ledger.GetActiveFilesAsync(rule.RuleId, ct)).ToList();
            var knownKeys = await _ledger.GetKnownIdentityKeysAsync(rule.RuleId, ct);

            var currentSourceMap = sourceFiles.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

            // 1. Discover new or modified source files
            foreach (var snapshot in sourceFiles)
            {
                var probe = new TrackedFileRecord
                {
                    RuleId = rule.RuleId,
                    RelativePath = snapshot.RelativePath,
                    FileName = snapshot.FileName,
                    FileSizeBytes = snapshot.FileSizeBytes,
                    LastWriteTimeUtc = snapshot.LastWriteTimeUtc,
                    FirstSeenUtc = startedUtc,
                    LastSeenUtc = startedUtc
                };

                if (knownKeys.Contains(probe.BuildIdentityKey()))
                    continue;

                if (isInitialRun && rule.ExistingFilesPolicy == ExistingFilesPolicy.BaselineOnly)
                {
                    probe.State = FileState.BaselineIgnored;
                }
                else if (isInitialRun && rule.ExistingFilesPolicy == ExistingFilesPolicy.BaselineAndVerify)
                {
                    probe.State = FileState.PendingVerification;
                }
                else
                {
                    probe.State = defaults.StabilityChecksRequired <= 1
                        ? FileState.PendingVerification
                        : FileState.Detected;
                }

                await _ledger.InsertTrackedFileAsync(probe, ct);
                knownKeys.Add(probe.BuildIdentityKey());

                if (!probe.State.IsTerminal())
                    activeFiles.Add(probe);
            }

            // 2. Advance stability gate & handle Moved fast-promotion
            foreach (var active in activeFiles.Where(f => f.State == FileState.Detected))
            {
                if (currentSourceMap.TryGetValue(active.RelativePath, out var current))
                {
                    if (current.FileSizeBytes == active.FileSizeBytes && current.LastWriteTimeUtc == active.LastWriteTimeUtc)
                    {
                        active.StabilityHits++;
                        active.LastSeenUtc = startedUtc;
                        if (active.StabilityHits >= defaults.StabilityChecksRequired)
                            active.State = FileState.PendingVerification;
                    }
                    else
                    {
                        active.FileSizeBytes = current.FileSizeBytes;
                        active.LastWriteTimeUtc = current.LastWriteTimeUtc;
                        active.StabilityHits = 1;
                        active.LastSeenUtc = startedUtc;
                    }
                }
                else if (rule.FolderCheck?.SourceDisposition == SourceDisposition.Moved)
                {
                    // File left SourcePath before second stability tick: promote immediately to verify Target/Error
                    active.State = FileState.PendingVerification;
                }

                await _ledger.UpdateTrackedFileAsync(active, ct);
            }

            // 3. Verify all PendingVerification / SlaBreached / PartialMismatch files
            var toVerify = activeFiles
                .Where(f => f.State is FileState.PendingVerification or FileState.SlaBreached or FileState.PartialMismatch)
                .ToList();

            var needsTargetScan = rule.FolderCheck is not null
                && (toVerify.Count > 0 || rule.FolderCheck.TrackFastPathMoves);

            IReadOnlyList<NasFileSnapshot>? targetSnapshot = null;
            IReadOnlyList<NasFileSnapshot>? errorSnapshot = null;

            if (needsTargetScan && rule.FolderCheck is not null)
            {
                var resolvedTarget = ExpandDateTokens(rule.FolderCheck.TargetPath, startedUtc);
                targetSnapshot = Directory.Exists(resolvedTarget)
                    ? EnumerateDirectoryFast(resolvedTarget, "*", includeSubdirectories: false)
                    : Array.Empty<NasFileSnapshot>();

                if (!string.IsNullOrWhiteSpace(rule.FolderCheck.ErrorPath) && Directory.Exists(rule.FolderCheck.ErrorPath))
                    errorSnapshot = EnumerateDirectoryFast(rule.FolderCheck.ErrorPath, "*", includeSubdirectories: false);
            }

            var verifier = _verifierResolver.Resolve(rule.VerificationMode);
            var outcomes = await verifier.VerifyBatchAsync(rule, toVerify, targetSnapshot, errorSnapshot, ct);

            var slaBreaches = new List<TrackedFileRecord>();

            foreach (var file in toVerify)
            {
                var res = outcomes[file.TrackedFileId];
                file.DbStatus = res.DbStatus ?? file.DbStatus;
                file.MatchedTargetFile = res.MatchedTargetFile ?? file.MatchedTargetFile;
                if (res.FolderVerified && !file.FolderVerifiedAtUtc.HasValue)
                    file.FolderVerifiedAtUtc = startedUtc;

                var ageMinutes = (startedUtc - file.FirstSeenUtc).TotalMinutes;
                var stillInSource = currentSourceMap.ContainsKey(file.RelativePath);

                if (res.Outcome == VerificationOutcome.Succeeded)
                {
                    file.State = FileState.Succeeded;
                    file.VerifiedAtUtc = startedUtc;
                }
                else if (res.Outcome == VerificationOutcome.Failed)
                {
                    file.State = FileState.Failed;
                    file.VerifiedAtUtc = startedUtc;
                }
                else if (ageMinutes >= rule.SlaMinutes)
                {
                    if (!stillInSource && rule.FolderCheck?.SourceDisposition == SourceDisposition.Moved)
                        file.State = FileState.VanishedUnverified;
                    else if (rule.VerificationMode == VerificationMode.Hybrid && (file.FolderVerifiedAtUtc.HasValue || !string.IsNullOrEmpty(file.DbStatus)))
                        file.State = FileState.PartialMismatch;
                    else
                        file.State = FileState.SlaBreached;

                    if (!file.AlertedAtUtc.HasValue)
                    {
                        file.AlertedAtUtc = startedUtc;
                        slaBreaches.Add(file);
                    }
                }

                await _ledger.UpdateTrackedFileAsync(file, ct);
            }

            // 4. Target-Side Reverse Discovery for fast-path moves between ticks
            if (!isInitialRun && rule.FolderCheck is { TrackFastPathMoves: true } && targetSnapshot is { Count: > 0 })
            {
                var knownTargets = await _ledger.GetKnownTargetFilesAsync(rule.RuleId, ct);
                foreach (var targetFile in targetSnapshot)
                {
                    if (knownTargets.Contains(targetFile.RelativePath))
                        continue;

                    if (targetFile.LastWriteTimeUtc < startedUtc.AddSeconds(-defaults.ScanIntervalSeconds * 2))
                        continue;

                    var fastRecord = new TrackedFileRecord
                    {
                        RuleId = rule.RuleId,
                        RelativePath = targetFile.RelativePath,
                        FileName = targetFile.FileName,
                        FileSizeBytes = targetFile.FileSizeBytes,
                        LastWriteTimeUtc = targetFile.LastWriteTimeUtc,
                        State = FileState.SucceededFastPath,
                        FirstSeenUtc = startedUtc,
                        LastSeenUtc = startedUtc,
                        VerifiedAtUtc = startedUtc,
                        FolderVerifiedAtUtc = startedUtc,
                        MatchedTargetFile = targetFile.RelativePath
                    };

                    await _ledger.InsertTrackedFileAsync(fastRecord, ct);
                    knownTargets.Add(targetFile.RelativePath);
                }
            }

            if (slaBreaches.Count > 0)
            {
                var oldest = slaBreaches.OrderBy(f => f.FirstSeenUtc).First();
                _logger.LogWarning(
                    "Rule {RuleId}: {Count} file(s) breached {Sla}m SLA. Oldest: {FileName} (State={State}, FirstSeen={FirstSeenUtc:O})",
                    rule.RuleId, slaBreaches.Count, rule.SlaMinutes, oldest.FileName, oldest.State, oldest.FirstSeenUtc);
            }

            pendingCount = toVerify.Count(f => !f.State.IsTerminal());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            health = "ShareUnavailable";
            _logger.LogError(ex, "Rule {RuleId} scan failed or timed out; SLA evaluation paused for this tick.", rule.RuleId);
        }
        finally
        {
            await _ledger.UpsertRuleHeartbeatAsync(rule.RuleId, startedUtc, DateTime.UtcNow, health, pendingCount, CancellationToken.None);
        }
    }

    private static List<NasFileSnapshot> EnumerateDirectoryFast(string rootPath, string pattern, bool includeSubdirectories)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubdirectories,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary
        };

        var enumerable = new FileSystemEnumerable<NasFileSnapshot>(
            rootPath,
            (ref FileSystemEntry entry) => new NasFileSnapshot(
                RelativePath: Path.GetRelativePath(rootPath, entry.ToFullPath()),
                FileName: entry.FileName.ToString(),
                FileSizeBytes: entry.Length,
                LastWriteTimeUtc: entry.LastWriteTimeUtc.UtcDateTime),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory && FileSystemName.MatchesSimpleExpression(pattern, entry.FileName, ignoreCase: true)
        };

        return enumerable.ToList();
    }

    private static string ExpandDateTokens(string pathTemplate, DateTime utcNow) => pathTemplate
        .Replace("{yyyy}", utcNow.ToString("yyyy"), StringComparison.Ordinal)
        .Replace("{MM}", utcNow.ToString("MM"), StringComparison.Ordinal)
        .Replace("{dd}", utcNow.ToString("dd"), StringComparison.Ordinal);
}
