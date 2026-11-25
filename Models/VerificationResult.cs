namespace NasFileMonitor.Models;

public enum VerificationOutcome
{
    Pending,
    Succeeded,
    Failed
}

public sealed record VerificationResult(
    long TrackedFileId,
    VerificationOutcome Outcome,
    string? MatchedTargetFile = null,
    string? DbStatus = null,
    bool FolderVerified = false,
    string? DiagnosticNote = null);
