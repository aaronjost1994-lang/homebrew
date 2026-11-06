using System.Runtime.Versioning;
using System.Security.Cryptography;
using WinBrew.Core;
using WinBrew.Core.Installers;
using WinBrew.Windows.Installers;

namespace WinBrew.ElevatedHelper;

/// <summary>
/// WinBrew.ElevatedHelper (A12, v1.1). Sole argument: the path of a typed
/// request file (request-&lt;32 hex&gt;.json in a directory named "elevated").
/// The request is UNTRUSTED input: strictly validated; it never supplies a
/// command line, a script or an executable to run. The helper:
/// <list type="number">
/// <item>trusts %ProgramData%\WinBrew and \elevated only if it created them or
/// they pass the owner + DACL + reparse checks on an opened handle;</item>
/// <item>creates a random &lt;op&gt; directory with its final protected descriptor;</item>
/// <item>copies the artifact in, opens the copy with FileShare.Read only, hashes it
/// from that handle, compares it with the request, and keeps the handle open
/// while msiexec / the EXE runs;</item>
/// <item>writes a typed result next to the request and deletes &lt;op&gt;.</item>
/// </list>
/// Rollback is not promised for MSI/EXE operations.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: WinBrew.ElevatedHelper <request file>");
            return 2;
        }

        var requestPath = Path.GetFullPath(args[0]);
        if (!ElevatedProtocol.RequestFileNameRegex().IsMatch(Path.GetFileName(requestPath)) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(requestPath)), "elevated", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("The request file must be staging\\elevated\\request-<id>.json (A12).");
            return 2;
        }

        ElevatedResult result;
        try
        {
            result = RunAsync(requestPath).GetAwaiter().GetResult();
        }
        catch (WinBrewException ex)
        {
            result = new ElevatedResult("1", null, null, null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            result = new ElevatedResult("1", null, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }

        File.WriteAllBytes(ElevatedProtocol.ResultPathFor(requestPath), ElevatedProtocol.Serialize(result));
        return result.Error is null ? 0 : 1;
    }

    private static async Task<ElevatedResult> RunAsync(string requestPath)
    {
        byte[] bytes;
        using (var stream = new FileStream(requestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > ElevatedProtocol.MaxRequestBytes)
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure, "The request file is too large (A12).");
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        var request = ElevatedProtocol.Parse(bytes);

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinBrew");
        ProtectedDirectories.EnsureTrustedOrCreate(root);
        var elevated = Path.Combine(root, "elevated");
        ProtectedDirectories.EnsureTrustedOrCreate(elevated);
        var op = ProtectedDirectories.CreateOperationDirectory(elevated); // name generated here, never from the request

        var log = Path.Combine(op, "installer.log");
        var userLog = ElevatedProtocol.LogPathFor(requestPath);
        try
        {
            var runner = new WindowsInstallerRunner();
            int exitCode;
            switch (request.Operation)
            {
                case ElevatedProtocol.MsiInstall:
                case ElevatedProtocol.ExeInstall:
                    var extension = request.Operation == ElevatedProtocol.MsiInstall ? ".msi" : ".exe";
                    var copy = Path.Combine(op, "package" + extension);
                    CopyIn(request.ArtifactPath!, copy);
                    using (var held = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(held));
                        if (sha != request.ArtifactSha256)
                        {
                            throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                                $"The artifact's SHA-256 is {sha}, the request says {request.ArtifactSha256}; nothing was run (A12).");
                        }

                        // The handle stays open (no write/delete sharing) while the installer runs.
                        exitCode = request.Operation == ElevatedProtocol.MsiInstall
                            ? await MsiRules.RunWithBusyRetriesAsync(
                                ct => runner.RunMsiexecAsync(MsiRules.InstallArguments(copy, log, request.MsiProperties ?? new Dictionary<string, string>()), ct),
                                Task.Delay, CancellationToken.None)
                            : await runner.RunExecutableAsync(copy, request.ExeArgs!, CancellationToken.None);
                    }

                    break;
                case ElevatedProtocol.MsiUninstall:
                    exitCode = await MsiRules.RunWithBusyRetriesAsync(
                        ct => runner.RunMsiexecAsync(MsiRules.UninstallArguments(request.ProductCode!, log), ct), Task.Delay, CancellationToken.None);
                    break;
                default:
                    var path = new WindowsInstallProbe().Expand(request.ExeUninstall!.Path);
                    if (path is null || !File.Exists(path))
                    {
                        throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused, $"The uninstaller '{request.ExeUninstall.Path}' does not exist (A12).");
                    }

                    exitCode = await runner.RunExecutableAsync(path, request.ExeUninstall.Args, CancellationToken.None);
                    break;
            }

            string? logPath = null;
            if (File.Exists(log))
            {
                File.Copy(log, userLog, overwrite: true);
                logPath = userLog;
            }

            return new ElevatedResult("1", exitCode, logPath, request.Operation.StartsWith("msi", StringComparison.Ordinal) ? request.ProductCode : null, null);
        }
        finally
        {
            Directory.Delete(op, recursive: true); // the <op> directory this helper created
        }
    }

    /// <summary>Copies from a source opened with FileShare.Read (no writer can change it mid-copy) into a NEW file.</summary>
    private static void CopyIn(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }
}
