namespace NasFileMonitor.Models;

public enum FileState
{
    BaselineIgnored,
    Detected,
    PendingVerification,
    Succeeded,
    SucceededFastPath,
    Failed,
    SlaBreached,
    PartialMismatch,
    VanishedUnverified
}

public static class FileStateExtensions
{
    public static bool IsTerminal(this FileState state) => state is
        FileState.BaselineIgnored or
        FileState.Succeeded or
        FileState.SucceededFastPath or
        FileState.Failed;
}
