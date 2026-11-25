using NasFileMonitor.Configuration;
using NasFileMonitor.Models;

namespace NasFileMonitor.Verification;

public sealed class HybridVerifier : IFileVerifier
{
    private readonly FolderToFolderVerifier _folderVerifier;
    private readonly FolderToDbVerifier _dbVerifier;

    public HybridVerifier(FolderToFolderVerifier folderVerifier, FolderToDbVerifier dbVerifier)
    {
        _folderVerifier = folderVerifier;
        _dbVerifier = dbVerifier;
    }

    public async Task<IReadOnlyDictionary<long, VerificationResult>> VerifyBatchAsync(
        MonitorRule rule,
        IReadOnlyList<TrackedFileRecord> pendingFiles,
        IReadOnlyList<NasFileSnapshot>? targetSnapshot,
        IReadOnlyList<NasFileSnapshot>? errorSnapshot,
        CancellationToken cancellationToken)
    {
        var folderTask = _folderVerifier.VerifyBatchAsync(rule, pendingFiles, targetSnapshot, errorSnapshot, cancellationToken);
        var dbTask = _dbVerifier.VerifyBatchAsync(rule, pendingFiles, null, null, cancellationToken);

        await Task.WhenAll(folderTask, dbTask);

        var folderResults = folderTask.Result;
        var dbResults = dbTask.Result;
        var merged = new Dictionary<long, VerificationResult>(pendingFiles.Count);

        foreach (var file in pendingFiles)
        {
            var f = folderResults[file.TrackedFileId];
            var d = dbResults[file.TrackedFileId];

            if (f.Outcome == VerificationOutcome.Failed || d.Outcome == VerificationOutcome.Failed)
            {
                merged[file.TrackedFileId] = new VerificationResult(
                    file.TrackedFileId,
                    VerificationOutcome.Failed,
                    MatchedTargetFile: f.MatchedTargetFile,
                    DbStatus: d.DbStatus,
                    FolderVerified: f.FolderVerified,
                    DiagnosticNote: f.DiagnosticNote ?? $"DB reported failure: {d.DbStatus}");
                continue;
            }

            var folderDone = file.FolderVerifiedAtUtc.HasValue || f.Outcome == VerificationOutcome.Succeeded;
            var dbDone = d.Outcome == VerificationOutcome.Succeeded;

            if (folderDone && dbDone)
            {
                merged[file.TrackedFileId] = new VerificationResult(
                    file.TrackedFileId,
                    VerificationOutcome.Succeeded,
                    MatchedTargetFile: f.MatchedTargetFile ?? file.MatchedTargetFile,
                    DbStatus: d.DbStatus,
                    FolderVerified: true);
            }
            else
            {
                merged[file.TrackedFileId] = new VerificationResult(
                    file.TrackedFileId,
                    VerificationOutcome.Pending,
                    MatchedTargetFile: f.MatchedTargetFile ?? file.MatchedTargetFile,
                    DbStatus: d.DbStatus ?? file.DbStatus,
                    FolderVerified: folderDone);
            }
        }

        return merged;
    }
}
