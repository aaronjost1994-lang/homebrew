using WinBrew.Application.Lifecycle;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Resolution;

namespace WinBrew.Application.Packages;

/// <summary>
/// One package in an operation plan (A8, A10).
/// Action: <c>install</c> (new), <c>upgrade</c> (version and/or architecture change),
/// <c>keep</c> (already at the selected version — listed so the plan shows the full graph).
/// </summary>
public sealed record PlanItem(
    string Package,
    string Repo,
    string Id,
    string Action,
    string Version,
    string Arch,
    string? PreviousVersion,
    string? PreviousArch,
    bool ArchitectureFallback,
    bool Explicit,
    bool PromoteToExplicit,
    IReadOnlyList<string> Dependencies)
{
    /// <summary>bundle install --locked (A15): the exact digests the lockfile requires; null when resolved normally.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public LockPin? Locked { get; init; }

    /// <summary>True when files change (install or upgrade).</summary>
    public bool Changes => Action is "install" or "upgrade";

    /// <summary>"x64 → arm64" when the architecture is re-selected (A10).</summary>
    public string? ArchitectureTransition => PreviousArch is not null && PreviousArch != Arch ? $"{PreviousArch} → {Arch}" : null;
}

/// <summary>The lockfile's exact manifest and artifact for one package (A15).</summary>
public sealed record LockPin(string ManifestSha256, string ArtifactUrl, string ArtifactSha256);

/// <summary>The full plan shown before consent: every package of the resolved graph, in install order.</summary>
public sealed record OperationPlan(string Command, IReadOnlyList<PlanItem> Items, IReadOnlyList<string> Warnings)
{
    public bool NothingToDo => Items.All(i => !i.Changes && !i.PromoteToExplicit);
}

/// <summary>Renders the plan and returns consent (the CLI shows it; --yes approves; non-interactive → exit 10).</summary>
public delegate Task<bool> PlanApproval(OperationPlan plan, CancellationToken ct);

/// <summary>
/// Builds the in-memory catalog snapshot (D2/D6: SQLite catalog only — no
/// manifest is fetched for planning), runs the pure PubGrub resolver, and
/// turns the result into an <see cref="OperationPlan"/>. A conflict throws
/// ConflictOrRefused (exit 6) with the explanation and a structured
/// <c>conflict</c> detail; an unconfigured repository named by a
/// qualified dependency is NotFound (exit 3, A9). Nothing is written.
/// </summary>
public sealed class ResolutionPlanner(LifecycleServices s)
{
    public sealed record Request(
        string Command,
        ResolveMode Mode,
        IReadOnlyList<RootRequirement> Requested,
        IReadOnlySet<string> UpgradeTargets);

    public async Task<OperationPlan> PlanAsync(Request request, CancellationToken ct)
    {
        var installed = await s.Installed.ListInstalledAsync(ct);
        var installedByName = installed.ToDictionary(p => Identifiers.Qualified(p.Repo, p.Id), StringComparer.Ordinal);
        var pins = await s.Installed.GetPinsAsync(ct);
        var repositories = (await s.Catalog.ListRepositoriesAsync(ct)).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>();

        // Roots: what was asked for, plus every explicitly installed package that
        // must stay installed (and satisfied) — an install never silently breaks them.
        var roots = new List<RootRequirement>(request.Requested);
        var requestedNames = request.Requested.Select(r => r.Qualified).ToHashSet(StringComparer.Ordinal);
        foreach (var p in installed.Where(p => p.Explicit))
        {
            var name = Identifiers.Qualified(p.Repo, p.Id);
            if (requestedNames.Contains(name))
            {
                continue;
            }

            if (await s.Catalog.GetPackageAsync(p.Repo, p.Id, ct) is null)
            {
                warnings.Add($"{name} {p.Version} is installed but no longer in the catalog; it is left as is.");
                continue;
            }

            roots.Add(new RootRequirement(name, null, null, $"{name} {p.Version} is installed"));
        }

        var snapshot = await BuildSnapshotAsync(roots.Select(r => r.Qualified).Concat(installedByName.Keys), repositories, ct);
        var result = PubGrubResolver.Resolve(new ResolverRequest(
            snapshot,
            LifecyclePaths.HostArchitecture(s.Probe),
            roots,
            installedByName.ToDictionary(kv => kv.Key, kv => kv.Value.Version, StringComparer.Ordinal),
            pins,
            request.Mode,
            request.UpgradeTargets));

        if (result.Conflict is { } conflict)
        {
            var details = new Dictionary<string, object?>
            {
                ["conflict"] = new Dictionary<string, object?>
                {
                    ["explanation"] = conflict.Explanation.ToList(),
                    ["causes"] = conflict.Causes.Select(c => new Dictionary<string, object?>
                    {
                        ["kind"] = c.Kind,
                        ["package"] = c.Package,
                        ["version"] = c.Version,
                        ["dependency"] = c.Dependency,
                        ["range"] = c.Range,
                        ["text"] = c.Text,
                    }).ToList(),
                },
            };

            if (conflict.MissingRepository is { } missing)
            {
                details["missingRepository"] = missing;
                throw new WinBrewException(WinBrewErrorCode.NotFound,
                    $"Repository '{missing}' is required by a dependency but is not configured (A9).\n" + string.Join("\n", conflict.Explanation), details);
            }

            throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                "Dependencies cannot be resolved; nothing was changed (D2).\n" + string.Join("\n", conflict.Explanation), details);
        }

        var items = new List<PlanItem>();
        foreach (var r in result.Packages)
        {
            installedByName.TryGetValue(r.Qualified, out var current);
            var requested = requestedNames.Contains(r.Qualified);
            var action = current is null ? "install"
                : current.Version == r.Version && current.Arch == r.Architecture.Architecture ? "keep"
                : "upgrade";
            items.Add(new PlanItem(r.Qualified, r.Repo, r.Id, action, r.Version, r.Architecture.Architecture,
                current?.Version, current?.Arch, r.Architecture.IsFallback,
                Explicit: requested || current?.Explicit == true,
                PromoteToExplicit: requested && current is { Explicit: false },
                r.Dependencies));

            if (r.Architecture.IsFallback && action != "keep")
            {
                var host = Core.Artifacts.HostArchitectureRules.HostName(LifecyclePaths.HostArchitecture(s.Probe));
                warnings.Add($"Architecture fallback: {r.Qualified}@{r.Version} has no {host} artifact; using {r.Architecture.Architecture} (A10).");
            }

            if (items[^1].ArchitectureTransition is { } transition && action == "upgrade")
            {
                warnings.Add($"Architecture re-selection for {r.Qualified}: {transition} (A10).");
            }
        }

        return new OperationPlan(request.Command, items, warnings);
    }

    /// <summary>Reads every catalog package reachable from the roots (all versions, all dependencies) into memory.</summary>
    private async Task<ResolverSnapshot> BuildSnapshotAsync(IEnumerable<string> start, HashSet<string> repositories, CancellationToken ct)
    {
        var packages = new Dictionary<string, SnapshotPackage>(StringComparer.Ordinal);
        var queue = new Queue<string>(start.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal));
        var seen = new HashSet<string>(queue, StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            var slash = name.IndexOf('/', StringComparison.Ordinal);
            var repo = name[..slash];
            if (!repositories.Contains(repo))
            {
                continue;
            }

            var package = await s.Catalog.GetPackageAsync(repo, name[(slash + 1)..], ct);
            if (package is null)
            {
                continue;
            }

            packages[name] = new SnapshotPackage(package.Repo, package.Id, package.Versions
                .Select(v => new CandidateVersion(v.Version, v.Architectures, v.Dependencies.Select(d => new CandidateDependency(d.Id, d.Range)).ToList()))
                .ToList());

            foreach (var dep in package.Versions.SelectMany(v => v.Dependencies))
            {
                Identifiers.TrySplitReference(dep.Id, out var depRepo, out var depId);
                var qualified = Identifiers.Qualified(depRepo ?? package.Repo, depId!);
                if (seen.Add(qualified))
                {
                    queue.Enqueue(qualified);
                }
            }
        }

        return new ResolverSnapshot(packages.Values.ToList(), repositories);
    }
}
