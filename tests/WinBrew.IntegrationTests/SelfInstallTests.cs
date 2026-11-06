using Microsoft.Win32;
using WinBrew.Core.Shims;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>Stand-ins for the release-zip set next to the test host (A14: wbrew.exe, e_sqlite3.dll, wbrew-shim.exe).</summary>
internal static class ReleaseFiles
{
    public const string ShimContent = "RELEASE-SHIM-TEMPLATE";
    private static readonly object Gate = new();

    public static void EnsureNextToHost()
    {
        lock (Gate)
        {
            foreach (var (name, content) in new[] { ("wbrew.exe", "RELEASE-WBREW"), ("e_sqlite3.dll", "RELEASE-SQLITE"), ("wbrew-shim.exe", ShimContent) })
            {
                var path = Path.Combine(AppContext.BaseDirectory, name);
                if (!File.Exists(path) || File.ReadAllText(path) != content)
                {
                    var temp = path + "." + Guid.NewGuid().ToString("N");
                    File.WriteAllText(temp, content);
                    File.Move(temp, path, overwrite: true);
                }
            }
        }
    }
}

/// <summary>A disposable HKCU key standing in for HKCU\Environment (A16: never the real user PATH).</summary>
internal sealed class PathTestKey : IDisposable
{
    public PathTestKey()
    {
        SubKey = @"Software\WinBrew.Tests\" + Guid.NewGuid().ToString("N");
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(SubKey);
        }
    }

    public string SubKey { get; }

    public string? ReadPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Registry.CurrentUser.OpenSubKey(SubKey);
        return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(SubKey, throwOnMissingSubKey: false);
        }
    }
}

/// <summary>A14: <c>wbrew self install</c> for the zip layout.</summary>
public sealed class SelfInstallTests
{
    public const string Version = "0.1.0";

    /// <summary>
    /// After recovery: either fully installed (app\0.1.0\ with the three
    /// files, bin\wbrew.exe, sidecar → app\0.1.0\wbrew.exe) or fully rolled
    /// back (no app\0.1.0\, the previous self-shim sidecar restored, no
    /// binary we created) — and never a temp directory, staging leftover or
    /// pending operation.
    /// </summary>
    internal static void AssertSelfInstallConsistent(CliHarness h)
    {
        Assert.Empty(h.ListWarnings());
        Assert.Empty(h.StagingLeftovers());
        var appRoot = Path.Combine(h.Root, "app");
        Assert.DoesNotContain(Directory.GetDirectories(appRoot), d => Path.GetFileName(d).StartsWith('.'));

        var app = Path.Combine(appRoot, Version);
        var sidecar = ShimSidecar.TryParse(File.ReadAllBytes(Path.Combine(h.Bin, "wbrew.shim.json")));
        Assert.NotNull(sidecar);

        if (Directory.Exists(app))
        {
            foreach (var f in new[] { "wbrew.exe", "e_sqlite3.dll", "wbrew-shim.exe" })
            {
                Assert.True(File.Exists(Path.Combine(app, f)), f);
            }

            Assert.Equal(Path.Combine(app, "wbrew.exe"), sidecar.Target);
            Assert.Equal(ReleaseFiles.ShimContent, File.ReadAllText(Path.Combine(h.Bin, "wbrew.exe")));
        }
        else
        {
            Assert.EndsWith(Path.Combine("0.0.0-test", "wbrew.exe"), sidecar.Target, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(h.Bin, "wbrew.exe")), "rolled back, but bin\\wbrew.exe remains");
        }

        Assert.Empty(h.OldFiles());
    }

    [TestHooksFact]
    public void SelfInstall_CopiesTheZipSet_AndCreatesTheSelfShim()
    {
        using var h = new CliHarness().Ready();
        ReleaseFiles.EnsureNextToHost();

        var run = CliHarness.Expect(0, h.Run("self", "install", "--non-interactive", "--json"));
        var data = run.Json().GetProperty("data");
        Assert.True(data.GetProperty("copied").GetBoolean());
        Assert.False(data.GetProperty("pathChanged").GetBoolean());
        Assert.Contains(run.Json().GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("PATH not changed", StringComparison.Ordinal));

        AssertSelfInstallConsistent(h);
        Assert.True(Directory.Exists(Path.Combine(h.Root, "app", Version)));
        // No extraction directory anywhere in the layout (A14).
        Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(h.Root, "app")), d => Path.GetFileName(d).Contains(".tmp-", StringComparison.Ordinal));
    }

    [TestHooksFact]
    public void SelfInstall_IsIdempotent()
    {
        using var h = new CliHarness().Ready();
        ReleaseFiles.EnsureNextToHost();
        CliHarness.Expect(0, h.Run("self", "install", "--non-interactive"));
        var second = CliHarness.Expect(0, h.Run("self", "install", "--non-interactive", "--json"));
        Assert.False(second.Json().GetProperty("data").GetProperty("copied").GetBoolean());
        AssertSelfInstallConsistent(h);
    }

    [TestHooksFact]
    public void SelfInstall_RefusesToOverwriteADifferentInstalledCopy()
    {
        using var h = new CliHarness().Ready();
        ReleaseFiles.EnsureNextToHost();
        var app = Path.Combine(h.Root, "app", Version);
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "wbrew.exe"), "SOME OTHER BUILD");
        File.WriteAllText(Path.Combine(app, "e_sqlite3.dll"), "RELEASE-SQLITE");
        File.WriteAllText(Path.Combine(app, "wbrew-shim.exe"), ReleaseFiles.ShimContent);

        var run = CliHarness.Expect(6, h.Run("self", "install", "--non-interactive"));
        Assert.Contains("never overwrites a running version", run.StdErr, StringComparison.Ordinal);
        Assert.Equal("SOME OTHER BUILD", File.ReadAllText(Path.Combine(app, "wbrew.exe")));
    }

    [TestHooksFact]
    public void SelfInstall_ModifyPath_ChangesPathOnlyWhereSupported()
    {
        using var h = new CliHarness().Ready();
        using var hive = new PathTestKey(); // Windows: a disposable key, never the real user PATH
        ReleaseFiles.EnsureNextToHost();
        var env = new Dictionary<string, string?> { ["WINBREW_TEST_PATH_KEY"] = hive.SubKey };
        var run = CliHarness.Expect(0, h.RunWith(env, "self", "install", "--modify-path", "--json"));
        var changed = run.Json().GetProperty("data").GetProperty("pathChanged").GetBoolean();

        if (OperatingSystem.IsWindows())
        {
            Assert.True(changed);
            Assert.Equal(h.Bin, hive.ReadPath());
        }
        else
        {
            // Off-Windows PATH integration is reported, never faked.
            Assert.False(changed);
            Assert.Contains(run.Json().GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("needs Windows", StringComparison.Ordinal));
        }
    }

    [WindowsTestHooksFact("PATH integration writes HKCU (a disposable key via WINBREW_TEST_PATH_KEY)")]
    public void SelfInstall_ModifyPath_UsesTheTestHive()
    {
        using var h = new CliHarness().Ready();
        using var hive = new PathTestKey();
        ReleaseFiles.EnsureNextToHost();
        var env = new Dictionary<string, string?> { ["WINBREW_TEST_PATH_KEY"] = hive.SubKey };
        CliHarness.Expect(0, h.RunWith(env, "self", "install", "--modify-path"));
        Assert.Equal(h.Bin, hive.ReadPath());
        CliHarness.Expect(0, h.RunWith(env, "self", "install", "--modify-path"));
        Assert.Equal(h.Bin, hive.ReadPath()); // never a duplicate (A12)
        Assert.Equal("true", CliHarness.Expect(0, h.Run("config", "get", "path.managed")).StdOut.Trim().Split(" = ")[1]);
    }
}

/// <summary>A11: when recovery cannot reach a consistent state it says so — exit 8, RecoveryFailed, doctor Fail.</summary>
public sealed class RecoveryFailedTests
{
    /// <summary>Turns bin\hello.shim.json into a directory so restoring the previous sidecar must fail.</summary>
    internal static void SabotageSidecar(CliHarness h)
    {
        var sidecar = Path.Combine(h.Bin, "hello.shim.json");
        File.Delete(sidecar);
        Directory.CreateDirectory(sidecar);
        File.WriteAllText(Path.Combine(sidecar, "blocker"), "x");
    }

    [TestHooksFact]
    public void Recover_ThatCannotRestoreState_EndsInRecoveryFailed_Exit8()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        Assert.True(h.RunFault("shim.sidecar.replaced", "install", "hello@2.0.0", "--yes").FaultInjected);
        SabotageSidecar(h);

        var recover = CliHarness.Expect(8, h.Run("recover", "--json"));
        var error = recover.Json().GetProperty("error");
        Assert.Equal("PartialOrRecoveryRequired", error.GetProperty("name").GetString());
        Assert.Contains("hello", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        // Terminal and reported: doctor fails, read-only commands warn, nothing claims success.
        var doctor = h.Run("doctor");
        Assert.Equal(1, doctor.ExitCode);
        Assert.Contains("journal.recoveryFailed", doctor.StdOut, StringComparison.Ordinal);
        Assert.Contains(h.ListWarnings(), w => w.Contains("RecoveryFailed", StringComparison.Ordinal));
    }
}
