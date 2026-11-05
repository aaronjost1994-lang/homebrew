using WinBrew.Application.Ports;
using WinBrew.Core.Shims;

namespace WinBrew.Windows;

/// <summary>
/// bin\ file mechanics for shims (D4, A12). Decisions about ownership are
/// made by the use cases from the database; this class never deletes or
/// overwrites anything on its own initiative.
/// <list type="bullet">
/// <item>Sidecars: write-to-temp + atomic replace.</item>
/// <item>Binaries: a binary that must change is ALWAYS renamed aside to a
/// journaled <c>&lt;name&gt;.exe.old.&lt;guid&gt;</c> first (renaming a running
/// .exe is permitted on Windows, overwriting it is not), then the new copy
/// is moved into place. The journal records the .old path BEFORE the
/// rename; cleanup later deletes only recorded files.</item>
/// </list>
/// </summary>
public sealed class ShimStore(string binDirectory, IPackageFileSystem files) : IShimStore
{
    public string BinDirectory { get; } = Path.GetFullPath(binDirectory);

    public string BinaryPath(string name) => Path.Combine(BinDirectory, name + ".exe");

    public string SidecarPath(string name) => Path.Combine(BinDirectory, name + ShimSidecar.SidecarSuffix);

    public Task<byte[]?> ReadSidecarAsync(string name, CancellationToken ct) => files.ReadFileAsync(SidecarPath(name), ct);

    public Task WriteSidecarAsync(string name, ReadOnlyMemory<byte> content, CancellationToken ct) =>
        files.WriteFileAtomicAsync(SidecarPath(name), content, ct);

    public Task DeleteSidecarAsync(string name, CancellationToken ct) => files.DeleteFileAsync(SidecarPath(name), ct);

    public bool BinaryExists(string name) => files.FileExists(BinaryPath(name));

    public Task<string?> BinarySha256Async(string name, CancellationToken ct) => files.Sha256Async(BinaryPath(name), ct);

    public async Task<ShimBinaryPlacement> PlaceBinaryAsync(
        string name,
        string templatePath,
        string templateSha256,
        Func<string, CancellationToken, Task> journalOldFile,
        CancellationToken ct)
    {
        var target = BinaryPath(name);
        string? oldPath = null;

        if (files.FileExists(target))
        {
            if (await files.Sha256Async(target, ct) == templateSha256)
            {
                return new ShimBinaryPlacement(Placed: false, OldPath: null); // D4: only the sidecar changes
            }

            oldPath = await RenameAsideAsync(target, journalOldFile, ct);
        }

        await files.CreateDirectoryAsync(BinDirectory, ct);
        await files.CopyFileAsync(templatePath, target, overwrite: false, ct);
        return new ShimBinaryPlacement(Placed: true, OldPath: oldPath);
    }

    public async Task RemoveBinaryAsync(string name, Func<string, CancellationToken, Task> journalOldFile, CancellationToken ct)
    {
        var target = BinaryPath(name);
        if (!files.FileExists(target))
        {
            return;
        }

        // Rename aside (works while the shim runs), journaled; cleanup deletes it later.
        await RenameAsideAsync(target, journalOldFile, ct);
    }

    public IReadOnlyList<string> ListBinaryNames() =>
        Directory.Exists(BinDirectory)
            ? Directory.GetFiles(BinDirectory, "*.exe").Select(p => Path.GetFileNameWithoutExtension(p)!).ToList()
            : [];

    private async Task<string> RenameAsideAsync(string target, Func<string, CancellationToken, Task> journalOldFile, CancellationToken ct)
    {
        var oldPath = target + ".old." + Guid.NewGuid().ToString("N");
        await journalOldFile(oldPath, ct); // intent before the rename (D4)
        await files.MoveFileAsync(target, oldPath, overwrite: false, ct);
        return oldPath;
    }
}
