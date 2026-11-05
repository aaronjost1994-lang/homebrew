using WinBrew.Application.Lifecycle;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Operations;

namespace WinBrew.Application.Packages;

public sealed record UninstallRequest(string Package, bool DryRun);

public sealed record UninstallPlan(string Repo, string Id, string Version, string Arch, IReadOnlyList<string> Shims);

public sealed record UninstallResult(UninstallPlan Plan, bool Changed, bool DryRun, [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings, IReadOnlyList<RecoveredOperation> Recovered);

public delegate Task<bool> UninstallApproval(UninstallPlan plan, CancellationToken ct);

/// <summary>
/// <c>wbrew uninstall</c> (A11/D8). Refuses (exit 6) while another installed
/// package depends on the target. The SQLite commit happens FIRST (nothing
/// on disk changes before it), then shims and package files are removed as
/// idempotent roll-forward steps — a crash at any point is finished by
/// recovery, and a running shim's binary becomes a journaled .old file.
/// </summary>
public sealed class UninstallUseCase(LifecycleServices s, MutationSession session, RecoveryService recovery)
{
    public async Task<UninstallResult> ExecuteAsync(UninstallRequest request, UninstallApproval approve, CancellationToken ct)
    {
        var parsed = PackageLookup.Parse(request.Package, versionOption: null);
        if (parsed.Version is not null)
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError, "uninstall takes a package without a version.");
        }

        if (request.DryRun)
        {
            var dry = await PlanAsync(parsed, ct);
            return new UninstallResult(dry, Changed: false, DryRun: true, [], []);
        }

        using var scope = await session.BeginAsync("uninstall", ct);
        var plan = await PlanAsync(parsed, ct);

        if (!await approve(plan, ct))
        {
            throw new OperationCanceledException("The operation plan was not approved.");
        }

        var op = await JournaledOperation.StartAsync(s.Journal, s.Faults, OperationType.Uninstall,
            $"uninstall {plan.Repo}/{plan.Id}", ct);
        var intent = new UninstallIntent(plan.Repo, plan.Id, plan.Version, plan.Shims,
            LifecyclePaths.PackageRoot(s.Layout, plan.Id));
        await op.RecordAsync(JournalStepNames.Plan, intent, ct);

        await op.StepAsync(FaultCheckpoints.StateCommitted, intent, async (_, token) =>
        {
            await s.Installed.CommitUninstallAsync(op.OpId, plan.Repo, plan.Id, token);
            op.AssumeState(OperationState.StateCommitted);
        }, ct);

        var warnings = new List<string>();
        try
        {
            await PostCommit.FinishUninstallAsync(s, op, intent, ct);
            await recovery.CleanupJournaledFilesAsync(ct);
        }
        catch (Exception ex) when (ex is WinBrewException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Uninstalled, but file removal did not finish ({ex.Message}); " +
                         "the next mutating command or 'wbrew recover' completes it.");
        }

        return new UninstallResult(plan, Changed: true, DryRun: false, warnings, scope.Recovered);
    }

    private async Task<UninstallPlan> PlanAsync(PackageRequest request, CancellationToken ct)
    {
        InstalledPackage? installed;
        if (request.Repo is not null)
        {
            installed = await s.Installed.GetInstalledAsync(request.Repo, request.Id, ct);
        }
        else
        {
            var matches = (await s.Installed.ListInstalledAsync(ct)).Where(p => p.Id == request.Id).ToList();
            if (matches.Count > 1)
            {
                var candidates = matches.Select(m => Identifiers.Qualified(m.Repo, m.Id)).ToList();
                throw new WinBrewException(WinBrewErrorCode.UsageError,
                    $"'{request.Id}' is ambiguous; use a qualified id: {string.Join(", ", candidates)} (A9).",
                    new Dictionary<string, object?> { ["candidates"] = candidates });
            }

            installed = matches.SingleOrDefault();
        }

        if (installed is null)
        {
            throw new WinBrewException(WinBrewErrorCode.NotFound, $"Package '{request.Id}' is not installed.");
        }

        var dependents = await s.Installed.GetDependentsAsync(installed.Repo, installed.Id, ct);
        if (dependents.Count > 0)
        {
            throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                $"Cannot uninstall {installed.Repo}/{installed.Id}: required by {string.Join(", ", dependents)} (D8).",
                new Dictionary<string, object?> { ["dependents"] = dependents });
        }

        var shims = (await s.Installed.GetShimsForAsync(installed.Repo, installed.Id, ct)).Select(r => r.Name).ToList();
        return new UninstallPlan(installed.Repo, installed.Id, installed.Version, installed.Arch, shims);
    }
}
