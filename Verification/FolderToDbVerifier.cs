using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using NasFileMonitor.Configuration;
using NasFileMonitor.Models;

namespace NasFileMonitor.Verification;

public sealed class FolderToDbVerifier : IFileVerifier
{
    private readonly IConfiguration _configuration;
    private readonly INamedQueryRegistry _queryRegistry;

    public FolderToDbVerifier(IConfiguration configuration, INamedQueryRegistry queryRegistry)
    {
        _configuration = configuration;
        _queryRegistry = queryRegistry;
    }

    public async Task<IReadOnlyDictionary<long, VerificationResult>> VerifyBatchAsync(
        MonitorRule rule,
        IReadOnlyList<TrackedFileRecord> pendingFiles,
        IReadOnlyList<NasFileSnapshot>? targetSnapshot,
        IReadOnlyList<NasFileSnapshot>? errorSnapshot,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<long, VerificationResult>(pendingFiles.Count);
        if (pendingFiles.Count == 0)
            return results;

        var dbConfig = rule.DbCheck!;
        var connString = _configuration.GetConnectionString(dbConfig.ConnectionStringName)
            ?? throw new InvalidOperationException($"Missing connection string '{dbConfig.ConnectionStringName}'.");
        var sql = _queryRegistry.GetSql(dbConfig.QueryKey);

        var successSet = new HashSet<string>(dbConfig.SuccessValues, StringComparer.OrdinalIgnoreCase);
        var failureSet = new HashSet<string>(dbConfig.FailureValues, StringComparer.OrdinalIgnoreCase);

        await using var conn = new SqlConnection(connString);
        await conn.OpenAsync(cancellationToken);

        foreach (var chunk in pendingFiles.Chunk(Math.Max(1, dbConfig.BatchSize)))
        {
            var keyLookup = chunk
                .GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var keysJson = JsonSerializer.Serialize(keyLookup.Keys);
            var command = new CommandDefinition(sql, new { FileKeysJson = keysJson }, cancellationToken: cancellationToken);
            var rows = await conn.QueryAsync<DbStatusRow>(command);

            var statusByKey = rows
                .GroupBy(r => r.FileKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Status, StringComparer.OrdinalIgnoreCase);

            foreach (var file in chunk)
            {
                if (!statusByKey.TryGetValue(file.FileName, out var status) || string.IsNullOrWhiteSpace(status))
                {
                    results[file.TrackedFileId] = new VerificationResult(file.TrackedFileId, VerificationOutcome.Pending);
                }
                else if (failureSet.Contains(status))
                {
                    results[file.TrackedFileId] = new VerificationResult(file.TrackedFileId, VerificationOutcome.Failed, DbStatus: status);
                }
                else if (successSet.Contains(status))
                {
                    results[file.TrackedFileId] = new VerificationResult(file.TrackedFileId, VerificationOutcome.Succeeded, DbStatus: status);
                }
                else
                {
                    results[file.TrackedFileId] = new VerificationResult(file.TrackedFileId, VerificationOutcome.Pending, DbStatus: status);
                }
            }
        }

        return results;
    }

    private sealed record DbStatusRow(string FileKey, string Status);
}
