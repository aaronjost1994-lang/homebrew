using System.Runtime.InteropServices;
using WinBrew.Application.Ports;

namespace WinBrew.Windows;

/// <summary>
/// Restart Manager (A12): RmStartSession / RmRegisterResources / RmGetList
/// name the processes holding files. Read-only by design — WinBrew never
/// calls RmShutdown and never terminates a process (A3).
/// </summary>
public sealed class RestartManagerInspector : IFileLockInspector
{
    public IReadOnlyList<string> GetLockingProcesses(IReadOnlyList<string> paths)
    {
        if (!OperatingSystem.IsWindows() || paths.Count == 0)
        {
            return [];
        }

        var key = new char[CchRmSessionKey + 1];
        if (RmStartSession(out var session, 0, key) != 0)
        {
            return [];
        }

        try
        {
            var files = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (files.Length == 0 || RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0)
            {
                return [];
            }

            uint needed = 0;
            uint count = 0;
            uint reasons = 0;
            var result = RmGetList(session, out needed, ref count, null, ref reasons);
            if (result != ErrorMoreData || needed == 0)
            {
                return [];
            }

            var infos = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0)
            {
                return [];
            }

            return infos.Take((int)count)
                .Select(i => $"{i.AppName} (PID {i.Process.ProcessId})")
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    private const int CchRmSessionKey = 32;
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, char[] strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RmUniqueProcess[]? rgApplications,
        uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps, ref uint lpdwRebootReasons);
}
