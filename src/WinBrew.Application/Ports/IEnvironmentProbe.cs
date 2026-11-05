using System.Runtime.InteropServices;

namespace WinBrew.Application.Ports;

/// <summary>
/// Environment probes for doctor and artifact selection (A10/A12). The real
/// implementation lives in WinBrew.Windows (IsWow64Process2, registry);
/// tests supply fakes. Never use RuntimeInformation.ProcessArchitecture for
/// host detection — it reports the emulated architecture under x64-on-ARM64
/// emulation (A10); that value is exposed here only as the *build*
/// architecture for the build-vs-OS comparison.
/// </summary>
public interface IEnvironmentProbe
{
    /// <summary>Native machine of the OS (IsWow64Process2 on Windows); null when not determinable.</summary>
    Architecture? NativeMachine { get; }

    /// <summary>Architecture of the running binary (for the build-vs-OS warning).</summary>
    Architecture ProcessArchitecture { get; }

    /// <summary>System LongPathsEnabled setting (A12); null when not determinable.</summary>
    bool? LongPathsEnabled { get; }

    /// <summary>Whether the HKCU\Environment\Path value exists at all (A12).</summary>
    bool UserPathValueExists { get; }

    /// <summary>
    /// True when both paths resolve onto the same volume (A6: staging,
    /// packages, cache and bin must share the volume with state).
    /// </summary>
    bool AreSameVolume(string pathA, string pathB);

    /// <summary>
    /// True when a live process matches BOTH the pid and the recorded start
    /// time (D5 lock-holder inspection: protects against PID reuse).
    /// </summary>
    bool IsProcessAlive(int pid, string? processStartTime);
}
