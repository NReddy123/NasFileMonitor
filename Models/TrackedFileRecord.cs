namespace NasFileMonitor.Models;

public sealed class TrackedFileRecord
{
    public long TrackedFileId { get; set; }
    public string RuleId { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public int AttemptNumber { get; set; } = 1;
    public int StabilityHits { get; set; } = 1;
    public FileState State { get; set; } = FileState.Detected;
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public DateTime? FolderVerifiedAtUtc { get; set; }
    public string? MatchedTargetFile { get; set; }
    public string? DbStatus { get; set; }
    public DateTime? AlertedAtUtc { get; set; }

    public string BuildIdentityKey() =>
        $"{RuleId}|{RelativePath.ToLowerInvariant()}|{FileSizeBytes}|{LastWriteTimeUtc.Ticks}";
}

public sealed record NasFileSnapshot(
    string RelativePath,
    string FileName,
    long FileSizeBytes,
    DateTime LastWriteTimeUtc);
