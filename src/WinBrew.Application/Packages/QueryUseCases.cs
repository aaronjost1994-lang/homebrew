using NuGet.Versioning;
using WinBrew.Application.Lifecycle;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Artifacts;
using WinBrew.Core.Manifests;
using WinBrew.Core.Operations;

namespace WinBrew.Application.Packages;

public sealed record InstalledRow(string Package, string Repo, string Id, string Version, string Arch, bool Explicit, IReadOnlyList<string> Shims);

public sealed record InstallerRow(string Package, string Repo, string Id, string Version, string Arch, string Type, string Scope, string? ProductCode, string State, string? Drift);

public sealed record ListResult(IReadOnlyList<InstalledRow> Packages, [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings)
{
    /// <summary>Installer-managed packages (v1.1) with explicit reconciliation (A12).</summary>
    public IReadOnlyList<InstallerRow> Installers { get; init; } = [];
}

public sealed record InfoVersion(string Version, bool Prerelease, IReadOnlyList<string> Architectures, IReadOnlyList<string> Dependencies);

public sealed record InfoResult(
    string Package,
    string DisplayName,
    string Description,
    string? Latest,
    IReadOnlyList<InfoVersion> Versions,
    InstalledRow? Installed,
    bool? WritesToInstallDir,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings);

/// <summary>Warnings every read-only command adds (A11: pending recovery is reported, never acted on).</summary>
internal static class ReadOnlyWarnings
{
    public static async Task<List<string>> CollectAsync(IOperationJournal journal, CancellationToken ct)
    {
        var warnings = new List<string>();
        var pending = await journal.GetIncompleteAsync(ct);
        if (pending.Count > 0)
        {
            warnings.Add($"{pending.Count} interrupted operation(s) need recovery: " +
                         string.Join(", ", pending.Select(p => $"{OperationTypeWire.ToWire(p.Type)} {p.OpId} ({OperationStateWire.ToWire(p.State)})")) +
                         ". Run 'wbrew recover' or any mutating command (A11).");
        }

        var failed = await journal.GetRecoveryFailedAsync(ct);
        if (failed.Count > 0)
        {
            warnings.Add($"{failed.Count} operation(s) ended in RecoveryFailed; see 'wbrew doctor' (A11).");
        }

        return warnings;
    }
}

/// <summary><c>wbrew list</c>: installed packages. Read-only: no lock, WAL snapshot reads (D5).</summary>
public sealed class ListUseCase(IInstalledStore installed, IOperationJournal journal, IInstallerStore installerStore, Installers.InstallerService installers)
{
    public async Task<ListResult> ExecuteAsync(CancellationToken ct)
    {
        var rows = new List<InstalledRow>();
        foreach (var p in (await installed.ListInstalledAsync(ct)).OrderBy(p => Identifiers.Qualified(p.Repo, p.Id), StringComparer.Ordinal))
        {
            var shims = (await installed.GetShimsForAsync(p.Repo, p.Id, ct)).Select(r => r.Name).ToList();
            rows.Add(new InstalledRow(Identifiers.Qualified(p.Repo, p.Id), p.Repo, p.Id, p.Version, p.Arch, p.Explicit, shims));
        }

        var warnings = await ReadOnlyWarnings.CollectAsync(journal, ct);
        var managed = new List<InstallerRow>();
        foreach (var p in await installerStore.ListInstallerPackagesAsync(ct))
        {
            // A12: reconciliation is explicit — drift is REPORTED, never corrected silently.
            var drift = installers.Reconcile(p);
            if (drift is not null)
            {
                warnings.Add("Installer drift: " + drift + " Uninstall it with 'wbrew uninstall --allow-installer' or reinstall it.");
            }

            managed.Add(new InstallerRow(Identifiers.Qualified(p.Repo, p.Id), p.Repo, p.Id, p.Version, p.Arch, p.InstallType, p.Scope, p.ProductCode, p.State, drift));
        }

        return new ListResult(rows, warnings) { Installers = managed };
    }
}

/// <summary>
/// <c>wbrew info</c>: catalog summary, versions, the locally computed latest
/// version for this host (no latestVersion field exists, D6), installed
/// state, and the writesToInstallDir warning when the verified manifest is
/// cached (A3/A9). Read-only.
/// </summary>
public sealed class InfoUseCase(LifecycleServices s, Catalog.MetadataFreshness freshness)
{
    public async Task<InfoResult> ExecuteAsync(string package, bool offline, CancellationToken ct)
    {
        var request = PackageLookup.Parse(package, versionOption: null);
        var warnings = await ReadOnlyWarnings.CollectAsync(s.Journal, ct);
        warnings.AddRange(await freshness.ForReadOnlyAsync(ct));
        var catalogPackage = await PackageLookup.FindAsync(s.Catalog, request, ct);
        var host = LifecyclePaths.HostArchitecture(s.Probe);

        var versions = catalogPackage.Versions
            .OrderByDescending(v => NuGetVersion.Parse(v.Version))
            .Select(v => new InfoVersion(v.Version, v.IsPrerelease, v.Architectures,
                v.Dependencies.Select(d => $"{d.Id} {d.Range}").ToList()))
            .ToList();

        var latest = catalogPackage.Versions
            .Where(v => !v.IsPrerelease && HostArchitectureRules.Select(host, v.Architectures) is not null)
            .Select(v => NuGetVersion.Parse(v.Version))
            .OrderByDescending(v => v)
            .FirstOrDefault()?.ToNormalizedString();

        var installed = await s.Installed.GetInstalledAsync(catalogPackage.Repo, catalogPackage.Id, ct);
        InstalledRow? installedRow = null;
        if (installed is not null)
        {
            var shims = (await s.Installed.GetShimsForAsync(installed.Repo, installed.Id, ct)).Select(r => r.Name).ToList();
            installedRow = new InstalledRow(Identifiers.Qualified(installed.Repo, installed.Id), installed.Repo, installed.Id,
                installed.Version, installed.Arch, installed.Explicit, shims);
        }

        bool? writesToInstallDir = null;
        var manifestVersion = installed?.Version ?? latest;
        if (manifestVersion is not null)
        {
            var entry = catalogPackage.Versions.FirstOrDefault(v => v.Version == manifestVersion);
            var bytes = await s.Files.ReadFileAsync(
                LifecyclePaths.CachedManifest(s.Layout, catalogPackage.Repo, catalogPackage.Id, manifestVersion), ct);
            if (entry is not null && (bytes is null || Sha256Hex(bytes) != entry.ManifestSha256))
            {
                // A9: the writesToInstallDir warning needs the manifest. Read-only: it is
                // fetched into memory, bound by the signed index digest, never cached.
                bytes = offline ? null : await FetchManifestAsync(catalogPackage.Repo, catalogPackage.Id, entry, ct);
            }

            if (entry is not null && bytes is not null && Sha256Hex(bytes) == entry.ManifestSha256)
            {
                try
                {
                    writesToInstallDir = ManifestParser.Parse(bytes, s.Flags.TestHooksBuild).WritesToInstallDir;
                }
                catch (WinBrewException)
                {
                    writesToInstallDir = null;
                }
            }
        }

        if (writesToInstallDir == true)
        {
            warnings.Add($"{catalogPackage.Id} writes configuration or state inside its install directory; " +
                         "that state is not carried across upgrades (A3).");
        }

        return new InfoResult(Identifiers.Qualified(catalogPackage.Repo, catalogPackage.Id), catalogPackage.DisplayName,
            catalogPackage.Description, latest, versions, installedRow, writesToInstallDir, warnings);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private async Task<byte[]?> FetchManifestAsync(string repo, string id, CatalogVersion entry, CancellationToken ct)
    {
        var repository = await s.Catalog.GetRepositoryAsync(repo, ct);
        if (repository is null)
        {
            return null;
        }

        var settings = await LifecycleSettings.LoadAsync(s.Config, s.Flags, ct);
        var url = new Uri(new Uri(repository.Url.TrimEnd('/') + "/"), $"manifests/{id}/{entry.Version}.json");
        var (body, result) = await s.Http.GetBytesAsync(new FetchRequest(url, entry.ManifestSize, settings.MaxMetadataBytes,
            settings.DownloadTimeout, settings.DownloadRetries, settings.Localhost), ct);
        if (result.Sha256 != entry.ManifestSha256)
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                $"Manifest digest mismatch for {repo}/{id}@{entry.Version}: index.json says {entry.ManifestSha256}, received {result.Sha256} (D6).");
        }

        return body;
    }
}

public sealed record RecoverResult(bool DryRun, IReadOnlyList<RecoveredOperation> Operations, int PendingFileCleanups);

/// <summary>
/// <c>wbrew recover [--dry-run]</c> (A11). The real run takes the lock and
/// recovers every incomplete operation; exit 8 when any ends RecoveryFailed.
/// --dry-run lists what would be recovered without changing anything.
/// </summary>
public sealed class RecoverUseCase(MutationSession session, IOperationJournal journal, RecoveryService recovery)
{
    public async Task<RecoverResult> ExecuteAsync(bool dryRun, CancellationToken ct)
    {
        if (dryRun)
        {
            var pending = await journal.GetIncompleteAsync(ct);
            return new RecoverResult(true, pending.Select(p => new RecoveredOperation(
                p.OpId, OperationTypeWire.ToWire(p.Type), OperationStateWire.ToWire(p.State),
                p.State == OperationState.StateCommitted ? "Completed" : "RolledBack",
                p.State == OperationState.StateCommitted ? "would roll forward" : "would compensate",
                null)).ToList(), 0);
        }

        using var scope = await session.BeginAsync("recover", ct, failOnRecoveryFailure: false);
        var remaining = await recovery.CleanupJournaledFilesAsync(ct);

        var failed = scope.Recovered.Where(r => r.ToState == "RecoveryFailed").ToList();
        if (failed.Count > 0)
        {
            throw new WinBrewException(WinBrewErrorCode.PartialOrRecoveryRequired,
                "Recovery could not reach a consistent state: " + string.Join(" | ", failed.Select(f => f.Error)),
                new Dictionary<string, object?> { ["recoveryFailed"] = failed.Select(f => f.OpId).ToList() });
        }

        return new RecoverResult(false, scope.Recovered, remaining);
    }
}
