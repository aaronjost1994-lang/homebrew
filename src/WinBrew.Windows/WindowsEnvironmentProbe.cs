using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinBrew.Application.Ports;

namespace WinBrew.Windows;

/// <summary>
/// Real Windows environment probes (A6/A10/A12/D5). Every probe degrades to
/// "unknown" off-Windows (null/false) so doctor can report honestly instead
/// of crashing — behavior against actual Windows APIs is exercised on
/// Windows runners (A16, PROBES.md §4).
/// </summary>
public sealed class WindowsEnvironmentProbe : IEnvironmentProbe
{
    // IMAGE_FILE_MACHINE values (A10 table).
    private const ushort ImageFileMachineI386 = 0x014C;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xAA64;

    private const string LongPathsKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string UserEnvironmentKey = "Environment";

    public Architecture? NativeMachine
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            if (!IsWow64Process2(Process.GetCurrentProcess().Handle, out var _, out var nativeMachine))
            {
                return null;
            }

            return nativeMachine switch
            {
                ImageFileMachineAmd64 => Architecture.X64,
                ImageFileMachineArm64 => Architecture.Arm64,
                ImageFileMachineI386 => Architecture.X86,
                _ => null,
            };
        }
    }

    /// <summary>The running binary's architecture — the BUILD side of the comparison (A10).</summary>
    public Architecture ProcessArchitecture => RuntimeInformation.ProcessArchitecture;

    public bool? LongPathsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(LongPathsKey);
                var value = key?.GetValue("LongPathsEnabled");
                return value is int i ? i != 0 : null;
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    public bool UserPathValueExists
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UserEnvironmentKey);
                return key?.GetValue("Path", null) is not null;
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    public bool AreSameVolume(string pathA, string pathB)
    {
        var rootA = Path.GetPathRoot(Path.GetFullPath(pathA));
        var rootB = Path.GetPathRoot(Path.GetFullPath(pathB));

        if (rootA is null || rootB is null)
        {
            return false;
        }

        if (string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            // Off-Windows, distinct roots are distinct volumes (POSIX single
            // namespace aside — tests use one temp root).
            return false;
        }

        var serialA = GetVolumeSerial(rootA);
        var serialB = GetVolumeSerial(rootB);
        return serialA is uint a && serialB is uint b && a == b;
    }

    public bool IsProcessAlive(int pid, string? processStartTime)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(processStartTime))
            {
                // D5: doctor reports a holder only when BOTH pid and start
                // time match; a missing start time is a mismatch (metadata
                // predates the field or is torn).
                return false;
            }

            var recorded = DateTime.Parse(
                processStartTime,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            var actual = process.StartTime.ToUniversalTime();

            // Same clock, formatting round-trip: allow a small tolerance.
            return Math.Abs((actual - recorded).TotalSeconds) <= 1.5;
        }
        catch (ArgumentException)
        {
            return false; // no such process — PID free or reused
        }
        catch (InvalidOperationException)
        {
            return false; // exited between the checks
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // access denied — cannot confirm, not the holder per D5
        }
    }

    private static uint? GetVolumeSerial(string root)
    {
        return GetVolumeInformationW(root, null, 0, out var serial, out var _, out var _, null, 0)
            ? serial
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        IntPtr hProcess,
        out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        char[]? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        string? lpFileSystemNameBuffer,
        int nFileSystemNameSize);
}
