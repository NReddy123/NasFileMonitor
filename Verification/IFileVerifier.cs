using NasFileMonitor.Configuration;
using NasFileMonitor.Models;

namespace NasFileMonitor.Verification;

public interface IFileVerifier
{
    Task<IReadOnlyDictionary<long, VerificationResult>> VerifyBatchAsync(
        MonitorRule rule,
        IReadOnlyList<TrackedFileRecord> pendingFiles,
        IReadOnlyList<NasFileSnapshot>? targetSnapshot,
        IReadOnlyList<NasFileSnapshot>? errorSnapshot,
        CancellationToken cancellationToken);
}

public interface IFileVerifierResolver
{
    IFileVerifier Resolve(VerificationMode mode);
}

public sealed class FileVerifierResolver : IFileVerifierResolver
{
    private readonly FolderToFolderVerifier _folderVerifier;
    private readonly FolderToDbVerifier _dbVerifier;
    private readonly HybridVerifier _hybridVerifier;

    public FileVerifierResolver(
        FolderToFolderVerifier folderVerifier,
        FolderToDbVerifier dbVerifier,
        HybridVerifier hybridVerifier)
    {
        _folderVerifier = folderVerifier;
        _dbVerifier = dbVerifier;
        _hybridVerifier = hybridVerifier;
    }

    public IFileVerifier Resolve(VerificationMode mode) => mode switch
    {
        VerificationMode.FolderToFolder => _folderVerifier,
        VerificationMode.FolderToDb => _dbVerifier,
        VerificationMode.Hybrid => _hybridVerifier,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported verification mode.")
    };
}
