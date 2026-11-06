using WinBrew.Application.Lifecycle;
using WinBrew.Core;
using WinBrew.Core.Operations;

namespace WinBrew.Application.Doctor;

/// <summary>
/// Phase B doctor checks (read-only): the active shim template exists and
/// managed shims' recorded template hashes (D4), interrupted operations and
/// RecoveryFailed reports (A11), and journaled .old files still awaiting
/// cleanup (D4, PLAN §1).
/// </summary>
public sealed class LifecycleDoctorChecks(LifecycleServices s, Ports.IInstallerStore installerStore, Installers.InstallerService installers)
{
    public async Task AddChecksAsync(List<DoctorCheck> checks, CancellationToken ct)
    {
        var dbPath = Path.Combine(s.Layout.Directories["state"], "winbrew.db");
        if (!s.Files.FileExists(dbPath))
        {
            return; // the database check already reported "no database yet"
        }

        await CheckTemplateAsync(checks, ct);
        await CheckJournalAsync(checks, ct);
        await CheckInstallersAsync(checks, ct);
    }

    /// <summary>A12: MsiQueryProductState / detection drift for installer-managed packages, reported only.</summary>
    private async Task CheckInstallersAsync(List<DoctorCheck> checks, CancellationToken ct)
    {
        var managed = await installerStore.ListInstallerPackagesAsync(ct);
        if (managed.Count == 0)
        {
            return;
        }

        var drift = managed.Select(installers.Reconcile).Where(d => d is not null).ToList();
        checks.Add(drift.Count == 0
            ? new("installers.reconcile", DoctorStatus.Ok, $"{managed.Count} installer-managed package(s) match Windows Installer / their detection rules.")
            : new("installers.reconcile", DoctorStatus.Warn, "Drift (not corrected): " + string.Join(" | ", drift)));
    }

    private async Task CheckTemplateAsync(List<DoctorCheck> checks, CancellationToken ct)
    {
        string template;
        try
        {
            template = await LifecyclePaths.FindShimTemplateAsync(s, ct);
        }
        catch (WinBrewException ex)
        {
            checks.Add(new("shims.template", DoctorStatus.Warn, ex.Message));
            return;
        }

        var sha = await s.Files.Sha256Async(template, ct);
        var stale = 0;
        var total = 0;
        foreach (var package in await s.Installed.ListInstalledAsync(ct))
        {
            foreach (var shim in await s.Installed.GetShimsForAsync(package.Repo, package.Id, ct))
            {
                total++;
                if (shim.TemplateSha256 != sha)
                {
                    stale++;
                }
            }
        }

        checks.Add(stale == 0
            ? new("shims.template", DoctorStatus.Ok, $"Shim template {template} present; {total} managed shim(s) match it (D4).")
            : new("shims.template", DoctorStatus.Warn,
                $"{stale} of {total} managed shim(s) were built from a different template than {template}; " +
                "they still work and are replaced on their next upgrade (D4)."));
    }

    private async Task CheckJournalAsync(List<DoctorCheck> checks, CancellationToken ct)
    {
        var incomplete = await s.Journal.GetIncompleteAsync(ct);
        checks.Add(incomplete.Count == 0
            ? new("journal.pending", DoctorStatus.Ok, "No interrupted operations (A11).")
            : new("journal.pending", DoctorStatus.Warn,
                $"{incomplete.Count} interrupted operation(s): " +
                string.Join(", ", incomplete.Select(o => $"{OperationTypeWire.ToWire(o.Type)} {o.OpId} ({OperationStateWire.ToWire(o.State)})")) +
                ". Run 'wbrew recover' (A11)."));

        foreach (var failed in await s.Journal.GetRecoveryFailedAsync(ct))
        {
            checks.Add(new("journal.recoveryFailed", DoctorStatus.Fail,
                $"Operation {failed.OpId} ({failed.Command}) ended in RecoveryFailed: {failed.Error ?? "no report recorded"} (A11)."));
        }

        var pending = await s.Journal.GetPendingCleanupAsync(ct);
        checks.Add(pending.Count == 0
            ? new("journal.oldFiles", DoctorStatus.Ok, "No journaled .old files awaiting cleanup (D4).")
            : new("journal.oldFiles", DoctorStatus.Warn,
                $"{pending.Count} journaled file(s) await cleanup (still locked?): " +
                string.Join(", ", pending.Take(5).Select(f => f.Path)) + (pending.Count > 5 ? ", …" : string.Empty) +
                ". They are deleted after the next successful mutating command (D4)."));
    }
}
