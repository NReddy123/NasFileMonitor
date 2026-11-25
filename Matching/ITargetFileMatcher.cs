using NasFileMonitor.Configuration;
using NasFileMonitor.Models;

namespace NasFileMonitor.Matching;

public interface ITargetFileMatcher
{
    NasFileSnapshot? FindMatch(
        TrackedFileRecord sourceFile,
        IReadOnlyList<NasFileSnapshot> targetSnapshot,
        FolderCheckConfig config);
}
