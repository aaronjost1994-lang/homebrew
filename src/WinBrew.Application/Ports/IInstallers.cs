using WinBrew.Core.Installers;

namespace WinBrew.Application.Ports;

/// <summary>
/// An installer-managed package (v1.1, A12): recorded in the user's database
/// with ProductCode/UpgradeCode, scope and version. Per-machine installs are
/// recorded here too and reconciled through the ProductCode; other users'
/// WinBrew instances do not see them (documented limitation).
/// </summary>
/// <param name="State">Installed | RebootRequired | Unknown.</param>
/// <param name="InstallerJson">The manifest's installer declaration at install time (uninstall never depends on the catalog).</param>
public sealed record InstallerPackage(
    string Repo,
    string Id,
    string Version,
    string Arch,
    string InstallType,
    string Scope,
    string? ProductCode,
    string? UpgradeCode,
    string State,
    string? LogPath,
    string InstallerJson,
    string InstalledAt,
    string UpdatedAt);

public interface IInstallerStore
{
    Task<InstallerPackage?> GetInstallerPackageAsync(string repo, string id, CancellationToken ct);

    Task<IReadOnlyList<InstallerPackage>> ListInstallerPackagesAsync(CancellationToken ct);

    Task UpsertInstallerPackageAsync(InstallerPackage package, CancellationToken ct);

    Task DeleteInstallerPackageAsync(string repo, string id, CancellationToken ct);
}

/// <summary>MsiQueryProductState results that matter for reconciliation (A12).</summary>
public enum MsiProductState
{
    Unknown,
    Absent,
    Installed,
    Advertised,
}

/// <summary>Windows Installer queries (msi.dll). Read-only.</summary>
public interface IMsiProducts
{
    bool IsSupported { get; }

    MsiProductState QueryState(string productCode);

    /// <summary>MsiGetProductInfoEx: "user" (per-user unmanaged), "machine", or null when not installed in either context.</summary>
    string? GetInstalledContext(string productCode);
}

/// <summary>A copied artifact opened with FileShare.Read only, hashed from that handle, kept open while the installer runs (A12).</summary>
public sealed record HeldArtifact(string Path, string Sha256, IDisposable Handle);

/// <summary>Runs msiexec / an EXE through ProcessStartInfo.ArgumentList — never a shell (A12).</summary>
public interface IInstallerRunner
{
    bool IsSupported { get; }

    Task<HeldArtifact> HoldAsync(string path, CancellationToken ct);

    /// <summary>%SystemRoot%\System32\msiexec.exe with <paramref name="arguments"/>.</summary>
    Task<int> RunMsiexecAsync(IReadOnlyList<string> arguments, CancellationToken ct);

    Task<int> RunExecutableAsync(string path, IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>Starts WinBrew.ElevatedHelper (runas, once per elevated operation) with a typed request file (A12).</summary>
public interface IElevatedRunner
{
    Task<ElevatedResult> RunAsync(ElevatedRequest request, CancellationToken ct);
}

/// <summary>EXE detection and path-pattern expansion (A12).</summary>
public interface IInstallProbe
{
    /// <summary>Expands an <see cref="InstallerPaths"/> pattern to an absolute path, or null when its root is unknown here.</summary>
    string? Expand(string pattern);

    /// <summary>True when an Uninstall key with this name exists (HKCU, HKLM, both registry views).</summary>
    bool UninstallKeyExists(string keyName);

    /// <summary>Whether the file exists, and its file version.</summary>
    (bool Exists, string? Version) GetFile(string path);
}
