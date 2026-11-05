using System.Runtime.InteropServices;
using WinBrew.Core.Pe;

namespace WinBrew.Core.Artifacts;

/// <summary>Outcome of artifact architecture selection (A10).</summary>
public sealed record ArchitectureSelection(string Architecture, bool IsFallback, bool IsX86Fallback);

/// <summary>
/// A10 rules: the artifact selection order and the PE machine types accepted
/// on each host. The host is the NATIVE machine (IsWow64Process2, falling
/// back to OSArchitecture) — never ProcessArchitecture.
/// </summary>
public static class HostArchitectureRules
{
    /// <summary>Wire name of a host architecture ("x64" / "arm64").</summary>
    public static string HostName(Architecture host) => host switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        _ => throw new WinBrewException(WinBrewErrorCode.GeneralFailure,
            $"Unsupported host architecture '{host}': WinBrew supports x64 and ARM64 Windows hosts (A4)."),
    };

    /// <summary>The A10 artifact acceptance order for a host.</summary>
    public static IReadOnlyList<string> SelectionOrder(Architecture host) => host switch
    {
        Architecture.Arm64 => ["arm64", "x64", "x86"],
        Architecture.X64 => ["x64", "x86"],
        _ => throw new WinBrewException(WinBrewErrorCode.GeneralFailure,
            $"Unsupported host architecture '{host}' (A4)."),
    };

    /// <summary>
    /// Picks the first available architecture in the A10 order. Returns null
    /// when none of the offered architectures runs on the host.
    /// </summary>
    public static ArchitectureSelection? Select(Architecture host, IEnumerable<string> available)
    {
        var offered = new HashSet<string>(available, StringComparer.Ordinal);
        var order = SelectionOrder(host);
        for (var i = 0; i < order.Count; i++)
        {
            if (offered.Contains(order[i]))
            {
                return new ArchitectureSelection(order[i], IsFallback: i > 0, IsX86Fallback: order[i] == "x86");
            }
        }

        return null;
    }

    /// <summary>
    /// PE machine-type acceptance (A10 table). ARM32 (0x01C4) and every
    /// unknown machine type are always rejected.
    /// </summary>
    public static bool IsAcceptedMachine(Architecture host, ushort machine) => host switch
    {
        Architecture.X64 => machine is PeImage.MachineAmd64 or PeImage.MachineI386,
        Architecture.Arm64 => machine is PeImage.MachineArm64 or PeImage.MachineAmd64 or PeImage.MachineI386,
        _ => false,
    };

    /// <summary>Display name of a PE machine value for error messages.</summary>
    public static string MachineName(ushort machine) => machine switch
    {
        PeImage.MachineAmd64 => "AMD64 (0x8664)",
        PeImage.MachineI386 => "I386 (0x014C)",
        PeImage.MachineArm64 => "ARM64 (0xAA64)",
        PeImage.MachineArmNt => "ARM32 (0x01C4, unsupported)",
        _ => $"unknown (0x{machine:X4})",
    };
}
