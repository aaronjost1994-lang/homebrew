using System.Globalization;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Installers;
using WinBrew.Core.Manifests;

namespace WinBrew.Application.Installers;

/// <summary>Installer consents (A8): never implied by --yes.</summary>
/// <param name="AllowInstaller">--allow-installer: running an MSI/EXE.</param>
/// <param name="Scope">"user" (default) or "machine" (--scope machine: elevated helper).</param>
public sealed record InstallerOptions(bool AllowInstaller, string Scope)
{
    public static readonly InstallerOptions None = new(false, "user");
}

/// <summary>An MSI/EXE install request from the plan executor (artifact already downloaded and SHA-256 verified into the cache).</summary>
public sealed record InstallerJob(string Repo, string Id, string Version, string Arch, Manifest Manifest, string ArtifactPath, string ArtifactSha256, InstallerOptions Options);

/// <summary>Waits between 1618 retries (injected so tests do not sleep).</summary>
public delegate Task InstallerDelay(TimeSpan delay, CancellationToken ct);

/// <summary>
/// MSI/EXE installs and uninstalls (v1.1, A12). Per-user operations run
/// msiexec/the EXE directly (never elevated) from a staging copy that is
/// held open with FileShare.Read and hashed from that handle; per-machine
/// operations go through WinBrew.ElevatedHelper with a typed request. Exit
/// codes follow <see cref="MsiRules"/> (1618 retried, then 13). After an
/// install the actual context is confirmed (MsiGetProductInfoEx) or the EXE
/// detection rules are evaluated; ambiguity is state Unknown + exit 8.
/// Rollback is NOT promised for MSI/EXE operations.
/// </summary>
public sealed class InstallerService(
    IInstallerStore store,
    IInstallerRunner runner,
    IElevatedRunner elevated,
    IMsiProducts msi,
    IInstallProbe probe,
    IStorageLayout layout,
    IPackageFileSystem files,
    TimeProvider time,
    InstallerDelay? delay = null)
{
    private readonly InstallerDelay _delay = delay ?? ((d, ct) => Task.Delay(d, ct));

    public async Task<IReadOnlyList<string>> InstallAsync(InstallerJob job, CancellationToken ct)
    {
        var manifest = job.Manifest;
        var declaration = manifest.Installer
            ?? throw new WinBrewException(WinBrewErrorCode.VerificationFailure, $"{job.Id}@{job.Version} has no installer declaration.");
        var scope = CheckConsentAndScope(job.Repo, job.Id, manifest.Scope, job.Options);
        var warnings = new List<string>();

        var existing = await store.GetInstallerPackageAsync(job.Repo, job.Id, ct);
        if (existing is { State: "Installed" } && existing.Version == job.Version && existing.Scope == scope)
        {
            return [$"{job.Repo}/{job.Id} {job.Version} is already installed ({scope})."];
        }

        var (exitCode, logPath) = scope == "machine"
            ? await RunMachineInstallAsync(job, declaration, ct)
            : await RunUserInstallAsync(job, declaration, ct);

        var outcome = declaration.Type == "msi" ? MsiRules.Map(exitCode) : MsiRules.MapExe(exitCode);
        ThrowOnFailure(outcome, exitCode, logPath, $"{job.Repo}/{job.Id} {job.Version}");

        // Confirm what actually happened (A12) — never assume.
        string state = "Installed";
        string recordedScope = scope;
        if (declaration.Type == "msi")
        {
            var context = msi.GetInstalledContext(declaration.Msi!.ProductCode);
            if (context is null)
            {
                state = "Unknown";
            }
            else if (context != scope)
            {
                recordedScope = context;
                warnings.Add($"The MSI installed per-{context}, not per-{scope}; the actual context is recorded (A12).");
            }
        }
        else if (Detect(declaration.Exe!.Detection) != true)
        {
            state = "Unknown";
        }

        if (state == "Installed" && outcome is InstallerOutcome.RebootRequired or InstallerOutcome.RebootStarted)
        {
            state = "RebootRequired";
        }

        var now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        await store.UpsertInstallerPackageAsync(new InstallerPackage(job.Repo, job.Id, job.Version, job.Arch, declaration.Type, recordedScope,
            declaration.Msi?.ProductCode, declaration.Msi?.UpgradeCode, state, logPath, InstallerDeclarationJson.Serialize(declaration),
            existing?.InstalledAt ?? now, now), ct);

        if (state == "Unknown")
        {
            throw new WinBrewException(WinBrewErrorCode.PartialOrRecoveryRequired,
                $"The {declaration.Type.ToUpperInvariant()} installer for {job.Repo}/{job.Id} reported exit {exitCode}, but its result cannot be confirmed " +
                $"(detection is ambiguous); state recorded as Unknown. Log: {logPath} (A12).",
                new Dictionary<string, object?> { ["state"] = state, ["log"] = logPath });
        }

        if (state == "RebootRequired")
        {
            throw new WinBrewException(WinBrewErrorCode.RebootRequired,
                $"{job.Repo}/{job.Id} {job.Version} was installed; a reboot is required{(outcome == InstallerOutcome.RebootStarted ? " and was started" : "")} (A12).",
                new Dictionary<string, object?> { ["log"] = logPath });
        }

        return warnings;
    }

    public async Task<IReadOnlyList<string>> UninstallAsync(InstallerPackage package, InstallerOptions options, CancellationToken ct)
    {
        if (!options.AllowInstaller)
        {
            throw ConsentRequired(package.Repo, package.Id);
        }

        RequireSupported();
        var declaration = InstallerDeclarationJson.Parse(package.InstallerJson);
        int exitCode;
        string? logPath;
        if (declaration.Type == "msi")
        {
            var productCode = package.ProductCode ?? declaration.Msi!.ProductCode;
            if (package.Scope == "machine")
            {
                (exitCode, logPath) = await RunElevatedAsync(new ElevatedRequest("1", ElevatedProtocol.MsiUninstall, ProductCode: productCode), ct);
            }
            else
            {
                logPath = LogPath(package.Id, "uninstall");
                exitCode = await MsiRules.RunWithBusyRetriesAsync(t => runner.RunMsiexecAsync(MsiRules.UninstallArguments(productCode, logPath), t),
                    (d, t) => _delay(d, t), ct);
            }
        }
        else
        {
            var command = declaration.Exe!.SilentUninstall;
            var path = probe.Expand(command.Path);
            if (path is null || !probe.GetFile(path).Exists)
            {
                throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                    $"The uninstaller '{command.Path}' of {package.Repo}/{package.Id} does not exist here; WinBrew never runs registry " +
                    "UninstallString values or a shell, so it cannot uninstall this package safely (A12).");
            }

            logPath = null;
            exitCode = package.Scope == "machine"
                ? (await RunElevatedAsync(new ElevatedRequest("1", ElevatedProtocol.ExeUninstall, ExeUninstall: command), ct)).ExitCode
                : await runner.RunExecutableAsync(path, command.Args, ct);
        }

        var outcome = declaration.Type == "msi" ? MsiRules.Map(exitCode) : MsiRules.MapExe(exitCode);
        ThrowOnFailure(outcome, exitCode, logPath, $"uninstall of {package.Repo}/{package.Id}");

        var gone = declaration.Type == "msi"
            ? msi.QueryState(package.ProductCode ?? declaration.Msi!.ProductCode) == MsiProductState.Absent
            : Detect(declaration.Exe!.Detection) == false;
        if (!gone)
        {
            await store.UpsertInstallerPackageAsync(package with { State = "Unknown", LogPath = logPath, UpdatedAt = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) }, ct);
            throw new WinBrewException(WinBrewErrorCode.PartialOrRecoveryRequired,
                $"The uninstaller of {package.Repo}/{package.Id} exited {exitCode}, but the product is still detected; state recorded as Unknown (A12).");
        }

        await store.DeleteInstallerPackageAsync(package.Repo, package.Id, ct);
        if (outcome is InstallerOutcome.RebootRequired or InstallerOutcome.RebootStarted)
        {
            throw new WinBrewException(WinBrewErrorCode.RebootRequired, $"{package.Repo}/{package.Id} was removed; a reboot is required (A12).");
        }

        return [];
    }

    /// <summary>
    /// Explicit reconciliation for list/doctor (A12): compares the record with
    /// MsiQueryProductState / the detection rules. Returns the drift, or null.
    /// NEVER corrects state.
    /// </summary>
    public string? Reconcile(InstallerPackage package)
    {
        if (!runner.IsSupported)
        {
            return null;
        }

        var declaration = InstallerDeclarationJson.Parse(package.InstallerJson);
        var name = $"{package.Repo}/{package.Id}";
        if (package.State == "Unknown")
        {
            return $"{name}: state is Unknown (the last installer run could not be confirmed).";
        }

        if (declaration.Type == "msi")
        {
            var state = msi.QueryState(package.ProductCode ?? declaration.Msi!.ProductCode);
            return state switch
            {
                MsiProductState.Installed => null,
                MsiProductState.Absent => $"{name}: ProductCode {package.ProductCode} is no longer installed (removed outside WinBrew?).",
                _ => $"{name}: Windows Installer reports state {state} for {package.ProductCode}.",
            };
        }

        return Detect(declaration.Exe!.Detection) switch
        {
            true => null,
            false => $"{name}: the detection rules no longer find it (removed outside WinBrew?).",
            _ => $"{name}: the detection rules disagree (ambiguous).",
        };
    }

    /// <summary>true = every declared rule matches; false = none matches; null = ambiguous.</summary>
    public bool? Detect(ExeDetection detection)
    {
        var results = new List<bool>();
        if (detection.UninstallKey is { } key)
        {
            results.Add(probe.UninstallKeyExists(key));
        }

        if (detection.File is { } rule)
        {
            var path = probe.Expand(rule.Path);
            var (exists, version) = path is null ? (false, null) : probe.GetFile(path);
            results.Add(exists && (rule.Version is null || string.Equals(version, rule.Version, StringComparison.Ordinal)));
        }

        return results.All(r => r) ? true : results.All(r => !r) ? false : null;
    }

    /// <summary>A8/A12 consents and scope, checked BEFORE anything is downloaded. Returns the scope to use.</summary>
    public string CheckConsentAndScope(string repo, string id, IReadOnlyList<string> manifestScopes, InstallerOptions options)
    {
        if (!options.AllowInstaller)
        {
            throw ConsentRequired(repo, id);
        }

        var scope = options.Scope;
        if (!manifestScopes.Contains(scope, StringComparer.Ordinal))
        {
            throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                $"{repo}/{id} declares scope [{string.Join(", ", manifestScopes)}]; " +
                (scope == "user" ? "install it with --scope machine (per-machine, elevated)." : "it cannot be installed per-machine.") + " (A12)");
        }

        RequireSupported();
        return scope;
    }

    private void RequireSupported()
    {
        if (!runner.IsSupported)
        {
            throw new WinBrewException(WinBrewErrorCode.GeneralFailure, "MSI/EXE installers need Windows (A12).");
        }
    }

    private async Task<(int ExitCode, string LogPath)> RunUserInstallAsync(InstallerJob job, InstallerDeclaration declaration, CancellationToken ct)
    {
        // A12: run a private staging copy, held open with FileShare.Read and hashed from that handle.
        var work = Path.Combine(layout.Directories["staging"], "installer-" + Guid.NewGuid().ToString("N"));
        await files.CreateDirectoryAsync(work, ct);
        try
        {
            var copy = Path.Combine(work, $"{job.Id}-{job.Version}.{declaration.Type}");
            await files.CopyFileAsync(job.ArtifactPath, copy, overwrite: false, ct);
            var held = await runner.HoldAsync(copy, ct);
            using (held.Handle)
            {
                if (held.Sha256 != job.ArtifactSha256)
                {
                    throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                        $"The staged installer's SHA-256 {held.Sha256} differs from the verified artifact {job.ArtifactSha256} (A12).");
                }

                var logPath = LogPath(job.Id, "install");
                var code = declaration.Type == "msi"
                    ? await MsiRules.RunWithBusyRetriesAsync(
                        t => runner.RunMsiexecAsync(MsiRules.InstallArguments(held.Path, logPath, MsiRules.PropertiesFor(declaration.Msi!, "user")), t),
                        (d, t) => _delay(d, t), ct)
                    : await runner.RunExecutableAsync(held.Path, declaration.Exe!.SilentInstallArgs, ct);
                return (code, logPath);
            }
        }
        finally
        {
            await files.DeleteDirectoryAsync(work, CancellationToken.None);
        }
    }

    /// <summary>
    /// The cache is content-addressed (no extension); the helper takes a .msi/.exe source, so a
    /// named copy is staged next to the request. The helper copies it AGAIN into its protected
    /// &lt;op&gt; directory and verifies the hash from its own held handle before anything runs.
    /// </summary>
    private async Task<(int ExitCode, string LogPath)> RunMachineInstallAsync(InstallerJob job, InstallerDeclaration declaration, CancellationToken ct)
    {
        var directory = Path.Combine(layout.Directories["staging"], "elevated");
        await files.CreateDirectoryAsync(directory, ct);
        var source = Path.Combine(directory, $"artifact-{Guid.NewGuid():N}.{declaration.Type}");
        await files.CopyFileAsync(job.ArtifactPath, source, overwrite: false, ct);
        try
        {
            return await RunElevatedAsync(declaration.Type == "msi"
                ? new ElevatedRequest("1", ElevatedProtocol.MsiInstall, source, job.ArtifactSha256,
                    MsiProperties: MsiRules.PropertiesFor(declaration.Msi!, "machine"))
                : new ElevatedRequest("1", ElevatedProtocol.ExeInstall, source, job.ArtifactSha256,
                    ExeArgs: declaration.Exe!.SilentInstallArgs), ct);
        }
        finally
        {
            await files.TryDeleteFileAsync(source, CancellationToken.None);
        }
    }

    private async Task<(int ExitCode, string LogPath)> RunElevatedAsync(ElevatedRequest request, CancellationToken ct)
    {
        var result = await elevated.RunAsync(request, ct);
        if (result.Error is not null || result.ExitCode is null)
        {
            throw new WinBrewException(WinBrewErrorCode.GeneralFailure,
                $"The elevated helper refused or failed: {result.Error ?? "no exit code"} (A12).",
                new Dictionary<string, object?> { ["log"] = result.LogPath });
        }

        return (result.ExitCode.Value, result.LogPath ?? "");
    }

    private string LogPath(string id, string action) =>
        Path.Combine(layout.Directories["logs"], $"installer-{id}-{action}-{time.GetUtcNow():yyyyMMddHHmmss}.log");

    private static void ThrowOnFailure(InstallerOutcome outcome, int exitCode, string? logPath, string what)
    {
        switch (outcome)
        {
            case InstallerOutcome.Cancelled:
                throw new WinBrewException(WinBrewErrorCode.Cancelled, $"The installer for {what} was cancelled (exit {exitCode}).");
            case InstallerOutcome.Busy:
                throw new WinBrewException(WinBrewErrorCode.InstallerBusy,
                    $"Another installation is in progress (msiexec 1618) after {MsiRules.BusyRetries} retries; try {what} again later (D9: 13).",
                    new Dictionary<string, object?> { ["msiexec"] = exitCode, ["log"] = logPath });
            case InstallerOutcome.Failed:
                throw new WinBrewException(WinBrewErrorCode.GeneralFailure,
                    $"The installer for {what} failed with exit {exitCode}. Log: {logPath ?? "(none)"} (A12).",
                    new Dictionary<string, object?> { ["installerExitCode"] = exitCode, ["log"] = logPath });
        }
    }

    private static WinBrewException ConsentRequired(string repo, string id) =>
        new(WinBrewErrorCode.ConsentRequired,
            $"{repo}/{id} runs an MSI/EXE installer — executable code chosen by the repository. Re-run with --allow-installer to consent; " +
            "--yes never implies it (A8, A12).");
}
