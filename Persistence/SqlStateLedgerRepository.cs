using Dapper;
using Microsoft.Data.SqlClient;
using NasFileMonitor.Models;

namespace NasFileMonitor.Persistence;

public sealed class SqlStateLedgerRepository : IStateLedgerRepository
{
    private readonly string _connectionString;

    public SqlStateLedgerRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("StateLedgerDb")
            ?? throw new InvalidOperationException("Missing connection string 'StateLedgerDb'.");
    }

    public async Task<bool> TryAcquireOrRenewLeaseAsync(
        string leaseName,
        string ownerMachine,
        int leaseDurationSeconds,
        CancellationToken ct)
    {
        const string sql = """
            MERGE dbo.ServiceLeases WITH (HOLDLOCK) AS target
            USING (SELECT @LeaseName AS LeaseName) AS src
                ON target.LeaseName = src.LeaseName
            WHEN MATCHED AND (target.OwnerMachineName = @OwnerMachine OR target.ExpiresUtc <= SYSUTCDATETIME()) THEN
                UPDATE SET
                    OwnerMachineName = @OwnerMachine,
                    AcquiredUtc = CASE WHEN target.OwnerMachineName = @OwnerMachine THEN target.AcquiredUtc ELSE SYSUTCDATETIME() END,
                    ExpiresUtc = DATEADD(SECOND, @LeaseDurationSeconds, SYSUTCDATETIME())
            WHEN NOT MATCHED THEN
                INSERT (LeaseName, OwnerMachineName, AcquiredUtc, ExpiresUtc)
                VALUES (@LeaseName, @OwnerMachine, SYSUTCDATETIME(), DATEADD(SECOND, @LeaseDurationSeconds, SYSUTCDATETIME()));

            SELECT CASE WHEN OwnerMachineName = @OwnerMachine AND ExpiresUtc > SYSUTCDATETIME() THEN 1 ELSE 0 END
            FROM dbo.ServiceLeases
            WHERE LeaseName = @LeaseName;
            """;

        await using var conn = new SqlConnection(_connectionString);
        var acquired = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { LeaseName = leaseName, OwnerMachine = ownerMachine, LeaseDurationSeconds = leaseDurationSeconds }, cancellationToken: ct));

        return acquired == 1;
    }

    public async Task<bool> HasRuleBaselineAsync(string ruleId, CancellationToken ct)
    {
        const string sql = "SELECT TOP 1 1 FROM dbo.RuleHeartbeats WHERE RuleId = @RuleId;";
        await using var conn = new SqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql, new { RuleId = ruleId }, cancellationToken: ct)) == 1;
    }

    public async Task<IReadOnlyList<TrackedFileRecord>> GetActiveFilesAsync(string ruleId, CancellationToken ct)
    {
        const string sql = """
            SELECT TrackedFileId, RuleId, RelativePath, FileName, FileSizeBytes, LastWriteTimeUtc,
                   AttemptNumber, StabilityHits, State, FirstSeenUtc, LastSeenUtc,
                   VerifiedAtUtc, FolderVerifiedAtUtc, MatchedTargetFile, DbStatus, AlertedAtUtc
            FROM dbo.TrackedFiles
            WHERE RuleId = @RuleId
              AND State NOT IN ('BaselineIgnored', 'Succeeded', 'SucceededFastPath', 'Failed');
            """;

        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<TrackedFileRecord>(new CommandDefinition(sql, new { RuleId = ruleId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<HashSet<string>> GetKnownIdentityKeysAsync(string ruleId, CancellationToken ct)
    {
        const string sql = """
            SELECT RelativePath, FileSizeBytes, LastWriteTimeUtc
            FROM dbo.TrackedFiles
            WHERE RuleId = @RuleId;
            """;

        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<(string RelativePath, long FileSizeBytes, DateTime LastWriteTimeUtc)>(
            new CommandDefinition(sql, new { RuleId = ruleId }, cancellationToken: ct));

        return rows
            .Select(r => $"{ruleId}|{r.RelativePath.ToLowerInvariant()}|{r.FileSizeBytes}|{r.LastWriteTimeUtc.Ticks}")
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<HashSet<string>> GetKnownTargetFilesAsync(string ruleId, CancellationToken ct)
    {
        const string sql = """
            SELECT MatchedTargetFile
            FROM dbo.TrackedFiles
            WHERE RuleId = @RuleId AND MatchedTargetFile IS NOT NULL;
            """;

        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(sql, new { RuleId = ruleId }, cancellationToken: ct));
        return rows.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task InsertTrackedFileAsync(TrackedFileRecord record, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO dbo.TrackedFiles (
                RuleId, RelativePath, FileName, FileSizeBytes, LastWriteTimeUtc,
                AttemptNumber, StabilityHits, State, FirstSeenUtc, LastSeenUtc,
                VerifiedAtUtc, FolderVerifiedAtUtc, MatchedTargetFile, DbStatus, AlertedAtUtc)
            OUTPUT INSERTED.TrackedFileId
            VALUES (
                @RuleId, @RelativePath, @FileName, @FileSizeBytes, @LastWriteTimeUtc,
                @AttemptNumber, @StabilityHits, @StateString, @FirstSeenUtc, @LastSeenUtc,
                @VerifiedAtUtc, @FolderVerifiedAtUtc, @MatchedTargetFile, @DbStatus, @AlertedAtUtc);
            """;

        await using var conn = new SqlConnection(_connectionString);
        record.TrackedFileId = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, new
            {
                record.RuleId,
                record.RelativePath,
                record.FileName,
                record.FileSizeBytes,
                record.LastWriteTimeUtc,
                record.AttemptNumber,
                record.StabilityHits,
                StateString = record.State.ToString(),
                record.FirstSeenUtc,
                record.LastSeenUtc,
                record.VerifiedAtUtc,
                record.FolderVerifiedAtUtc,
                record.MatchedTargetFile,
                record.DbStatus,
                record.AlertedAtUtc
            }, cancellationToken: ct));
    }

    public async Task UpdateTrackedFileAsync(TrackedFileRecord record, CancellationToken ct)
    {
        const string sql = """
            UPDATE dbo.TrackedFiles
            SET StabilityHits = @StabilityHits,
                State = @StateString,
                LastSeenUtc = @LastSeenUtc,
                VerifiedAtUtc = @VerifiedAtUtc,
                FolderVerifiedAtUtc = @FolderVerifiedAtUtc,
                MatchedTargetFile = @MatchedTargetFile,
                DbStatus = @DbStatus,
                AlertedAtUtc = @AlertedAtUtc
            WHERE TrackedFileId = @TrackedFileId;
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            record.TrackedFileId,
            record.StabilityHits,
            StateString = record.State.ToString(),
            record.LastSeenUtc,
            record.VerifiedAtUtc,
            record.FolderVerifiedAtUtc,
            record.MatchedTargetFile,
            record.DbStatus,
            record.AlertedAtUtc
        }, cancellationToken: ct));
    }

    public async Task UpsertRuleHeartbeatAsync(
        string ruleId,
        DateTime startedUtc,
        DateTime completedUtc,
        string health,
        int pendingCount,
        CancellationToken ct)
    {
        const string sql = """
            MERGE dbo.RuleHeartbeats AS target
            USING (SELECT @RuleId AS RuleId) AS src ON target.RuleId = src.RuleId
            WHEN MATCHED THEN
                UPDATE SET
                    LastTickStartedUtc = @StartedUtc,
                    LastTickCompletedUtc = @CompletedUtc,
                    RuleHealth = @Health,
                    PendingCount = @PendingCount
            WHEN NOT MATCHED THEN
                INSERT (RuleId, LastTickStartedUtc, LastTickCompletedUtc, RuleHealth, PendingCount)
                VALUES (@RuleId, @StartedUtc, @CompletedUtc, @Health, @PendingCount);
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            RuleId = ruleId,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            Health = health,
            PendingCount = pendingCount
        }, cancellationToken: ct));
    }
}
