using NasFileMonitor.Configuration;
using NasFileMonitor.Matching;
using NasFileMonitor.Models;

namespace NasFileMonitor.Verification;

public sealed class FolderToFolderVerifier : IFileVerifier
{
    private readonly ITargetFileMatcher _matcher;

    public FolderToFolderVerifier(ITargetFileMatcher matcher)
    {
        _matcher = matcher;
    }

    public Task<IReadOnlyDictionary<long, VerificationResult>> VerifyBatchAsync(
        MonitorRule rule,
        IReadOnlyList<TrackedFileRecord> pendingFiles,
        IReadOnlyList<NasFileSnapshot>? targetSnapshot,
        IReadOnlyList<NasFileSnapshot>? errorSnapshot,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<long, VerificationResult>(pendingFiles.Count);
        var folderConfig = rule.FolderCheck!;

        foreach (var file in pendingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (errorSnapshot is { Count: > 0 })
            {
                var errorMatch = _matcher.FindMatch(file, errorSnapshot, folderConfig);
                if (errorMatch is not null)
                {
                    results[file.TrackedFileId] = new VerificationResult(
                        file.TrackedFileId,
                        VerificationOutcome.Failed,
                        MatchedTargetFile: errorMatch.RelativePath,
                        DiagnosticNote: $"Found in ErrorPath: {errorMatch.RelativePath}");
                    continue;
                }
            }

            if (targetSnapshot is { Count: > 0 })
            {
                var match = _matcher.FindMatch(file, targetSnapshot, folderConfig);
                if (match is not null)
                {
                    results[file.TrackedFileId] = new VerificationResult(
                        file.TrackedFileId,
                        VerificationOutcome.Succeeded,
                        MatchedTargetFile: match.RelativePath,
                        FolderVerified: true);
                    continue;
                }
            }

            results[file.TrackedFileId] = new VerificationResult(file.TrackedFileId, VerificationOutcome.Pending);
        }

        return Task.FromResult<IReadOnlyDictionary<long, VerificationResult>>(results);
    }
}
