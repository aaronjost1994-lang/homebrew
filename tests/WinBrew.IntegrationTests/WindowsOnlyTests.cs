using System.Diagnostics;
using Microsoft.Win32;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Shims;
using WinBrew.Infrastructure.Files;
using WinBrew.TestFixtures;
using WinBrew.Windows;

namespace WinBrew.IntegrationTests;

/// <summary>A12 PATH editing against a disposable HKCU key (never the real user PATH).</summary>
public sealed class UserPathEditorTests : IDisposable
{
    private readonly PathTestKey _key = new();

    public void Dispose() => _key.Dispose();

    private RegistryUserPathEditor Editor() => new(_key.SubKey);

    [Fact]
    public async Task OffWindows_PathIntegrationIsUnsupported_AndSaysSo()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(Editor().IsSupported);
            return;
        }

        Assert.False(Editor().IsSupported);
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Editor().AddAsync(@"C:\WinBrew\bin", CancellationToken.None));
        Assert.Contains("needs Windows", ex.Message, StringComparison.Ordinal);
    }

    [WindowsFact("HKCU registry")]
    public async Task MissingValue_IsCreatedAsExpandString()
    {
        var result = await Editor().AddAsync(@"C:\WinBrew\bin", CancellationToken.None);
        Assert.True(result.Changed);
        using var key = Registry.CurrentUser.OpenSubKey(_key.SubKey)!;
        Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("Path"));
        Assert.Equal(@"C:\WinBrew\bin", _key.ReadPath());
    }

    [WindowsFact("HKCU registry")]
    public async Task ExistingEntries_KeepOrderKindAndRawForm_NoDuplicates()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(_key.SubKey, writable: true)!)
        {
            key.SetValue("Path", @"%USERPROFILE%\tools;C:\Other", RegistryValueKind.String);
        }

        await Editor().AddAsync(@"C:\WinBrew\bin", CancellationToken.None);
        var again = await Editor().AddAsync(@"c:\winbrew\BIN\", CancellationToken.None); // case/slash-insensitive
        Assert.True(again.AlreadyPresent);
        Assert.Equal(@"%USERPROFILE%\tools;C:\Other;C:\WinBrew\bin", _key.ReadPath()); // not expanded (A12)
        using var k = Registry.CurrentUser.OpenSubKey(_key.SubKey)!;
        Assert.Equal(RegistryValueKind.String, k.GetValueKind("Path"));              // kind kept
        Assert.Equal([@"%USERPROFILE%\tools", @"C:\Other"], Editor().EntriesBefore(@"C:\WinBrew\bin"));
    }

    [WindowsFact("HKCU registry")]
    public async Task Removal_DeletesOnlyTheExactEntry()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(_key.SubKey, writable: true)!)
        {
            key.SetValue("Path", @"C:\A;C:\WinBrew\bin;C:\WinBrew\bin2", RegistryValueKind.ExpandString);
        }

        Assert.True(await Editor().RemoveAsync(@"C:\WinBrew\bin", CancellationToken.None));
        Assert.Equal(@"C:\A;C:\WinBrew\bin2", _key.ReadPath());
        Assert.False(await Editor().RemoveAsync(@"C:\WinBrew\bin", CancellationToken.None));
    }

    [WindowsFact("HKCU registry")]
    public async Task LengthLimit_Refuses_Above32767_WarnsAbove2047()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(_key.SubKey, writable: true)!)
        {
            key.SetValue("Path", new string('x', 32760), RegistryValueKind.ExpandString);
        }

        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Editor().AddAsync(@"C:\WinBrew\bin", CancellationToken.None));
        Assert.Equal(WinBrewErrorCode.ConflictOrRefused, ex.Code);

        using (var key = Registry.CurrentUser.OpenSubKey(_key.SubKey, writable: true)!)
        {
            key.SetValue("Path", new string('y', 2040), RegistryValueKind.ExpandString);
        }

        Assert.NotNull((await Editor().AddAsync(@"C:\WinBrew\bin", CancellationToken.None)).Warning);
    }
}

/// <summary>
/// D4 against the REAL Native AOT shim (WINBREW_SHIM_EXE = published
/// wbrew-shim.exe). The target is a copy of cmd.exe inside packages\, so
/// exit codes, containment and the length limit are observed end to end.
/// Ctrl+C delivery and stdin/stdout piping are E2E items (Phase F).
/// </summary>
public sealed class ShimProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wbrew-shimproc-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Install(string name, string? target = null)
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.Copy(Environment.GetEnvironmentVariable("WINBREW_SHIM_EXE")!, Path.Combine(bin, name + ".exe"), overwrite: true);
        var pkg = Path.Combine(_root, "packages", "cmd", "1.0.0", "x64");
        Directory.CreateDirectory(pkg);
        var cmd = Path.Combine(pkg, "cmd.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmd, overwrite: true);
        File.WriteAllBytes(Path.Combine(bin, name + ShimSidecar.SidecarSuffix),
            new ShimSidecar(target ?? cmd, "local/cmd", "1.0.0", ShimSidecar.Console).ToUtf8Bytes());
        return Path.Combine(bin, name + ".exe");
    }

    private static (int Code, string Err) Run(string exe, string arguments, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var (k, v) in env ?? new Dictionary<string, string>())
        {
            psi.Environment[k] = v;
        }

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, err);
    }

    [WindowsArtifactFact("WINBREW_SHIM_EXE", "the published wbrew-shim.exe")]
    public void ExitCode_IsForwarded() => Assert.Equal(42, Run(Install("c"), "/c exit 42").Code);

    [WindowsArtifactFact("WINBREW_SHIM_EXE", "the published wbrew-shim.exe")]
    public void MissingTarget_Is9009()
    {
        var shim = Install("c", Path.Combine(_root, "packages", "nope", "x.exe"));
        Assert.Equal(9009, Run(shim, "").Code);
    }

    [WindowsArtifactFact("WINBREW_SHIM_EXE", "the published wbrew-shim.exe")]
    public void TargetOutsidePackages_Is9009()
    {
        var shim = Install("c", Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        var (code, err) = Run(shim, "/c exit 0");
        Assert.Equal(9009, code);
        Assert.Contains("not inside", err, StringComparison.Ordinal);
    }

    [WindowsArtifactFact("WINBREW_SHIM_EXE", "the published wbrew-shim.exe")]
    public void NonSelfShim_TargetingApp_Is9009_SelfShim_IsAccepted()
    {
        var app = Path.Combine(_root, "app", "1.0.0");
        Directory.CreateDirectory(app);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(app, "wbrew.exe"), overwrite: true);
        Assert.Equal(9009, Run(Install("c", Path.Combine(app, "wbrew.exe")), "/c exit 3").Code);
        Assert.Equal(3, Run(Install("wbrew", Path.Combine(app, "wbrew.exe")), "/c exit 3").Code);
    }

    [WindowsArtifactFact("WINBREW_SHIM_EXE", "the published wbrew-shim.exe")]
    public void WinBrewRoot_DoesNotMoveTheContainmentRoot()
    {
        var shim = Install("c");
        var env = new Dictionary<string, string> { ["WINBREW_ROOT"] = Path.Combine(Path.GetTempPath(), "elsewhere") };
        Assert.Equal(7, Run(shim, "/c exit 7", env).Code);
    }

    [WindowsArtifactFact("WINBREW_SHIM_EXE", "the published wbrew-shim.exe")]
    public void CommandLineOverflow_Is206()
    {
        // The arguments must FIT the shim's own command line ("<shim>" + ' ' + args,
        // at most 32,766 chars plus the terminating null, or CreateProcess refuses
        // to start the shim at all) and OVERFLOW the child's ("<target>" + ' ' +
        // args, the shim's ShimRules.ExceedsLimit). That window exists because the target
        // path (packages\cmd\1.0.0\x64\cmd.exe) is longer than the shim path (bin\c.exe).
        var shim = Install("c");
        var target = Path.Combine(_root, "packages", "cmd", "1.0.0", "x64", "cmd.exe");
        const int MaxCommandLineChars = 32767; // ShimRules.MaxCommandLineChars (the shim project is not referenced here)
        var argsLength = MaxCommandLineChars - 1 - (shim.Length + 3);
        Assert.True(target.Length + 3 + argsLength + 1 > MaxCommandLineChars, "the arguments must overflow the child's command line");
        var (code, err) = Run(shim, "/c " + new string('a', argsLength - 3));
        Assert.Equal(206, code);
        Assert.Contains("exceeds the Windows limit", err, StringComparison.Ordinal);
    }

    [WindowsArtifactFact("WINBREW_SHIM_EXE", "the published wbrew-shim.exe")]
    public async Task ReplacingARunningShim_UsesAJournaledOldFile_WithoutSharingViolation()
    {
        var shim = Install("c");
        using var running = Process.Start(new ProcessStartInfo(shim, "/c ping -n 6 127.0.0.1 >nul") { UseShellExecute = false })!;
        await Task.Delay(500);

        var store = new ShimStore(Path.Combine(_root, "bin"), new PackageFileSystem(new RestartManagerInspector()));
        var template = Path.Combine(_root, "new-template.exe");
        File.Copy(Environment.GetEnvironmentVariable("WINBREW_SHIM_EXE")!, template);
        File.AppendAllText(template, "x"); // different hash → must replace
        var journaled = new List<string>();
        var placement = await store.PlaceBinaryAsync("c", template, "0000", (p, _) => { journaled.Add(p); return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(placement.Placed);
        Assert.Equal(placement.OldPath, Assert.Single(journaled));
        Assert.True(File.Exists(placement.OldPath)); // the running image, renamed aside
        running.WaitForExit();
        Assert.Equal(0, running.ExitCode);
    }
}

/// <summary>
/// A4/A16/A17 extraction verification: the published single-file wbrew.exe
/// must never create an extraction directory under %TEMP% on a fresh
/// profile. WINBREW_PUBLISHED_EXE = the published wbrew.exe (next to
/// e_sqlite3.dll). The observed result is recorded in ADR-0001.
/// </summary>
public sealed class ExtractionVerificationTests
{
    [WindowsArtifactFact("WINBREW_PUBLISHED_EXE", "the published release wbrew.exe")]
    public void PublishedBinary_CreatesNoExtractionDirectory()
    {
        var fresh = Path.Combine(Path.GetTempPath(), "wbrew-fresh-" + Guid.NewGuid().ToString("N")[..8]);
        var temp = Path.Combine(fresh, "temp");
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var args in new[] { "--version", "config list", "doctor --json" })
            {
                var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("WINBREW_PUBLISHED_EXE")!, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.Environment["TEMP"] = temp;
                psi.Environment["TMP"] = temp;
                psi.Environment["WINBREW_ROOT"] = Path.Combine(fresh, "root");
                psi.Environment.Remove("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
                using var p = Process.Start(psi)!;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode is 0 or 1, $"wbrew {args} exited {p.ExitCode}");
            }

            Assert.Empty(Directory.GetFileSystemEntries(temp)); // in particular no %TEMP%\.net\
        }
        finally
        {
            Directory.Delete(fresh, recursive: true);
        }
    }
}
