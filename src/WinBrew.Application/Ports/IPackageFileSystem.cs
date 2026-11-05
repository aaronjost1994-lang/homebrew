using WinBrew.Core.Archive;
using WinBrew.Core.Pe;

namespace WinBrew.Application.Ports;

/// <summary>Counts from a successful extraction (A13).</summary>
public sealed record ExtractionResult(int Entries, long TotalBytes);

/// <summary>
/// Safe ZIP extraction (A13): every entry-name defense, Unix symlink
/// rejection, case-insensitive duplicates, entry/total/ratio limits on
/// ACTUAL decompressed bytes, containment of each final path, and a
/// post-extraction reparse-point walk. Violations are VerificationFailure
/// (exit 5) with a <c>defense</c> detail naming the rule.
/// </summary>
public interface IArchiveExtractor
{
    Task<ExtractionResult> ExtractZipAsync(string zipPath, string destinationDirectory, ArchiveLimits limits, CancellationToken ct);
}

/// <summary>Content-addressed artifact cache: cache\artifacts\&lt;sha256&gt; (A6, A11 step 4).</summary>
public interface IArtifactCache
{
    /// <summary>Absolute path of the cache entry for a digest (it may not exist).</summary>
    string PathFor(string sha256);

    /// <summary>
    /// Returns the entry path when it exists AND its content still hashes to
    /// <paramref name="sha256"/>; a corrupt entry is deleted and null returned.
    /// </summary>
    Task<string?> TryGetVerifiedAsync(string sha256, CancellationToken ct);

    /// <summary>Moves an already-verified file into the cache (same volume, A6). Idempotent.</summary>
    Task<string> AddVerifiedAsync(string verifiedFile, string sha256, CancellationToken ct);
}

/// <summary>
/// File-system operations with the A12 files-in-use rules: sharing
/// violations (and antivirus-style transient access denials) are retried
/// with bounded backoff (5 attempts, about 3 seconds); files still locked
/// are reported through the Restart Manager (exit 11, never terminating a
/// process); disk full is exit 12. Every operation is idempotent.
/// </summary>
public interface IPackageFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    Task CreateDirectoryAsync(string path, CancellationToken ct);

    /// <summary>Renames a directory (same volume, atomic). The destination must not exist.</summary>
    Task MoveDirectoryAsync(string source, string destination, CancellationToken ct);

    /// <summary>Renames a file; overwrite replaces the destination atomically.</summary>
    Task MoveFileAsync(string source, string destination, bool overwrite, CancellationToken ct);

    /// <summary>Copies a file via a temp file in the destination directory + rename.</summary>
    Task CopyFileAsync(string source, string destination, bool overwrite, CancellationToken ct);

    /// <summary>Recursively deletes a directory; no-op when absent.</summary>
    Task DeleteDirectoryAsync(string path, CancellationToken ct);

    /// <summary>Deletes a file; no-op when absent; throws FilesInUse (exit 11) when still locked.</summary>
    Task DeleteFileAsync(string path, CancellationToken ct);

    /// <summary>Deletes a file; returns false (instead of throwing) when it stays locked after retries.</summary>
    Task<bool> TryDeleteFileAsync(string path, CancellationToken ct);

    /// <summary>Write-to-temp + replace (D4 sidecar rule).</summary>
    Task WriteFileAtomicAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct);

    /// <summary>Reads a small file fully; null when absent.</summary>
    Task<byte[]?> ReadFileAsync(string path, CancellationToken ct);

    /// <summary>Lowercase hex SHA-256 of a file; null when absent.</summary>
    Task<string?> Sha256Async(string path, CancellationToken ct);

    /// <summary>PE header facts of a file; null when it is not a PE image.</summary>
    Task<PeInfo?> ReadPeInfoAsync(string path, CancellationToken ct);

    /// <summary>True when the path is a regular file (not a directory, not a reparse point).</summary>
    bool IsRegularFile(string path);

    /// <summary>The deepest absolute path length under a directory once moved to <paramref name="finalRoot"/> (A12 long-path warning).</summary>
    int DeepestPathLength(string directory, string finalRoot);

    /// <summary>Immediate subdirectory names; empty when the directory is absent.</summary>
    IReadOnlyList<string> ListDirectories(string path);

    /// <summary>True when the directory exists and contains no entries at all.</summary>
    bool IsEmptyDirectory(string path);
}

/// <summary>Restart Manager port (A12): names the processes holding files. Never terminates anything.</summary>
public interface IFileLockInspector
{
    IReadOnlyList<string> GetLockingProcesses(IReadOnlyList<string> paths);
}

/// <summary>No Restart Manager (non-Windows hosts, tests).</summary>
public sealed class NoFileLockInspector : IFileLockInspector
{
    public static readonly NoFileLockInspector Instance = new();

    public IReadOnlyList<string> GetLockingProcesses(IReadOnlyList<string> paths) => [];
}
