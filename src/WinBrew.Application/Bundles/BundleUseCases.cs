using System.Security.Cryptography;
using WinBrew.Application.Catalog;
using WinBrew.Application.Lifecycle;
using WinBrew.Application.Packages;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Artifacts;
using WinBrew.Core.Bundles;
using WinBrew.Core.Manifests;
using WinBrew.Core.Resolution;

namespace WinBrew.Application.Bundles;

public sealed record BundleExportResult(string File, string Lock, int Packages, int Locked);

/// <summary>
/// <c>wbrew bundle export --file &lt;path&gt; [--lock &lt;path&gt;]</c> (A15): the
/// bundle lists every explicitly installed package (with its pin) and the
/// repositories they come from; the lockfile records every installed
/// package exactly — version, architecture, repository identity (name, URL,
/// root key fingerprints), manifest SHA-256, artifact URL and SHA-256, and
/// explicit or dependency status. Read-only for WinBrew state.
/// </summary>
public sealed class BundleExportUseCase(LifecycleServices s, IVersionInfo version)
{
    public async Task<BundleExportResult> ExecuteAsync(string file, string? lockPath, CancellationToken ct)
    {
        var installed = await s.Installed.ListInstalledAsync(ct);
        var pins = await s.Installed.GetPinsAsync(ct);
        var repos = new List<BundleRepository>();
        foreach (var name in installed.Select(p => p.Repo).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var repository = await s.Catalog.GetRepositoryAsync(name, ct)
                ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"Installed packages come from '{name}', which is no longer configured.");
            repos.Add(new BundleRepository(repository.Name, repository.Url));
        }

        var bundle = new Bundle("1", repos, installed.Where(p => p.Explicit)
            .OrderBy(p => Identifiers.Qualified(p.Repo, p.Id), StringComparer.Ordinal)
            .Select(p => new BundlePackage(Identifiers.Qualified(p.Repo, p.Id), null, pins.ContainsKey(Identifiers.Qualified(p.Repo, p.Id))))
            .ToList());
        var lockfile = await LockBuilder.BuildAsync(s, installed, version.Version, ct);

        var bundlePath = Path.GetFullPath(file);
        var lockFull = Path.GetFullPath(lockPath ?? BundleFiles.DefaultLockPath(bundlePath));
        await s.Files.WriteFileAtomicAsync(bundlePath, BundleFiles.Serialize(bundle), ct);
        await s.Files.WriteFileAtomicAsync(lockFull, BundleFiles.Serialize(lockfile), ct);
        return new BundleExportResult(bundlePath, lockFull, bundle.Packages.Count, lockfile.Packages.Count);
    }
}

/// <summary>Builds a lockfile from installed state + the verified manifests in the cache (A15).</summary>
internal static class LockBuilder
{
    public static async Task<Lockfile> BuildAsync(LifecycleServices s, IReadOnlyList<InstalledPackage> packages, string winbrewVersion, CancellationToken ct)
    {
        var repositories = new List<LockRepository>();
        foreach (var name in packages.Select(p => p.Repo).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var state = await s.Catalog.GetTrustStateAsync(name, ct)
                ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"Repository '{name}' has no verified trust state; cannot record its identity (A15).");
            repositories.Add(new LockRepository(state.Repo, state.Url, state.RootFingerprints));
        }

        var entries = new List<LockPackage>();
        foreach (var p in packages.OrderBy(p => Identifiers.Qualified(p.Repo, p.Id), StringComparer.Ordinal))
        {
            var name = $"{Identifiers.Qualified(p.Repo, p.Id)}@{p.Version}";
            var entry = (await s.Catalog.GetPackageAsync(p.Repo, p.Id, ct))?.Versions.FirstOrDefault(v => v.Version == p.Version)
                ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"{name} is no longer in the verified catalog; cannot lock it (A15).");
            var bytes = await s.Files.ReadFileAsync(LifecyclePaths.CachedManifest(s.Layout, p.Repo, p.Id, p.Version), ct);
            if (bytes is null || Convert.ToHexStringLower(SHA256.HashData(bytes)) != entry.ManifestSha256)
            {
                throw new WinBrewException(WinBrewErrorCode.NotFound,
                    $"The verified manifest of {name} is not in the cache; reinstall it to lock it (A15).");
            }

            var manifest = ManifestParser.Parse(bytes, s.Flags.TestHooksBuild);
            var artifact = manifest.Artifacts.Single(a => a.Architecture == p.Arch);
            entries.Add(new LockPackage(p.Repo, p.Id, p.Version, p.Arch, entry.ManifestSha256, artifact.Url, artifact.Sha256, p.Explicit));
        }

        return new Lockfile("1", repositories, entries, winbrewVersion);
    }
}

public sealed record BundleInstallRequest(string File, string? Lock, bool Locked, bool Offline);

public sealed record BundleInstallResult(PlanResult Install, string? LockWritten, IReadOnlyList<string> Pinned);

/// <summary>
/// <c>wbrew bundle install --file &lt;path&gt; [--lock &lt;path&gt;] [--locked]</c> (A15).
/// Repositories named by the bundle must already be configured with the same
/// URL — trust is never added implicitly (D6). Without <c>--locked</c> the
/// requested packages are resolved as one plan (D2) and the lockfile is
/// written afterwards. With <c>--locked</c> nothing is re-resolved: the plan is
/// exactly the lockfile; the repository identity, manifest digests and
/// artifacts must match it, and any missing or different entry fails
/// (exit 3 or 5) before anything changes. In <c>--offline</c> mode only
/// verified cache entries are used.
/// </summary>
public sealed class BundleInstallUseCase(
    LifecycleServices s, MutationSession session, ResolutionPlanner planner, PlanExecutor executor, MetadataFreshness freshness, IVersionInfo version)
{
    public async Task<BundleInstallResult> ExecuteAsync(BundleInstallRequest request, PlanApproval approve, CancellationToken ct)
    {
        var bundlePath = Path.GetFullPath(request.File);
        var bundle = BundleFiles.ParseBundle(await ReadAsync(bundlePath, "bundle", ct), bundlePath);
        var lockPath = Path.GetFullPath(request.Lock ?? BundleFiles.DefaultLockPath(bundlePath));
        Lockfile? lockfile = null;
        if (request.Locked)
        {
            lockfile = BundleFiles.ParseLockfile(await ReadAsync(lockPath, "lockfile", ct), lockPath);
        }

        using var scope = await session.BeginAsync("bundle install", ct);
        await CheckRepositoriesAsync(bundle, ct);
        var notes = await freshness.EnsureForMutationAsync(scope, request.Offline, ct);

        var plan = lockfile is null ? await ResolveAsync(bundle, ct) : await LockedPlanAsync(bundle, lockfile, ct);
        plan = PlanRunner.WithNotes(plan, notes);
        var result = await PlanRunner.RunAsync(plan, scope, approve, executor, request.Offline, ct);

        var pinned = new List<string>();
        foreach (var package in bundle.Packages.Where(p => p.Pinned))
        {
            var item = plan.Items.Single(i => i.Package == Qualify(package.Id, plan));
            await s.Installed.SetPinnedAsync(item.Repo, item.Id, true, ct);
            pinned.Add(item.Package);
        }

        string? written = null;
        if (lockfile is null)
        {
            var installed = await s.Installed.ListInstalledAsync(ct);
            await s.Files.WriteFileAtomicAsync(lockPath, BundleFiles.Serialize(await LockBuilder.BuildAsync(s, installed, version.Version, ct)), ct);
            written = lockPath;
        }

        return new BundleInstallResult(result, written, pinned);
    }

    private async Task<byte[]> ReadAsync(string path, string what, CancellationToken ct) =>
        await s.Files.ReadFileAsync(path, ct)
        ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"The {what} '{path}' does not exist (A15).");

    private async Task CheckRepositoriesAsync(Bundle bundle, CancellationToken ct)
    {
        foreach (var wanted in bundle.Repositories)
        {
            var configured = await s.Catalog.GetRepositoryAsync(wanted.Name, ct)
                ?? throw new WinBrewException(WinBrewErrorCode.NotFound,
                    $"The bundle needs repository '{wanted.Name}' ({wanted.Url}), which is not configured. Add it with " +
                    $"'wbrew repo add {wanted.Name} {wanted.Url}' after verifying its root keys; trust is never added implicitly (D6).");
            if (!SameUrl(configured.Url, wanted.Url))
            {
                throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                    $"Repository '{wanted.Name}' is configured as {configured.Url}, but the bundle names {wanted.Url} (A15).");
            }
        }
    }

    private async Task<OperationPlan> ResolveAsync(Bundle bundle, CancellationToken ct)
    {
        var roots = new List<RootRequirement>();
        var installed = (await s.Installed.ListInstalledAsync(ct)).ToDictionary(p => Identifiers.Qualified(p.Repo, p.Id), StringComparer.Ordinal);
        foreach (var package in bundle.Packages)
        {
            var found = await PackageLookup.FindAsync(s.Catalog, PackageLookup.Parse(package.Id, null), ct);
            var name = Identifiers.Qualified(found.Repo, found.Id);
            var exact = package.Pinned && installed.TryGetValue(name, out var current) ? current.Version : null;
            roots.Add(new RootRequirement(name, exact, package.Range, $"the bundle requests {name}{(package.Range is null ? "" : " " + package.Range)}"));
        }

        return await planner.PlanAsync(new ResolutionPlanner.Request("bundle install", ResolveMode.Install, roots,
            new HashSet<string>(StringComparer.Ordinal)), ct);
    }

    private async Task<OperationPlan> LockedPlanAsync(Bundle bundle, Lockfile lockfile, CancellationToken ct)
    {
        foreach (var locked in lockfile.Repositories)
        {
            var state = await s.Catalog.GetTrustStateAsync(locked.Name, ct)
                ?? throw new WinBrewException(WinBrewErrorCode.NotFound,
                    $"The lockfile needs repository '{locked.Name}' ({locked.Url}), which is not configured (A15).");
            if (!SameUrl(state.Url, locked.Url) ||
                !state.RootFingerprints.Order(StringComparer.Ordinal).SequenceEqual(locked.RootKeyFingerprints.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"Repository '{locked.Name}' does not match the lockfile's identity (URL and root key fingerprints) (A15).",
                    new Dictionary<string, object?>
                    {
                        ["repository"] = locked.Name,
                        ["lockedFingerprints"] = locked.RootKeyFingerprints,
                        ["configuredFingerprints"] = state.RootFingerprints,
                    });
            }
        }

        var byName = lockfile.Packages.ToDictionary(p => Identifiers.Qualified(p.Repo, p.Id), StringComparer.Ordinal);
        foreach (var package in bundle.Packages)
        {
            if (ResolveLocked(package.Id, byName) is null)
            {
                throw new WinBrewException(WinBrewErrorCode.NotFound,
                    $"The lockfile has no entry for '{package.Id}', which the bundle requests. Re-run 'wbrew bundle install' without --locked to update it (A15).",
                    new Dictionary<string, object?> { ["missing"] = package.Id });
            }
        }

        var installed = (await s.Installed.ListInstalledAsync(ct)).ToDictionary(p => Identifiers.Qualified(p.Repo, p.Id), StringComparer.Ordinal);
        var pins = await s.Installed.GetPinsAsync(ct);
        var host = LifecyclePaths.HostArchitecture(s.Probe);
        var resolved = new Dictionary<string, ResolvedPackage>(StringComparer.Ordinal);
        foreach (var (name, p) in byName)
        {
            var entry = (await s.Catalog.GetPackageAsync(p.Repo, p.Id, ct))?.Versions.FirstOrDefault(v => v.Version == p.Version)
                ?? throw new WinBrewException(WinBrewErrorCode.NotFound,
                    $"{name}@{p.Version} from the lockfile is not in the verified catalog of '{p.Repo}' (A15).");
            if (!entry.Architectures.Contains(p.Architecture, StringComparer.Ordinal) ||
                !HostArchitectureRules.SelectionOrder(host).Contains(p.Architecture, StringComparer.Ordinal))
            {
                throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                    $"{name}@{p.Version} is locked to {p.Architecture}, which is not available or cannot run on this host (A10, A15).");
            }

            if (pins.TryGetValue(name, out var pinnedAt) && pinnedAt != p.Version)
            {
                throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                    $"{name} is pinned to {pinnedAt}, but the lockfile requires {p.Version}; run 'wbrew unpin {name}' first (D2 rule 1).");
            }

            var dependencies = new List<string>();
            foreach (var dependency in entry.Dependencies)
            {
                if (!Identifiers.TrySplitReference(dependency.Id, out var depRepo, out var depId) || depId is null)
                {
                    throw new WinBrewException(WinBrewErrorCode.VerificationFailure, $"{name}@{p.Version} has an invalid dependency '{dependency.Id}'.");
                }

                var qualified = Identifiers.Qualified(depRepo ?? p.Repo, depId);
                if (!byName.ContainsKey(qualified))
                {
                    throw new WinBrewException(WinBrewErrorCode.NotFound,
                        $"The lockfile is missing {qualified}, a dependency of {name}@{p.Version} (A15).");
                }

                dependencies.Add(qualified);
            }

            resolved[name] = new ResolvedPackage(p.Repo, p.Id, p.Version, new ArchitectureSelection(p.Architecture, false, false), dependencies);
        }

        var items = new List<PlanItem>();
        foreach (var r in GraphOrder.InstallOrder(resolved))
        {
            var p = byName[r.Qualified];
            installed.TryGetValue(r.Qualified, out var current);
            var action = current is null ? "install" : current.Version == p.Version && current.Arch == p.Architecture ? "keep" : "upgrade";
            items.Add(new PlanItem(r.Qualified, r.Repo, r.Id, action, p.Version, p.Architecture, current?.Version, current?.Arch,
                false, p.Explicit || current?.Explicit == true, p.Explicit && current is { Explicit: false }, r.Dependencies)
            {
                Locked = new LockPin(p.ManifestSha256, p.ArtifactUrl, p.ArtifactSha256),
            });
        }

        return new OperationPlan("bundle install --locked", items, []);
    }

    private static LockPackage? ResolveLocked(string id, IReadOnlyDictionary<string, LockPackage> byName)
    {
        if (id.Contains('/', StringComparison.Ordinal))
        {
            return byName.GetValueOrDefault(id);
        }

        var matches = byName.Values.Where(p => p.Id == id).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => null,
            _ => throw new WinBrewException(WinBrewErrorCode.UsageError,
                $"'{id}' is ambiguous in the lockfile; qualify it in the bundle: {string.Join(", ", matches.Select(m => Identifiers.Qualified(m.Repo, m.Id)))} (A9)."),
        };
    }

    private static string Qualify(string id, OperationPlan plan) =>
        id.Contains('/', StringComparison.Ordinal) ? id : plan.Items.Single(i => i.Id == id && i.Explicit).Package;

    private static bool SameUrl(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);
}
