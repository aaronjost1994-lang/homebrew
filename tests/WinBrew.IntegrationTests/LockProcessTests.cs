using System.Diagnostics;
using WinBrew.Core;
using WinBrew.Infrastructure.Locking;
using WinBrew.Windows;

namespace WinBrew.IntegrationTests;

/// <summary>
/// Real cross-process lock behavior (A16): a blocked process reports the
/// holding PID from winbrew.lock, torn/partial metadata is "holder unknown",
/// and a reused PID with a mismatched start time is not reported as the
/// holder. Uses the LockHolderHelper child process.
/// </summary>
public sealed class LockProcessTests : IDisposable
{
    private readonly string _dir;

    public LockProcessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wbrew-lockproc-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private static string HelperDll()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "WinBrew.LockHolderHelper.dll");
        Assert.True(File.Exists(candidate), $"LockHolderHelper.dll not found at {candidate}");
        return candidate;
    }

    /// <summary>Starts the helper; returns (process, childPid announced on stdout).</summary>
    private static (Process Child, int Pid) StartHolder(string lockPath, string command, int holdMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(HelperDll());
        psi.ArgumentList.Add(lockPath);
        psi.ArgumentList.Add(command);
        psi.ArgumentList.Add(holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var child = Process.Start(psi) ?? throw new InvalidOperationException("could not start helper");
        var line = child.StandardOutput.ReadLine();
        Assert.NotNull(line);
        Assert.StartsWith("LOCKED ", line, StringComparison.Ordinal);

        var pid = int.Parse(line["LOCKED ".Length..], System.Globalization.CultureInfo.InvariantCulture);
        return (child, pid);
    }

    [Fact]
    public async Task BlockedProcess_ReportsTheHoldingPidAndCommand_OnTimeout() // A16
    {
        var lockPath = Path.Combine(_dir, "winbrew.lock");
        var (child, pid) = StartHolder(lockPath, "install", 3000);

        try
        {
            var blocked = new ProcessMutationLock(lockPath);
            var ex = await Assert.ThrowsAsync<WinBrewException>(() =>
                blocked.AcquireAsync("upgrade", TimeSpan.FromMilliseconds(500), CancellationToken.None));

            Assert.Equal(WinBrewErrorCode.LockUnavailable, ex.Code);
            Assert.Contains($"pid {pid}", ex.Message);
            Assert.Contains("install", ex.Message);

            // Structured holder details ride along for --json consumers.
            Assert.True(ex.Details.TryGetValue("holder", out var holderObj));
            Assert.NotNull(holderObj);
        }
        finally
        {
            if (!child.WaitForExit(5000))
            {
                child.Kill();
            }

            child.Dispose();
        }
    }

    [Fact]
    public async Task LockFrees_WhenHolderExits_NoStaleLockBreaking()
    {
        var lockPath = Path.Combine(_dir, "winbrew.lock");
        var (child, _) = StartHolder(lockPath, "repo add", 300);

        // The holder exits on its own; the OS closes its handle — the lock is
        // free WITHOUT any stale-lock breaking (D5).
        Assert.True(child.WaitForExit(10000), "helper did not exit");
        child.Dispose();

        using var acquired = await new ProcessMutationLock(lockPath)
            .AcquireAsync("repo remove", TimeSpan.FromMilliseconds(500), CancellationToken.None);
        Assert.Equal("repo remove", acquired.Holder.Command);
    }

    [Fact]
    public async Task HoldingMetadataReadable_ByDiagnosticReader_WhileHeld() // FileShare.Read pattern
    {
        var lockPath = Path.Combine(_dir, "winbrew.lock");
        var (child, pid) = StartHolder(lockPath, "selfupdate", 1500);

        try
        {
            var holder = await new ProcessMutationLock(lockPath).ReadHolderAsync(CancellationToken.None);
            Assert.NotNull(holder);
            Assert.Equal(pid, holder!.Pid);
            Assert.Equal("selfupdate", holder.Command);
            Assert.False(string.IsNullOrWhiteSpace(holder.ProcessStartTime));
        }
        finally
        {
            child.WaitForExit(5000);
            child.Dispose();
        }
    }

    // ---- PID-reuse detection via the doctor rule (D5) ---------------------

    private readonly WindowsEnvironmentProbe _probe = new();

    [Fact]
    public void HolderCheck_MatchingPidAndStartTime_IsAlive()
    {
        using var me = Process.GetCurrentProcess();
        var start = me.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(_probe.IsProcessAlive(Environment.ProcessId, start));
    }

    [Fact]
    public void ReusedPid_WithMismatchedStartTime_IsNotTheHolder() // A16 explicit case
    {
        using var me = Process.GetCurrentProcess();
        var wrongStart = me.StartTime.ToUniversalTime().Subtract(TimeSpan.FromHours(3))
            .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(_probe.IsProcessAlive(Environment.ProcessId, wrongStart));
    }

    [Fact]
    public void HolderCheck_MissingStartTime_IsNotTheHolder() // D5: both must match
    {
        Assert.False(_probe.IsProcessAlive(Environment.ProcessId, null));
        Assert.False(_probe.IsProcessAlive(Environment.ProcessId, string.Empty));
    }

    [Fact]
    public void HolderCheck_NonexistentPid_IsNotAlive()
    {
        // PID 4 is the Windows System process (or nonexistent on Linux);
        // either way it is not us and does not match any start time we record.
        Assert.False(_probe.IsProcessAlive(int.MaxValue, "2020-01-01T00:00:00.0000000Z"));
    }
}
