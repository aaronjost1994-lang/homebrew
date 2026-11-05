using System.Security.Cryptography;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Pe;
using WinBrew.Infrastructure.Http;

namespace WinBrew.Infrastructure.Files;

/// <summary>
/// File operations with the A12 rules: sharing violations and transient
/// access denials (antivirus scanners) are retried with bounded backoff —
/// 5 attempts, about 3 seconds in total; files still locked are named via
/// the Restart Manager and reported as FilesInUse (exit 11) — no process is
/// ever terminated. Disk full maps to InsufficientSpace (exit 12).
/// </summary>
public sealed class PackageFileSystem(IFileLockInspector lockInspector) : IPackageFileSystem
{
    /// <summary>Delays between the 5 attempts: 0.2 + 0.4 + 0.8 + 1.6 ≈ 3 s (A12).</summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1600),
    ];

    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) =>
        RetryAsync([path], () => Directory.CreateDirectory(path), ct);

    public Task MoveDirectoryAsync(string source, string destination, CancellationToken ct) =>
        RetryAsync([source, destination], () => Directory.Move(source, destination), ct);

    public Task MoveFileAsync(string source, string destination, bool overwrite, CancellationToken ct) =>
        RetryAsync([source, destination], () => File.Move(source, destination, overwrite), ct);

    public async Task CopyFileAsync(string source, string destination, bool overwrite, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(destination)}.tmp-{Guid.NewGuid():N}");
        try
        {
            await RetryAsync([source, temp], () => File.Copy(source, temp, overwrite: false), ct);
            await RetryAsync([destination], () => File.Move(temp, destination, overwrite), ct);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public Task DeleteDirectoryAsync(string path, CancellationToken ct) =>
        RetryAsync([path], () =>
        {
            if (Directory.Exists(path))
            {
                ClearReadOnly(path);
                Directory.Delete(path, recursive: true);
            }
        }, ct);

    public Task DeleteFileAsync(string path, CancellationToken ct) =>
        RetryAsync([path], () =>
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }, ct);

    public async Task<bool> TryDeleteFileAsync(string path, CancellationToken ct)
    {
        try
        {
            await DeleteFileAsync(path, ct);
            return true;
        }
        catch (WinBrewException ex) when (ex.Code == WinBrewErrorCode.FilesInUse)
        {
            return false;
        }
    }

    public async Task WriteFileAtomicAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, ct);
                await stream.FlushAsync(ct);
            }

            // File.Move(overwrite) is an atomic replace on the same volume (D4 sidecar rule).
            await RetryAsync([path], () => File.Move(temp, path, overwrite: true), ct);
        }
        catch (IOException ex) when (DiskFull.Is(ex))
        {
            throw new WinBrewException(WinBrewErrorCode.InsufficientSpace, $"Disk full writing '{path}': {ex.Message}");
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public async Task<byte[]?> ReadFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(path, ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public async Task<string?> Sha256Async(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    public async Task<PeInfo?> ReadPeInfoAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[PeImage.HeaderProbeLength];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        return PeImage.TryParse(buffer.AsSpan(0, read));
    }

    public bool IsRegularFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
    }

    public int DeepestPathLength(string directory, string finalRoot)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var root = Path.GetFullPath(directory);
        var deepest = finalRoot.Length;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, entry);
            deepest = Math.Max(deepest, Path.Combine(finalRoot, relative).Length);
        }

        return deepest;
    }

    public IReadOnlyList<string> ListDirectories(string path) =>
        Directory.Exists(path)
            ? Directory.GetDirectories(path).Select(d => Path.GetFileName(d)!).OrderBy(n => n, StringComparer.Ordinal).ToList()
            : [];

    public bool IsEmptyDirectory(string path) => Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();

    // ---- A12 retry core ---------------------------------------------------------------------

    private static bool IsLockLike(Exception ex) => ex switch
    {
        IOException io when io.HResult is ErrorSharingViolation or ErrorLockViolation => true,
        UnauthorizedAccessException => true, // antivirus scanners briefly deny access (A12)
        _ => false,
    };

    private async Task RetryAsync(IReadOnlyList<string> paths, Action action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (DiskFull.Is(ex))
            {
                throw new WinBrewException(WinBrewErrorCode.InsufficientSpace,
                    $"Disk full or quota exceeded at '{paths[0]}': {ex.Message}");
            }
            catch (Exception ex) when (IsLockLike(ex))
            {
                if (attempt >= RetryDelays.Length)
                {
                    var holders = lockInspector.GetLockingProcesses(paths);
                    throw new WinBrewException(WinBrewErrorCode.FilesInUse,
                        holders.Count > 0
                            ? $"Files are in use by {string.Join(", ", holders)}: {string.Join(", ", paths)}. Close them and retry (A12)."
                            : $"Files are locked or access is denied: {string.Join(", ", paths)} ({ex.Message}).",
                        new Dictionary<string, object?> { ["paths"] = paths, ["processes"] = holders });
                }

                await Task.Delay(RetryDelays[attempt], ct);
            }
        }
    }

    private static void ClearReadOnly(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }
}
