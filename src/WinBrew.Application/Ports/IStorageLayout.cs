namespace WinBrew.Application.Ports;

/// <summary>Status of the on-disk layout inspection (A6).</summary>
public sealed record StorageLayoutStatus(
    bool RootExists,
    bool AllDirectoriesPresent,
    bool Writable,
    /// <summary>Directory names that would sit on a different volume than state\ (A6 refuses these).</summary>
    IReadOnlyList<string> VolumeConflicts,
    string? Problem)
{
    public static StorageLayoutStatus Ok(bool rootExists, bool allPresent) =>
        new(rootExists, allPresent, Writable: true, VolumeConflicts: [], Problem: null);
}

/// <summary>
/// Storage layout port (A6). The whole layout must stay on one volume so
/// renames are atomic; a configuration that would put staging\, packages\,
/// cache\ or bin\ on a different volume from the rest is refused.
/// </summary>
public interface IStorageLayout
{
    /// <summary>Layout root: %LOCALAPPDATA%\WinBrew, overridden by WINBREW_ROOT (A6).</summary>
    string Root { get; }

    /// <summary>Well-known directories, name → absolute path (packages, bin, cache, staging, state, logs, app, repositories, backups).</summary>
    IReadOnlyDictionary<string, string> Directories { get; }

    /// <summary>Creates missing directories and checks the one-volume rule. Returns the status; throws WinBrewException on refusal.</summary>
    Task<StorageLayoutStatus> EnsureAsync(CancellationToken ct);

    /// <summary>Read-only inspection (doctor does not create anything).</summary>
    Task<StorageLayoutStatus> InspectAsync(CancellationToken ct);
}
