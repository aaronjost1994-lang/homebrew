using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Installers;

namespace WinBrew.Windows.Installers;

/// <summary>
/// Runs msiexec / EXE installers (A12): %SystemRoot%\System32\msiexec.exe or
/// the held artifact, through <see cref="ProcessStartInfo.ArgumentList"/> with
/// UseShellExecute = false. No shell; no string is concatenated into a
/// command line.
/// </summary>
public sealed class WindowsInstallerRunner : IInstallerRunner
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public Task<HeldArtifact> HoldAsync(string path, CancellationToken ct)
    {
        // FileShare.Read ONLY: nobody can write, rename or delete it while it is held (A12).
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
            return Task.FromResult(new HeldArtifact(path, sha, stream));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public Task<int> RunMsiexecAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunExecutableAsync(MsiexecPath, arguments, ct);

    public async Task<int> RunExecutableAsync(string path, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = path, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new WinBrewException(WinBrewErrorCode.GeneralFailure, $"Cannot start '{path}'.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    public static string MsiexecPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
}

/// <summary>Windows Installer queries through msi.dll (A12). Read-only.</summary>
public sealed class MsiProducts : IMsiProducts
{
    private const int InstallStateAbsent = 2;
    private const int InstallStateDefault = 5;
    private const int InstallStateAdvertised = 1;
    private const int InstallStateUnknown = -1;
    private const int ContextUserUnmanaged = 2;
    private const int ContextMachine = 4;
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;

    public bool IsSupported => OperatingSystem.IsWindows();

    public MsiProductState QueryState(string productCode)
    {
        if (!IsSupported)
        {
            return MsiProductState.Unknown;
        }

        return MsiQueryProductStateW(productCode) switch
        {
            InstallStateDefault => MsiProductState.Installed,
            InstallStateAdvertised => MsiProductState.Advertised,
            InstallStateAbsent or InstallStateUnknown => MsiProductState.Absent,
            _ => MsiProductState.Unknown,
        };
    }

    public string? GetInstalledContext(string productCode)
    {
        if (!IsSupported)
        {
            return null;
        }

        if (HasProduct(productCode, ContextUserUnmanaged))
        {
            return "user";
        }

        return HasProduct(productCode, ContextMachine) ? "machine" : null;
    }

    private static bool HasProduct(string productCode, int context)
    {
        // Querying a property's length is enough: success (or more data) means the product exists in that context.
        var length = 0;
        var result = MsiGetProductInfoExW(productCode, null, context, "VersionString", IntPtr.Zero, ref length);
        return result is ErrorSuccess or ErrorMoreData;
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiQueryProductStateW(string product);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiGetProductInfoExW(string productCode, string? userSid, int context, string property, IntPtr value, ref int length);
}

/// <summary>EXE detection: Uninstall registry keys (HKCU + HKLM, 64- and 32-bit views) and file versions (A12).</summary>
public sealed class WindowsInstallProbe : IInstallProbe
{
    private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public string? Expand(string pattern)
    {
        if (!OperatingSystem.IsWindows() || InstallerPaths.Split(pattern, out _) is not { } split)
        {
            return null;
        }

        var root = split.Root switch
        {
            "{ProgramFiles}" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "{ProgramFilesX86}" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "{ProgramData}" => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "{LocalAppData}" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "{AppData}" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            _ => "",
        };
        return root.Length == 0 ? null : Path.Combine([root, .. split.Segments]);
    }

    public bool UninstallKeyExists(string keyName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(UninstallRoot + "\\" + keyName);
                if (key is not null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public (bool Exists, string? Version) GetFile(string path)
    {
        if (!File.Exists(path))
        {
            return (false, null);
        }

        var info = FileVersionInfo.GetVersionInfo(path);
        return (true, info.FileMajorPart + info.FileMinorPart + info.FileBuildPart + info.FilePrivatePart == 0
            ? null
            : $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}");
    }
}

/// <summary>
/// Starts WinBrew.ElevatedHelper.exe once per elevated operation (A12): the
/// request file is written under staging\elevated\ and its path is the
/// helper's SOLE argument (ShellExecute "runas"; the elevation prompt cannot be
/// reached without the shell's runas verb — this is the only UseShellExecute
/// call in WinBrew and it passes one argument through ArgumentList).
/// </summary>
public sealed class ElevatedHelperRunner(IStorageLayout layout, ISelfLocation self) : IElevatedRunner
{
    public const string HelperFileName = "WinBrew.ElevatedHelper.exe";
    private const int ErrorCancelled = 1223;

    public async Task<ElevatedResult> RunAsync(ElevatedRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new WinBrewException(WinBrewErrorCode.GeneralFailure, "Elevated installer operations need Windows (A12).");
        }

        var helper = Path.Combine(self.ExecutableDirectory, HelperFileName);
        if (!File.Exists(helper))
        {
            throw new WinBrewException(WinBrewErrorCode.GeneralFailure, $"{HelperFileName} is missing next to wbrew ({self.ExecutableDirectory}).");
        }

        var directory = layout.Directories["staging.elevated"];
        Directory.CreateDirectory(directory);
        var requestPath = Path.Combine(directory, $"request-{Guid.NewGuid():N}.json");
        var resultPath = ElevatedProtocol.ResultPathFor(requestPath);
        await File.WriteAllBytesAsync(requestPath, ElevatedProtocol.Serialize(request), ct);
        try
        {
            var psi = new ProcessStartInfo { FileName = helper, UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add(requestPath);
            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                throw new WinBrewException(WinBrewErrorCode.Cancelled, "The elevation prompt was declined (A12).");
            }

            using (process)
            {
                if (process is null)
                {
                    throw new WinBrewException(WinBrewErrorCode.GeneralFailure, "The elevated helper did not start.");
                }

                await process.WaitForExitAsync(ct);
            }

            if (!File.Exists(resultPath))
            {
                return new ElevatedResult("1", null, null, null, "the helper wrote no result");
            }

            return ElevatedProtocol.ParseResult(await File.ReadAllBytesAsync(resultPath, ct));
        }
        finally
        {
            File.Delete(requestPath);
            File.Delete(resultPath);
        }
    }
}
