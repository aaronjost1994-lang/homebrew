using WinBrew.Application.Lifecycle;
using WinBrew.Application.Ports;
using WinBrew.Core;

namespace WinBrew.Application.SelfManagement;

public sealed record SelfUninstallResult(
    bool KeepData,
    bool PathRemoved,
    IReadOnlyList<string> ShimsRemoved,
    bool DataRemoved,
    IReadOnlyList<string> RemoveManually,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings);

/// <summary>
/// <c>wbrew self uninstall [--keep-data]</c> (A14): removes the PATH entry
/// (when WinBrew manages it) and every shim, and — unless --keep-data —
/// packages, cache, staging, logs, config and state. It refuses (exit 6)
/// while v1.1 installer-managed packages are installed. The running
/// executable's directory cannot be deleted, so its path — and anything
/// still in use — is reported for manual removal. No shell tricks. Every
/// step is idempotent: re-running finishes an interrupted uninstall.
/// </summary>
public sealed class SelfUninstallUseCase(LifecycleServices s, MutationSession session, IStateDatabase database, IInstallerStore installerStore)
{
    public async Task<SelfUninstallResult> ExecuteAsync(bool keepData, Func<CancellationToken, Task<bool>> approve, CancellationToken ct)
    {
        if (!await approve(ct))
        {
            throw new OperationCanceledException("Removing WinBrew was not approved.");
        }

        var warnings = new List<string>();
        var manual = new List<string>();
        var shims = new List<string>();
        bool pathRemoved;

        using (var scope = await session.BeginAsync("self uninstall", ct))
        {
            // A14 refusal (v1.1): installer-managed (MSI/EXE) packages cannot be removed by deleting files.
            var managed = await installerStore.ListInstallerPackagesAsync(ct);
            if (managed.Count > 0)
            {
                var names = managed.Select(p => $"{p.Repo}/{p.Id} ({p.InstallType}, {p.Scope})").ToList();
                throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                    $"Installer-managed packages are still installed: {string.Join(", ", names)}. Uninstall them first " +
                    "(wbrew uninstall <package> --allow-installer); WinBrew is not removed (A14).",
                    new Dictionary<string, object?> { ["installerPackages"] = names });
            }

            var bin = s.Shims.BinDirectory;
            pathRemoved = scope.Settings.PathManaged && s.UserPath.IsSupported && await s.UserPath.RemoveAsync(bin, ct);

            foreach (var name in s.Shims.ListBinaryNames().Order(StringComparer.OrdinalIgnoreCase))
            {
                await s.Shims.DeleteSidecarAsync(name, ct);
                await s.Shims.RemoveBinaryAsync(name, (path, _) =>
                {
                    manual.Add(path); // a running shim was renamed aside; it cannot be deleted while it runs
                    return Task.CompletedTask;
                }, ct);
                shims.Add(name);
            }

            if (!keepData)
            {
                foreach (var key in new[] { "packages", "cache", "staging", "repositories", "logs" })
                {
                    if (s.Layout.Directories.TryGetValue(key, out var dir) && s.Files.DirectoryExists(dir))
                    {
                        await s.Files.DeleteDirectoryAsync(dir, ct);
                    }
                }

                var config = Path.Combine(s.Layout.Root, "config.json");
                await s.Files.TryDeleteFileAsync(config, ct);
            }
            else
            {
                warnings.Add("--keep-data: packages, cache and state were kept; after reinstalling WinBrew, " +
                             "'wbrew doctor' lists the shims that need recreating.");
            }
        }

        // The state database goes last, after the session released its connections.
        if (!keepData)
        {
            await database.DeleteDatabaseAsync(ct);
        }

        // app\<ver>\: every version except the running one; bin\ and the root if they are now empty.
        var running = Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.Self.ExecutableDirectory));
        foreach (var dir in s.Files.ListDirectories(s.Layout.Directories["app"]).Select(n => Path.Combine(s.Layout.Directories["app"], n)))
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)), running, StringComparison.OrdinalIgnoreCase))
            {
                manual.Add(dir);
                continue;
            }

            await s.Files.DeleteDirectoryAsync(dir, ct);
        }

        manual.Add(s.Layout.Root);
        warnings.Add("Remove these paths manually once WinBrew is no longer running: " + string.Join(", ", manual.Distinct(StringComparer.OrdinalIgnoreCase)) + " (A14).");
        return new SelfUninstallResult(keepData, pathRemoved, shims, !keepData, manual.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), warnings);
    }
}
