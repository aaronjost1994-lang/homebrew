using System.Runtime.InteropServices;

namespace WinBrew.Shim;

/// <summary>
/// wbrew-shim.exe — the shim executable template (D4). Every bin\&lt;name&gt;.exe
/// is a copy of it; the sidecar bin\&lt;name&gt;.shim.json says what to run.
///
/// Contract (D4):
/// 1. Read the sidecar (FileShare.Read | FileShare.Delete), fully, then close it.
/// 2. Containment: the target must be absolute and inside &lt;root&gt;\packages\
///    (the self-shim wbrew.shim.json: inside &lt;root&gt;\app\), where &lt;root&gt;
///    is bin\.. of the shim's OWN location — WINBREW_ROOT is never read.
///    No reparse point may appear anywhere along the target path.
///    Violation: one stderr line, exit 9009. A missing target: exit 9009.
/// 3. argv[0] is stripped from GetCommandLineW() with the MSVC C-runtime
///    rules; the child gets "\"&lt;target&gt;\"" + tail, never re-quoted.
///    Over 32,767 characters: one stderr line, exit 206.
/// 4. Console targets: Job Object (KILL_ON_JOB_CLOSE | BREAKAWAY_OK; no job
///    on failure, logged), a Ctrl+C handler routine returning TRUE so the
///    child receives Ctrl+C, wait, and return the child's exit code.
///    GUI targets: start, do not wait, return 0.
/// The child inherits the working directory, environment and standard handles.
/// </summary>
internal static unsafe class Program
{
    private const int ExitFailure = 1;

    private static int Main()
    {
        try
        {
            return Run();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail(ShimRules.ExitTargetMissing, $"cannot start: {ex.Message}");
        }
    }

    private static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail(ShimRules.ExitTargetMissing, "the WinBrew shim runs on Windows only.");
        }

        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self))
        {
            return Fail(ShimRules.ExitTargetMissing, "cannot determine the shim's own path.");
        }

        var name = Path.GetFileNameWithoutExtension(self);
        var sidecarPath = Path.Combine(Path.GetDirectoryName(self)!, name + ".shim.json");

        // 1. Sidecar: read fully and close BEFORE CreateProcessW (D4).
        var bytes = SidecarReader.ReadAllShared(sidecarPath);
        if (bytes is null)
        {
            return Fail(ShimRules.ExitTargetMissing, $"sidecar '{sidecarPath}' is missing or unreadable.");
        }

        var sidecar = SidecarReader.Parse(bytes);
        if (sidecar is null)
        {
            return Fail(ShimRules.ExitTargetMissing, $"sidecar '{sidecarPath}' is invalid.");
        }

        // 2. Containment (defense in depth only, D4).
        var allowedRoot = ShimRules.AllowedRoot(self, name);
        if (!ShimRules.IsContained(allowedRoot, sidecar.Target, out var target))
        {
            return Fail(ShimRules.ExitTargetMissing, $"refusing target '{sidecar.Target}': it is not inside '{allowedRoot}'.");
        }

        foreach (var prefix in ShimRules.PathPrefixes(target))
        {
            var attributes = Native.GetFileAttributes(prefix);
            if (attributes == Native.InvalidFileAttributes)
            {
                break; // missing component: reported as a missing target below
            }

            if ((attributes & Native.FileAttributeReparsePoint) != 0)
            {
                return Fail(ShimRules.ExitTargetMissing, $"refusing target '{target}': '{prefix}' is a reparse point.");
            }
        }

        var targetAttributes = Native.GetFileAttributes(target);
        if (targetAttributes == Native.InvalidFileAttributes || (targetAttributes & Native.FileAttributeDirectory) != 0)
        {
            return Fail(ShimRules.ExitTargetMissing, $"target '{target}' was not found.");
        }

        // 3. argv[0] strip (MSVC C-runtime rules) and the length check.
        var raw = Native.GetCommandLineW();
        var tail = ShimRules.Tail(raw is null ? null : new string(raw));
#if WINBREW_TEST_HOOKS
        if (!ShimRules.IsWellFormedTail(tail))
        {
            Environment.FailFast("wbrew-shim: argv[0] tail invariant violated (test-hooks assertion, D4).");
        }
#endif
        var commandLine = ShimRules.Compose(target, tail);
        if (ShimRules.ExceedsLimit(commandLine))
        {
            return Fail(ShimRules.ExitCommandLineTooLong,
                $"the command line ({commandLine.Length} characters) exceeds the Windows limit of {ShimRules.MaxCommandLineChars}.");
        }

        return sidecar.Subsystem == "gui" ? StartGui(target, commandLine) : RunConsole(target, commandLine);
    }

    private static int StartGui(string target, string commandLine)
    {
        if (!Start(target, commandLine, Native.CreateUnicodeEnvironment, out var process, out var error))
        {
            return LaunchFailure(target, error);
        }

        Native.CloseHandle(process.Thread);
        Native.CloseHandle(process.Process);
        return 0; // D4: GUI targets are not waited for
    }

    private static int RunConsole(string target, string commandLine)
    {
        // The handler ROUTINE swallows Ctrl+C/Ctrl+Break in the shim; the child,
        // attached to the same console, still receives them normally (D4).
        Native.SetConsoleCtrlHandler(&ConsoleCtrlHandler, true);

        if (!Start(target, commandLine, Native.CreateSuspended | Native.CreateUnicodeEnvironment, out var process, out var error))
        {
            return LaunchFailure(target, error);
        }

        var job = CreateKillOnCloseJob();
        if (job != IntPtr.Zero && !Native.AssignProcessToJobObject(job, process.Process))
        {
            // e.g. the shim already runs inside an incompatible job: continue without one (D4).
            Console.Error.WriteLine($"wbrew-shim: running without a job object (error {Marshal.GetLastPInvokeError()}).");
            Native.CloseHandle(job);
            job = IntPtr.Zero;
        }

        Native.ResumeThread(process.Thread);
        Native.CloseHandle(process.Thread);

        Native.WaitForSingleObject(process.Process, Native.Infinite);
        var exitCode = Native.GetExitCodeProcess(process.Process, out var code) ? unchecked((int)code) : ExitFailure;

        Native.CloseHandle(process.Process);
        if (job != IntPtr.Zero)
        {
            Native.CloseHandle(job); // KILL_ON_JOB_CLOSE ends remaining job members; breakaways survive (documented)
        }

        return exitCode;
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = Native.CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (job == IntPtr.Zero)
        {
            Console.Error.WriteLine($"wbrew-shim: running without a job object (CreateJobObject error {Marshal.GetLastPInvokeError()}).");
            return IntPtr.Zero;
        }

        var info = new Native.JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new Native.JobObjectBasicLimitInformation
            {
                LimitFlags = Native.JobObjectLimitKillOnJobClose | Native.JobObjectLimitBreakawayOk,
            },
        };

        if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info,
                (uint)sizeof(Native.JobObjectExtendedLimitInformationData)))
        {
            Console.Error.WriteLine($"wbrew-shim: running without a job object (SetInformationJobObject error {Marshal.GetLastPInvokeError()}).");
            Native.CloseHandle(job);
            return IntPtr.Zero;
        }

        return job;
    }

    private static bool Start(string target, string commandLine, uint flags, out Native.ProcessInformation process, out int error)
    {
        // lpCommandLine must be a writable, NUL-terminated buffer.
        var buffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(0, buffer, 0, commandLine.Length);

        var startup = new Native.StartupInfoW { Cb = sizeof(Native.StartupInfoW) };
        fixed (char* cmd = buffer)
        {
            // Inherit handles like cmd.exe does: the child gets the shim's
            // standard handles, working directory (null) and environment (null).
            var ok = Native.CreateProcess(target, cmd, IntPtr.Zero, IntPtr.Zero, inheritHandles: true, flags,
                IntPtr.Zero, IntPtr.Zero, ref startup, out process);
            error = ok ? 0 : Marshal.GetLastPInvokeError();
            return ok;
        }
    }

    private static int LaunchFailure(string target, int error) =>
        error is Native.ErrorFileNotFound or Native.ErrorPathNotFound
            ? Fail(ShimRules.ExitTargetMissing, $"target '{target}' was not found.")
            : Fail(ExitFailure, $"cannot start '{target}' (Win32 error {error}).");

    [UnmanagedCallersOnly]
    private static int ConsoleCtrlHandler(uint ctrlType)
    {
        _ = ctrlType;
        return 1; // TRUE: handled here; the child handles its own copy of the event
    }

    private static int Fail(int code, string message)
    {
        Console.Error.WriteLine("wbrew-shim: " + message);
        return code;
    }
}
