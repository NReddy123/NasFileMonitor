using NasFileMonitor.Models;

namespace NasFileMonitor.Persistence;

public interface IStateLedgerRepository
{
    Task<bool> TryAcquireOrRenewLeaseAsync(string leaseName, string ownerMachine, int leaseDurationSeconds, CancellationToken ct);
    Task<bool> HasRuleBaselineAsync(string ruleId, CancellationToken ct);
    Task<IReadOnlyList<TrackedFileRecord>> GetActiveFilesAsync(string ruleId, CancellationToken ct);
    Task<HashSet<string>> GetKnownIdentityKeysAsync(string ruleId, CancellationToken ct);
    Task<HashSet<string>> GetKnownTargetFilesAsync(string ruleId, CancellationToken ct);
    Task InsertTrackedFileAsync(TrackedFileRecord record, CancellationToken ct);
    Task UpdateTrackedFileAsync(TrackedFileRecord record, CancellationToken ct);
    Task UpsertRuleHeartbeatAsync(string ruleId, DateTime startedUtc, DateTime completedUtc, string health, int pendingCount, CancellationToken ct);
}
