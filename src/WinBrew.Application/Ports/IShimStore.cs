namespace WinBrew.Application.Ports;

/// <summary>Result of placing a shim binary (D4).</summary>
/// <param name="Placed">True when bin\&lt;name&gt;.exe was written.</param>
/// <param name="OldPath">The journaled bin\&lt;name&gt;.exe.old.&lt;guid&gt; path when the previous binary had to be renamed aside.</param>
public sealed record ShimBinaryPlacement(bool Placed, string? OldPath);

/// <summary>
/// bin\ management (D4, A12). Sidecars are always written via temp +
/// replace. Ownership is decided by the database, never by hashes — this
/// port only performs file mechanics; use cases decide what is managed.
/// </summary>
public interface IShimStore
{
    string BinDirectory { get; }

    string BinaryPath(string name);

    string SidecarPath(string name);

    /// <summary>Raw sidecar bytes, or null when absent.</summary>
    Task<byte[]?> ReadSidecarAsync(string name, CancellationToken ct);

    /// <summary>Atomic sidecar replace (write-to-temp + File.Replace/Move(overwrite), D4).</summary>
    Task WriteSidecarAsync(string name, ReadOnlyMemory<byte> content, CancellationToken ct);

    Task DeleteSidecarAsync(string name, CancellationToken ct);

    bool BinaryExists(string name);

    Task<string?> BinarySha256Async(string name, CancellationToken ct);

    /// <summary>
    /// Places bin\&lt;name&gt;.exe as a copy of <paramref name="templatePath"/>.
    /// When the binary exists and matches the template hash it is left
    /// untouched. When it must change and is locked (a running shim), it is
    /// renamed to &lt;name&gt;.exe.old.&lt;guid&gt; on the same volume FIRST and the
    /// rename is reported to <paramref name="journalOldFile"/> BEFORE the
    /// rename happens (intent-before, D4) — then the new binary is placed.
    /// </summary>
    Task<ShimBinaryPlacement> PlaceBinaryAsync(
        string name,
        string templatePath,
        string templateSha256,
        Func<string, CancellationToken, Task> journalOldFile,
        CancellationToken ct);

    /// <summary>
    /// Removes bin\&lt;name&gt;.exe. When it is locked, renames it to a
    /// journaled .old.&lt;guid&gt; (reported first via <paramref name="journalOldFile"/>).
    /// </summary>
    Task RemoveBinaryAsync(string name, Func<string, CancellationToken, Task> journalOldFile, CancellationToken ct);

    /// <summary>Names of every bin\*.exe that is not a recorded .old file (for collision checks).</summary>
    IReadOnlyList<string> ListBinaryNames();
}

/// <summary>Outcome of a PATH change (A12).</summary>
public sealed record PathChangeResult(bool Changed, bool AlreadyPresent, string? Warning);

/// <summary>
/// HKCU\Environment\Path editor (A12). Implementations read the raw value
/// with DoNotExpandEnvironmentNames, keep the value kind, keep order,
/// compare case-insensitively with normalized trailing slashes, never add a
/// duplicate, create a missing value as REG_EXPAND_SZ, refuse (exit 6) above
/// 32,767 characters, warn above 2,047 and broadcast WM_SETTINGCHANGE.
/// </summary>
public interface IUserPathEditor
{
    /// <summary>False off-Windows: PATH integration is unavailable and reported, never faked.</summary>
    bool IsSupported { get; }

    Task<PathChangeResult> AddAsync(string entry, CancellationToken ct);

    /// <summary>Removes only the exact entry WinBrew added. Returns true when something was removed.</summary>
    Task<bool> RemoveAsync(string entry, CancellationToken ct);

    /// <summary>Entries currently on the user PATH that appear BEFORE <paramref name="entry"/> (A12 earlier-on-PATH warning).</summary>
    IReadOnlyList<string> EntriesBefore(string entry);
}

/// <summary>Where the running wbrew lives (A14 self install, D4 template lookup).</summary>
public interface ISelfLocation
{
    /// <summary>Full path of the running wbrew executable.</summary>
    string ExecutablePath { get; }

    /// <summary>Directory holding wbrew.exe, e_sqlite3.dll and wbrew-shim.exe.</summary>
    string ExecutableDirectory { get; }
}
