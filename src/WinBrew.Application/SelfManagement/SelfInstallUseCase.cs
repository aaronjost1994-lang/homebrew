using WinBrew.Application.Lifecycle;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Operations;
using WinBrew.Core.Shims;

namespace WinBrew.Application.SelfManagement;

public sealed record SelfInstallRequest(bool ModifyPath);

public sealed record SelfInstallResult(
    string Version,
    string AppDirectory,
    string SelfShim,
    bool Copied,
    bool PathChanged,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings);

/// <summary>Asks whether to add bin\ to the user PATH; null = no prompt possible (non-interactive).</summary>
public delegate Task<bool?> PathConsent(string binDirectory, CancellationToken ct);

/// <summary>
/// <c>wbrew self install [--modify-path]</c> for the ZIP layout (A14):
/// copies wbrew.exe, e_sqlite3.dll and wbrew-shim.exe into app\&lt;ver&gt;\
/// (via a temporary directory + one rename — no extraction directory
/// anywhere), creates bin\wbrew.exe as a copy of the template with the
/// self-shim sidecar pointing at app\&lt;ver&gt;\wbrew.exe, initializes state
/// and offers the PATH change. The PATH is changed only with consent
/// (--modify-path or an interactive yes; --yes never implies it, A8).
/// </summary>
public sealed class SelfInstallUseCase(LifecycleServices s, MutationSession session, RecoveryService recovery, IVersionInfo version)
{
    /// <summary>The release-zip set (scripts/publish-files.txt minus *.pdb, plus the AOT shim, A4/A14).</summary>
    public static readonly IReadOnlyList<string> RequiredFiles = ["wbrew.exe", "e_sqlite3.dll", LifecyclePaths.ShimTemplateFileName];

    /// <summary>Shipped in the release zip and copied when present: the v1.1 elevated helper (A12). Only per-machine installer operations need it.</summary>
    public static readonly IReadOnlyList<string> OptionalFiles = ["WinBrew.ElevatedHelper.exe"];

    /// <summary>The files to copy from <paramref name="source"/>: every required file plus the optional ones that exist.</summary>
    public static IEnumerable<string> FilesToCopy(Ports.IPackageFileSystem files, string source) =>
        RequiredFiles.Concat(OptionalFiles.Where(f => files.FileExists(Path.Combine(source, f))));

    public async Task<SelfInstallResult> ExecuteAsync(SelfInstallRequest request, PathConsent consent, CancellationToken ct)
    {
        var source = Path.GetFullPath(s.Self.ExecutableDirectory);
        var missing = RequiredFiles.Where(f => !s.Files.FileExists(Path.Combine(source, f))).ToList();
        if (missing.Count > 0)
        {
            throw new WinBrewException(WinBrewErrorCode.GeneralFailure,
                $"self install must run from the extracted release zip; missing next to wbrew: {string.Join(", ", missing)} (A14).",
                new Dictionary<string, object?> { ["missing"] = missing, ["directory"] = source });
        }

        var ver = AppVersionName(version.Version);
        using var scope = await session.BeginAsync("self install", ct);
        var warnings = new List<string>();

        var appDir = LifecyclePaths.AppDirectory(s.Layout, ver);
        var bin = s.Shims.BinDirectory;
        var runningFromApp = string.Equals(Path.TrimEndingDirectorySeparator(source), Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDir)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        // PATH consent is decided BEFORE anything changes (A8: missing consent never half-applies).
        var modifyPath = request.ModifyPath;
        if (!modifyPath && !scope.Settings.PathManaged)
        {
            var answer = await consent(bin, ct);
            if (answer is null)
            {
                warnings.Add($"PATH not changed (non-interactive). Re-run with --modify-path to add {bin} to the user PATH (A12).");
            }

            modifyPath = answer == true;
        }

        if (modifyPath && !s.UserPath.IsSupported)
        {
            warnings.Add("PATH integration needs Windows (HKCU\\Environment); the PATH was not changed.");
        }

        var op = await JournaledOperation.StartAsync(s.Journal, s.Faults, OperationType.SelfInstall, "self install", ct);
        var intent = new SelfInstallIntent(ver, appDir,
            Path.Combine(s.Layout.Directories["app"], $".{ver}.tmp-{op.OpId}"),
            AppDirectoryExisted: s.Files.DirectoryExists(appDir),
            ModifyPath: modifyPath && s.UserPath.IsSupported,
            BinDirectory: bin);

        var copied = false;
        try
        {
            await op.StepAsync(FaultCheckpoints.SelfInstallAppCopied, intent, async (_, token) =>
            {
                copied = await CopyAppAsync(source, intent, runningFromApp, token);
            }, ct, OperationState.Placed);

            await ActivateSelfShimAsync(op, ver, appDir, ct);
            await op.TransitionAsync(OperationState.ShimsActivated, ct);

            // Layout + database were initialized by the session; the commit marker
            // makes the PATH step roll-forward-only from here (A11).
            await op.TransitionAsync(OperationState.StateCommitted, ct);
            await op.CheckpointAsync(FaultCheckpoints.StateCommitted, ct);
        }
        catch (Exception ex) when (op.State is not (OperationState.StateCommitted or OperationState.Completed))
        {
            var record = await s.Journal.GetAsync(op.OpId, CancellationToken.None);
            if (record is not null)
            {
                var undo = await recovery.RecoverAsync(record, CancellationToken.None);
                if (undo.ToState == "RecoveryFailed")
                {
                    throw new WinBrewException(WinBrewErrorCode.PartialOrRecoveryRequired,
                        $"{ex.Message} — and undoing self install failed: {undo.Error}");
                }
            }

            throw;
        }

        await PostCommit.FinishSelfInstallAsync(s, op, intent, ct);
        await recovery.CleanupJournaledFilesAsync(ct);

        if (intent.ModifyPath)
        {
            warnings.Add("Shells that are already running may need to be reopened to see the PATH change (A12).");
        }

        return new SelfInstallResult(ver, appDir, s.Shims.BinaryPath(ShimSidecar.SelfShimName), copied, intent.ModifyPath, warnings);
    }

    /// <summary>"0.1.0+abc" → "0.1.0": app\&lt;wbrew-version&gt;\ uses the semantic version (A6).</summary>
    public static string AppVersionName(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var core = plus >= 0 ? informationalVersion[..plus] : informationalVersion;
        return VersionOps.TryNormalizeVersion(core, out var normalized) ? normalized : core;
    }

    private async Task<bool> CopyAppAsync(string source, SelfInstallIntent intent, bool runningFromApp, CancellationToken ct)
    {
        if (runningFromApp)
        {
            return false; // already in place (invoked through the self-shim)
        }

        if (s.Files.DirectoryExists(intent.AppDirectory))
        {
            foreach (var file in RequiredFiles)
            {
                var a = await s.Files.Sha256Async(Path.Combine(source, file), ct);
                var b = await s.Files.Sha256Async(Path.Combine(intent.AppDirectory, file), ct);
                if (a != b)
                {
                    throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                        $"{intent.AppDirectory} already exists with a different '{file}'. The installed copy may be running; " +
                        "WinBrew never overwrites a running version (A14).");
                }
            }

            return false;
        }

        await s.Files.CreateDirectoryAsync(intent.TemporaryDirectory, ct);
        foreach (var file in FilesToCopy(s.Files, source))
        {
            await s.Files.CopyFileAsync(Path.Combine(source, file), Path.Combine(intent.TemporaryDirectory, file), overwrite: true, ct);
        }

        await s.Files.MoveDirectoryAsync(intent.TemporaryDirectory, intent.AppDirectory, ct);
        return true;
    }

    private async Task ActivateSelfShimAsync(JournaledOperation op, string ver, string appDir, CancellationToken ct)
    {
        var template = Path.Combine(appDir, LifecyclePaths.ShimTemplateFileName);
        var templateSha = await s.Files.Sha256Async(template, ct)
            ?? throw new WinBrewException(WinBrewErrorCode.GeneralFailure, $"Shim template '{template}' is missing.");

        var name = ShimSidecar.SelfShimName;
        var sidecar = new ShimSidecar(Path.Combine(appDir, "wbrew.exe"), "winbrew", ver, ShimSidecar.Console).ToUtf8Bytes();
        var previous = await s.Shims.ReadSidecarAsync(name, ct);

        await op.StepAsync(FaultCheckpoints.ShimSidecarReplaced,
            new SidecarIntent(name, previous is null ? null : Convert.ToBase64String(previous), Convert.ToBase64String(sidecar)),
            (_, token) => s.Shims.WriteSidecarAsync(name, sidecar, token), ct);

        var existed = s.Shims.BinaryExists(name);
        await op.StepAsync(FaultCheckpoints.ShimBinaryReplaced, new BinaryIntent(name, existed),
            (seq, token) => s.Shims.PlaceBinaryAsync(name, template, templateSha,
                (path, t) => op.RecordFileAsync(seq, path, "old_shim", t), token), ct);
    }
}
