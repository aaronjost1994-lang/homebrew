using WinBrew.Application.Catalog;
using WinBrew.Application.Lifecycle;
using WinBrew.Core;
using WinBrew.Core.Resolution;

namespace WinBrew.Application.Packages;

public sealed record InstallRequest(string Package, string? VersionOption, bool DryRun, bool Offline, Installers.InstallerOptions? Installer = null);

public sealed record UpgradeRequest(string? Package, bool DryRun, bool Offline);

/// <summary>Result of install/upgrade: the plan shown, and what happened.</summary>
public sealed record PlanResult(
    OperationPlan Plan,
    bool Changed,
    bool DryRun,
    IReadOnlyList<string> Shims,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings,
    IReadOnlyList<RecoveredOperation> Recovered);

/// <summary>
/// <c>wbrew install &lt;package&gt;[@&lt;version&gt;]</c> (A8): the full dependency
/// graph is resolved from the SQLite catalog BEFORE any change (A10, D2);
/// already-installed packages stay installed and satisfied; the plan lists
/// every package (install / upgrade / keep) in dependency order.
/// </summary>
public sealed class InstallUseCase(LifecycleServices s, MutationSession session, ResolutionPlanner planner, PlanExecutor executor, MetadataFreshness freshness)
{
    public async Task<PlanResult> ExecuteAsync(InstallRequest request, PlanApproval approve, CancellationToken ct)
    {
        var parsed = PackageLookup.Parse(request.Package, request.VersionOption);

        if (request.DryRun)
        {
            // --dry-run: the full plan from the EXISTING catalog; no lock, no downloads,
            // no state writes, no shims, never a metadata refresh (A8).
            await freshness.EnsureForDryRunAsync(ct);
            var dry = await PlanAsync(parsed, ct);
            return new PlanResult(dry, false, true, [], dry.Warnings, []);
        }

        using var scope = await session.BeginAsync("install", ct);
        var refreshed = await freshness.EnsureForMutationAsync(scope, request.Offline, ct);
        var plan = PlanRunner.WithNotes(await PlanAsync(parsed, ct), refreshed);
        return await PlanRunner.RunAsync(plan, scope, approve, executor, request.Offline, ct, request.Installer);
    }

    private async Task<OperationPlan> PlanAsync(PackageRequest request, CancellationToken ct)
    {
        var package = await PackageLookup.FindAsync(s.Catalog, request, ct);
        var name = Identifiers.Qualified(package.Repo, package.Id);
        if (request.Version is not null && package.Versions.All(v => v.Version != request.Version))
        {
            throw new WinBrewException(WinBrewErrorCode.NotFound, $"Version {request.Version} of {name} is not in the catalog.");
        }

        var pins = await s.Installed.GetPinsAsync(ct);
        if (request.Version is not null && pins.TryGetValue(name, out var pinned) && pinned != request.Version)
        {
            throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                $"{name} is pinned to {pinned}; run 'wbrew unpin {name}' first (D2 rule 1).");
        }

        return await planner.PlanAsync(new ResolutionPlanner.Request(
            "install",
            ResolveMode.Install,
            [new RootRequirement(name, request.Version, null, $"you requested {name}")],
            new HashSet<string>(StringComparer.Ordinal)), ct);
    }
}

/// <summary>
/// <c>wbrew upgrade [package] [--dry-run]</c> (A8, D2 rule 3): the highest
/// satisfying versions for the target (or every unpinned installed package),
/// with architecture re-selection (A10). Upgrades are compensated as a whole
/// until StateCommitted: previous versions stay usable on any failure (A11).
/// </summary>
public sealed class UpgradeUseCase(LifecycleServices s, MutationSession session, ResolutionPlanner planner, PlanExecutor executor, MetadataFreshness freshness)
{
    public async Task<PlanResult> ExecuteAsync(UpgradeRequest request, PlanApproval approve, CancellationToken ct)
    {
        if (request.DryRun)
        {
            await freshness.EnsureForDryRunAsync(ct);
            var dry = await PlanAsync(request.Package, ct);
            return new PlanResult(dry, false, true, [], dry.Warnings, []);
        }

        using var scope = await session.BeginAsync("upgrade", ct);
        var refreshed = await freshness.EnsureForMutationAsync(scope, request.Offline, ct);
        var plan = PlanRunner.WithNotes(await PlanAsync(request.Package, ct), refreshed);
        return await PlanRunner.RunAsync(plan, scope, approve, executor, request.Offline, ct);
    }

    private async Task<OperationPlan> PlanAsync(string? package, CancellationToken ct)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        if (package is not null)
        {
            var target = await InstalledLookup.FindAsync(s.Installed, package, ct);
            var name = Identifiers.Qualified(target.Repo, target.Id);
            if ((await s.Installed.GetPinsAsync(ct)).ContainsKey(name))
            {
                throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                    $"{name} is pinned at {target.Version}; run 'wbrew unpin {name}' to upgrade it (D2 rule 1).");
            }

            targets.Add(name);
        }

        return await planner.PlanAsync(new ResolutionPlanner.Request("upgrade", ResolveMode.Upgrade, [], targets), ct);
    }
}

/// <summary>Shared tail of install/upgrade: approval, nothing-to-do, execution.</summary>
internal static class PlanRunner
{
    /// <summary>Puts metadata-refresh / offline-age notes at the top of the plan's warnings (A8: the plan says so).</summary>
    public static OperationPlan WithNotes(OperationPlan plan, IReadOnlyList<string> notes) =>
        notes.Count == 0 ? plan : plan with { Warnings = [.. notes, .. plan.Warnings] };

    public static async Task<PlanResult> RunAsync(
        OperationPlan plan, MutationScope scope, PlanApproval approve, PlanExecutor executor, bool offline, CancellationToken ct,
        Installers.InstallerOptions? installer = null)
    {
        if (plan.NothingToDo)
        {
            return new PlanResult(plan, false, false, [], plan.Warnings, scope.Recovered);
        }

        if (!await approve(plan, ct))
        {
            throw new OperationCanceledException("The operation plan was not approved.");
        }

        var result = await executor.ExecuteAsync(plan, scope, offline, ct, installer);
        return new PlanResult(plan, true, false, result.Shims, result.Warnings, scope.Recovered);
    }
}

/// <summary>Finds an INSTALLED package by '&lt;id&gt;' or '&lt;repo&gt;/&lt;id&gt;' (ambiguity → exit 2, missing → exit 3).</summary>
public static class InstalledLookup
{
    public static async Task<Ports.InstalledPackage> FindAsync(Ports.IInstalledStore installed, string package, CancellationToken ct)
    {
        var request = PackageLookup.Parse(package, versionOption: null);
        if (request.Version is not null)
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError, "This command takes a package without a version.");
        }

        if (request.Repo is not null)
        {
            return await installed.GetInstalledAsync(request.Repo, request.Id, ct)
                ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"Package '{package}' is not installed.");
        }

        var matches = (await installed.ListInstalledAsync(ct)).Where(p => p.Id == request.Id).ToList();
        if (matches.Count > 1)
        {
            var candidates = matches.Select(m => Identifiers.Qualified(m.Repo, m.Id)).ToList();
            throw new WinBrewException(WinBrewErrorCode.UsageError,
                $"'{request.Id}' is ambiguous; use a qualified id: {string.Join(", ", candidates)} (A9).",
                new Dictionary<string, object?> { ["candidates"] = candidates });
        }

        return matches.SingleOrDefault() ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"Package '{package}' is not installed.");
    }
}
