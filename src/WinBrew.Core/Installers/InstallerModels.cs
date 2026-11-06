using System.Text.RegularExpressions;

namespace WinBrew.Core.Installers;

/// <summary>
/// The manifest's <c>installer</c> declaration (v1.1, A9/A12). Installer
/// declarations are EXECUTABLE-CODE TRUST DECISIONS, not safe data: they
/// decide what runs, with which arguments, possibly elevated.
/// </summary>
public sealed record InstallerDeclaration(string Type, MsiInstaller? Msi = null, ExeInstaller? Exe = null);

/// <summary>
/// MSI: the ProductCode (tracked and used for uninstall/reconciliation), an
/// optional UpgradeCode, the public properties passed on every install, and
/// the property set the package was AUTHORED for per-user installs (A12: a
/// per-user install happens only when the manifest declares 'user' scope AND
/// this set).
/// </summary>
public sealed record MsiInstaller(
    string ProductCode,
    string? UpgradeCode = null,
    IReadOnlyDictionary<string, string>? Properties = null,
    IReadOnlyDictionary<string, string>? UserProperties = null);

/// <summary>An executable named by a path pattern (e.g. <c>{ProgramFiles}/Vendor/unins000.exe</c>) plus an argument array.</summary>
public sealed record ExeCommand(string Path, IReadOnlyList<string> Args);

/// <summary>EXE detection: an uninstall registry key name and/or a file (with an optional exact file version).</summary>
public sealed record ExeDetection(string? UninstallKey = null, ExeFileDetection? File = null);

public sealed record ExeFileDetection(string Path, string? Version = null);

/// <summary>EXE: explicit silent install arguments, an explicit silent uninstall command, detection rules (A12). Switches are never guessed.</summary>
public sealed record ExeInstaller(IReadOnlyList<string> SilentInstallArgs, ExeCommand SilentUninstall, ExeDetection Detection);

/// <summary>
/// Path patterns used by EXE uninstall and detection rules: a known root
/// token followed by relative segments. Expansion happens on the machine
/// (Windows adapter); validation here guarantees no traversal, no drive
/// letters, no device names and no alternate data streams.
/// </summary>
public static partial class InstallerPaths
{
    public static readonly IReadOnlyList<string> UserRoots = ["{LocalAppData}", "{AppData}"];
    public static readonly IReadOnlyList<string> MachineRoots = ["{ProgramFiles}", "{ProgramFilesX86}", "{ProgramData}"];

    [GeneratedRegex(@"^[A-Za-z0-9 ._()+@-]{1,128}$")]
    private static partial Regex SegmentRegex();

    /// <summary>Splits a pattern into its root token and relative segments, or returns null with a reason.</summary>
    public static (string Root, IReadOnlyList<string> Segments)? Split(string pattern, out string? problem)
    {
        problem = null;
        var root = UserRoots.Concat(MachineRoots).FirstOrDefault(r => pattern.StartsWith(r, StringComparison.Ordinal));
        if (root is null)
        {
            problem = $"must start with one of {string.Join(", ", UserRoots.Concat(MachineRoots))}";
            return null;
        }

        var rest = pattern[root.Length..].Replace('\\', '/');
        if (!rest.StartsWith('/'))
        {
            problem = "a separator must follow the root token";
            return null;
        }

        var segments = rest[1..].Split('/');
        foreach (var segment in segments)
        {
            if (segment is "" or "." or ".." || !SegmentRegex().IsMatch(segment) || segment.EndsWith('.') || segment.EndsWith(' ') ||
                Identifiers.ReservedDeviceNames.Contains(segment.Split('.')[0].ToUpperInvariant()))
            {
                problem = $"segment '{segment}' is not allowed (no traversal, ':' or device names)";
                return null;
            }
        }

        return (root, segments);
    }

    public static bool IsMachineRoot(string root) => MachineRoots.Contains(root, StringComparer.Ordinal);
}

/// <summary>MSI public-property and argument rules (A12: only declared properties; no shell).</summary>
public static partial class InstallerArguments
{
    /// <summary>Properties WinBrew controls itself (scope) or that would change the msiexec invocation.</summary>
    public static readonly IReadOnlySet<string> ReservedProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "ALLUSERS", "MSIINSTALLPERUSER", "REBOOT", "REBOOTPROMPT",
    };

    [GeneratedRegex("^[A-Z_][A-Z0-9_.]{0,63}$")]
    public static partial Regex PropertyNameRegex();

    /// <summary>
    /// Conservative value alphabet: no spaces, quotes or control characters, so
    /// every value reaches msiexec byte-for-byte through ArgumentList.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9._:\\/{}@+,=-]{0,1024}$")]
    public static partial Regex PropertyValueRegex();

    [GeneratedRegex(@"^\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}$")]
    public static partial Regex GuidRegex();

    /// <summary>EXE arguments: any printable text without control characters (passed through ArgumentList, never a shell).</summary>
    public static bool IsSafeArgument(string value) =>
        value.Length is > 0 and <= 1024 && !value.Any(char.IsControl);
}
