using NuGet.Versioning;
using WinBrew.Application.Lifecycle;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Artifacts;
using WinBrew.Core.Manifests;

namespace WinBrew.Application.Packages;

public sealed record OutdatedRow(string Package, string Installed, string Latest, bool Pinned);

public sealed record OutdatedResult(IReadOnlyList<OutdatedRow> Packages, [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings);

/// <summary>
/// <c>wbrew outdated</c>: installed packages whose catalog has a newer
/// stable version with an artifact for this host (A10, D2 rule 6). Read-only:
/// no lock, never a refresh (A8).
/// </summary>
public sealed class OutdatedUseCase(LifecycleServices s)
{
    public async Task<OutdatedResult> ExecuteAsync(CancellationToken ct)
    {
        var warnings = await ReadOnlyWarnings.CollectAsync(s.Journal, ct);
        var pins = await s.Installed.GetPinsAsync(ct);
        var host = LifecyclePaths.HostArchitecture(s.Probe);
        var rows = new List<OutdatedRow>();

        foreach (var p in (await s.Installed.ListInstalledAsync(ct)).OrderBy(p => Identifiers.Qualified(p.Repo, p.Id), StringComparer.Ordinal))
        {
            var catalog = await s.Catalog.GetPackageAsync(p.Repo, p.Id, ct);
            if (catalog is null || !NuGetVersion.TryParse(p.Version, out var current))
            {
                continue;
            }

            var latest = catalog.Versions
                .Where(v => !v.IsPrerelease && HostArchitectureRules.Select(host, v.Architectures) is not null)
                .Select(v => NuGetVersion.Parse(v.Version))
                .Where(v => v > current)
                .OrderByDescending(v => v)
                .FirstOrDefault();
            if (latest is not null)
            {
                var name = Identifiers.Qualified(p.Repo, p.Id);
                rows.Add(new OutdatedRow(name, p.Version, latest.ToNormalizedString(), pins.ContainsKey(name)));
            }
        }

        return new OutdatedResult(rows, warnings);
    }
}

public sealed record PinResult(string Package, string Version, bool Pinned, bool Changed);

/// <summary><c>wbrew pin</c> / <c>wbrew unpin</c> (D2 rule 1): mutating, under the lock (D5).</summary>
public sealed class PinUseCase(LifecycleServices s, MutationSession session)
{
    public async Task<PinResult> ExecuteAsync(string package, bool pin, CancellationToken ct)
    {
        using var scope = await session.BeginAsync(pin ? "pin" : "unpin", ct);
        var installed = await InstalledLookup.FindAsync(s.Installed, package, ct);
        var changed = await s.Installed.SetPinnedAsync(installed.Repo, installed.Id, pin, ct);
        return new PinResult(Identifiers.Qualified(installed.Repo, installed.Id), installed.Version, pin, changed);
    }
}

public sealed record AutoremoveResult(bool DryRun, IReadOnlyList<UninstallPlan> Removed, [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings);

/// <summary>
/// <c>wbrew autoremove [--dry-run]</c> (D8): removes packages that were
/// installed only as dependencies and that no installed package references
/// any more — repeatedly, so chains of orphans go too (dependents first).
/// Pinned packages are never removed.
/// </summary>
public sealed class AutoremoveUseCase(LifecycleServices s, MutationSession session, UninstallUseCase uninstall)
{
    public async Task<AutoremoveResult> ExecuteAsync(bool dryRun, CancellationToken ct)
    {
        if (dryRun)
        {
            return new AutoremoveResult(true, await PlanAsync(ct), []);
        }

        using var scope = await session.BeginAsync("autoremove", ct);
        var plan = await PlanAsync(ct);
        var warnings = new List<string>();
        foreach (var item in plan)
        {
            warnings.AddRange(await uninstall.RemoveAsync(item, ct));
        }

        return new AutoremoveResult(false, plan, warnings);
    }

    private async Task<List<UninstallPlan>> PlanAsync(CancellationToken ct)
    {
        var installed = (await s.Installed.ListInstalledAsync(ct)).ToDictionary(p => Identifiers.Qualified(p.Repo, p.Id), StringComparer.Ordinal);
        var edges = await s.Installed.ListDependencyEdgesAsync(ct);
        var pins = await s.Installed.GetPinsAsync(ct);
        var remaining = new HashSet<string>(installed.Keys, StringComparer.Ordinal);
        var order = new List<UninstallPlan>();

        while (true)
        {
            var orphans = remaining
                .Where(n => !installed[n].Explicit && !pins.ContainsKey(n))
                .Where(n => !edges.Any(e => e.Dependency == n && remaining.Contains(e.Dependent)))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            if (orphans.Count == 0)
            {
                return order;
            }

            foreach (var name in orphans)
            {
                var p = installed[name];
                var shims = (await s.Installed.GetShimsForAsync(p.Repo, p.Id, ct)).Select(r => r.Name).ToList();
                order.Add(new UninstallPlan(p.Repo, p.Id, p.Version, p.Arch, shims));
                remaining.Remove(name);
            }
        }
    }
}

public sealed record CleanupResult(
    bool DryRun,
    IReadOnlyList<string> Removed,
    long FreedBytes,
    long CacheBytesBefore,
    long CacheBytesAfter,
    int Protected,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings);

/// <summary>
/// <c>wbrew cleanup [--dry-run]</c> (A10, D10 cache.maxBytes): trims the
/// content-addressed cache to its budget, oldest first. Cleanup NEVER removes
/// an artifact that is active, pinned, referenced (the installed version's
/// artifact) or retained (an inactive version kept by retainVersions). When
/// the protected set cannot be fully determined, nothing is removed.
/// Journaled .old files of finished operations are deleted too (D4).
/// </summary>
public sealed class CleanupUseCase(LifecycleServices s, MutationSession session, RecoveryService recovery)
{
    public async Task<CleanupResult> ExecuteAsync(bool dryRun, CancellationToken ct)
    {
        if (dryRun)
        {
            return await RunAsync(dryRun: true, budget: await BudgetAsync(ct), ct);
        }

        using var scope = await session.BeginAsync("cleanup", ct);
        var result = await RunAsync(dryRun: false, budget: await BudgetAsync(ct), ct);
        await recovery.CleanupJournaledFilesAsync(ct);
        return result;
    }

    private async Task<long> BudgetAsync(CancellationToken ct)
    {
        var doc = await s.Config.LoadAsync(ct);
        return doc.Values.TryGetValue("cache.maxBytes", out var v) && v is long l
            ? l
            : (long)Core.Config.ConfigDefinitions.Keys["cache.maxBytes"].Default;
    }

    private async Task<CleanupResult> RunAsync(bool dryRun, long budget, CancellationToken ct)
    {
        var warnings = new List<string>();
        var (protectedShas, complete) = await ProtectedArtifactsAsync(warnings, ct);
        var entries = s.Cache.List();
        var total = entries.Sum(e => e.Size);
        var before = total;
        var removed = new List<string>();

        if (!complete)
        {
            warnings.Add("Cleanup removed nothing: the artifacts of some installed or retained versions could not be identified, " +
                         "so the protected set is incomplete (A10: never remove active, pinned, referenced or retained artifacts).");
            return new CleanupResult(dryRun, [], 0, before, before, protectedShas.Count, warnings);
        }

        foreach (var entry in entries.Where(e => !protectedShas.Contains(e.Sha256)))
        {
            if (total <= budget)
            {
                break;
            }

            if (!dryRun)
            {
                await s.Files.DeleteFileAsync(s.Cache.PathFor(entry.Sha256), ct);
            }

            removed.Add(entry.Sha256);
            total -= entry.Size;
        }

        if (total > budget)
        {
            warnings.Add($"The cache is still above cache.maxBytes ({budget}): the remaining {total} bytes are protected artifacts.");
        }

        return new CleanupResult(dryRun, removed, before - total, before, total, protectedShas.Count, warnings);
    }

    /// <summary>Artifacts of every installed version and every retained inactive version, from their verified manifests.</summary>
    private async Task<(HashSet<string> Shas, bool Complete)> ProtectedArtifactsAsync(List<string> warnings, CancellationToken ct)
    {
        var shas = new HashSet<string>(StringComparer.Ordinal);
        var complete = true;

        foreach (var p in await s.Installed.ListInstalledAsync(ct))
        {
            var versions = new List<(string Version, string Arch)> { (p.Version, p.Arch) };
            var root = LifecyclePaths.PackageRoot(s.Layout, p.Id);
            foreach (var version in s.Files.ListDirectories(root).Where(v => NuGetVersion.TryParse(v, out _)))
            {
                foreach (var arch in s.Files.ListDirectories(Path.Combine(root, version)).Where(a => a is "x64" or "arm64" or "x86"))
                {
                    versions.Add((version, arch));
                }
            }

            foreach (var (version, arch) in versions.Distinct())
            {
                var sha = await ArtifactShaAsync(p.Repo, p.Id, version, arch, ct);
                if (sha is null)
                {
                    complete = false;
                    warnings.Add($"No verified manifest cached for {p.Repo}/{p.Id}@{version}; its artifact cannot be identified.");
                    continue;
                }

                shas.Add(sha);
            }
        }

        return (shas, complete);
    }

    private async Task<string?> ArtifactShaAsync(string repo, string id, string version, string arch, CancellationToken ct)
    {
        var bytes = await s.Files.ReadFileAsync(LifecyclePaths.CachedManifest(s.Layout, repo, id, version), ct);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return ManifestParser.Parse(bytes, s.Flags.TestHooksBuild).Artifacts.FirstOrDefault(a => a.Architecture == arch)?.Sha256;
        }
        catch (WinBrewException)
        {
            return null;
        }
    }
}
