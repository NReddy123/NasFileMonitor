namespace NasFileMonitor.Configuration;

public sealed class MonitorConfig
{
    public PollingDefaults PollingDefaults { get; set; } = new();
    public List<MonitorRule> Monitors { get; set; } = new();
}

public sealed class PollingDefaults
{
    public int ScanIntervalSeconds { get; set; } = 30;
    public int StabilityChecksRequired { get; set; } = 2;
    public int MaxParallelRules { get; set; } = 4;
    public int PerRuleTimeoutSeconds { get; set; } = 45;
    public int LeaseDurationSeconds { get; set; } = 60;
}

public sealed class MonitorRule
{
    public string RuleId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string SourcePath { get; set; } = string.Empty;
    public string FilePattern { get; set; } = "*.*";
    public bool IncludeSubdirectories { get; set; }
    public ExistingFilesPolicy ExistingFilesPolicy { get; set; } = ExistingFilesPolicy.BaselineOnly;
    public int SlaMinutes { get; set; } = 15;
    public VerificationMode VerificationMode { get; set; } = VerificationMode.FolderToFolder;
    public FolderCheckConfig? FolderCheck { get; set; }
    public DbCheckConfig? DbCheck { get; set; }
}

public sealed class FolderCheckConfig
{
    public string TargetPath { get; set; } = string.Empty;
    public string? ErrorPath { get; set; }
    public SourceDisposition SourceDisposition { get; set; } = SourceDisposition.Copied;
    public bool TrackFastPathMoves { get; set; }
    public string TargetFileNameRegex { get; set; } = "^{BaseName}{Ext}$";
    public string? SourceExtractRegex { get; set; }
    public bool IgnoreCase { get; set; } = true;
    public int MinTargetModifiedOffsetSeconds { get; set; } = -120;
    public bool VerifySizeMatch { get; set; }
}

public sealed class DbCheckConfig
{
    public string ConnectionStringName { get; set; } = "BusinessAppDb";
    public string QueryKey { get; set; } = string.Empty;
    public string KeySource { get; set; } = "FileName";
    public int BatchSize { get; set; } = 200;
    public List<string> SuccessValues { get; set; } = new();
    public List<string> FailureValues { get; set; } = new();
}

public enum VerificationMode
{
    FolderToFolder,
    FolderToDb,
    Hybrid
}

public enum ExistingFilesPolicy
{
    BaselineOnly,
    BaselineAndVerify
}

public enum SourceDisposition
{
    Copied,
    Moved
}
