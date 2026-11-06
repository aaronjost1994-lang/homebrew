using NuGet.Versioning;
using WinBrew.Application.Lifecycle;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Archive;
using WinBrew.Core.Artifacts;
using WinBrew.Core.Manifests;
using WinBrew.Core.Operations;
using WinBrew.Core.Pe;
using WinBrew.Core.Shims;

namespace WinBrew.Application.Packages;

/// <summary>What an executed plan changed.</summary>
public sealed record ExecutionResult(IReadOnlyList<string> Shims, IReadOnlyList<string> Warnings);

/// <summary>
/// Executes an <see cref="OperationPlan"/> as ONE journaled operation (A11):
/// every changing package goes through manifest (digest + exact catalog
/// match, A10) → artifact (download or verified cache, SHA-256) → extract
/// (A13) → validate (PE, collisions) → place → shims, in dependency order;
/// then a SINGLE StateCommitted transaction records every package, shim and
/// dependency edge; then roll-forward cleanup. Any failure before the commit
/// compensates the WHOLE plan (previous versions and shims stay usable).
/// </summary>
public sealed class PlanExecutor(LifecycleServices s, RecoveryService recovery)
{
    private const string StagingDownloadName = "download.tmp";
    private const string StagingExtractName = "extract";

    private sealed record ShimPlan(string Name, string TargetPath, string Subsystem);

    private sealed record Prepared(PlanItem Item, string PackageDirectory, List<ShimPlan> Shims);

    public async Task<ExecutionResult> ExecuteAsync(OperationPlan plan, MutationScope scope, bool offline, CancellationToken ct)
    {
        var settings = scope.Settings;
        var warnings = new List<string>(plan.Warnings);
        var changing = plan.Items.Where(i => i.Changes).ToList();

        // A10/D6: every manifest is fetched, digest-checked and matched against its
        // catalog entry BEFORE the operation starts — a mismatch (exit 5) changes nothing.
        var manifests = new Dictionary<string, Manifest>(StringComparer.Ordinal);
        var verifiedBytes = new List<(string Path, byte[] Bytes)>();
        foreach (var item in changing)
        {
            var (manifest, cachePath, bytes) = await FetchVerifiedManifestAsync(item, settings, offline, ct);
            manifests[item.Package] = manifest;
            if (bytes is not null)
            {
                verifiedBytes.Add((cachePath, bytes));
            }
        }

        foreach (var (path, bytes) in verifiedBytes)
        {
            await s.Files.WriteFileAtomicAsync(path, bytes, ct); // cache\metadata: verified manifests only (A6)
        }

        var type = changing.Any(i => i.Action == "upgrade") ? OperationType.Upgrade : OperationType.Install;
        var op = await JournaledOperation.StartAsync(s.Journal, s.Faults, type,
            $"{plan.Command} " + string.Join(" ", changing.Select(i => $"{i.Package}@{i.Version}")), ct);
        var staging = LifecyclePaths.StagingDirectory(s.Layout, op.OpId);
        var shimNames = new List<string>();

        try
        {
            foreach (var item in changing)
            {
                await op.RecordAsync(JournalStepNames.Plan, new PlanIntent(item.Repo, item.Id, item.Version, item.Arch,
                    item.PreviousVersion, item.PreviousArch, staging), ct);
            }

            if (changing.Count > 0)
            {
                // A6/A11 step 3: staging\<op>\ on the same volume as packages\.
                await op.StepAsync(FaultCheckpoints.DownloadStarted, new StagingIntent(staging),
                    (_, token) => s.Files.CreateDirectoryAsync(staging, token), ct);
            }

            var prepared = new List<Prepared>();
            var claimedShims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in changing)
            {
                prepared.Add(await PreparePackageAsync(op, item, manifests[item.Package], staging, settings, offline, claimedShims, warnings, ct));
            }

            var template = prepared.Any(p => p.Shims.Count > 0) ? await LifecyclePaths.FindShimTemplateAsync(s, ct) : null;
            var templateSha = template is null ? null : await s.Files.Sha256Async(template, ct);

            var commits = new List<InstallCommit>();
            var stale = new List<string>();
            var prune = new List<string>();
            var now = LifecyclePaths.Now(s.Time);

            foreach (var p in prepared)
            {
                await PlaceAsync(op, Path.Combine(staging, p.Item.Id, StagingExtractName), p.PackageDirectory, ct);
                var records = await ActivateShimsAsync(op, p.Item, p.Shims, template, templateSha, ct);
                shimNames.AddRange(p.Shims.Select(x => x.Name));
                await op.TransitionAsync(OperationState.ShimsActivated, ct);
                await op.CheckpointAsync(FaultCheckpoints.PackageActivated, ct);

                var previousShims = p.Item.PreviousVersion is null ? [] : await s.Installed.GetShimsForAsync(p.Item.Repo, p.Item.Id, ct);
                var removed = previousShims
                    .Where(old => !p.Shims.Any(n => string.Equals(n.Name, old.Name, StringComparison.OrdinalIgnoreCase)))
                    .Select(old => old.Name)
                    .ToList();
                stale.AddRange(removed);
                prune.AddRange(ComputePruneList(p.Item, settings.RetainVersions));

                var previous = await s.Installed.GetInstalledAsync(p.Item.Repo, p.Item.Id, ct);
                commits.Add(new InstallCommit(
                    new InstalledPackage(p.Item.Repo, p.Item.Id, p.Item.Version, p.Item.Arch, p.Item.Explicit, previous?.InstalledAt ?? now, now),
                    records, removed, p.Item.Dependencies));
            }

            // Packages that keep their version but must become explicit (requested by name).
            foreach (var item in plan.Items.Where(i => !i.Changes && i.PromoteToExplicit))
            {
                var current = await s.Installed.GetInstalledAsync(item.Repo, item.Id, ct);
                commits.Add(new InstallCommit(
                    new InstalledPackage(item.Repo, item.Id, item.Version, item.Arch, true, current?.InstalledAt ?? now, now),
                    await s.Installed.GetShimsForAsync(item.Repo, item.Id, ct), [], item.Dependencies));
            }

            if (op.State == OperationState.Planned && changing.Count == 0)
            {
                await op.TransitionAsync(OperationState.ShimsActivated, ct);
            }

            var commitIntent = new CommitIntent(stale, prune, changing.Count > 0 ? staging : null);
            await op.StepAsync(FaultCheckpoints.StateCommitted, commitIntent, async (_, token) =>
            {
                await s.Installed.CommitPlanAsync(op.OpId, commits, token);
                op.AssumeState(OperationState.StateCommitted);
            }, ct);

            try
            {
                await PostCommit.FinishInstallAsync(s, op, commitIntent, ct);
                await recovery.CleanupJournaledFilesAsync(ct);
            }
            catch (Exception ex) when (ex is WinBrewException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Changes are committed, but cleanup did not finish ({ex.Message}); " +
                             "the next mutating command or 'wbrew recover' completes it.");
            }

            return new ExecutionResult(shimNames, warnings);
        }
        catch (Exception ex) when (op.State is not (OperationState.StateCommitted or OperationState.Completed))
        {
            await UndoAsync(op, ex);
            throw;
        }
    }

    /// <summary>Compensates the whole plan immediately (A11: previous versions usable, staging cleaned).</summary>
    private async Task UndoAsync(JournaledOperation op, Exception original)
    {
        var record = await s.Journal.GetAsync(op.OpId, CancellationToken.None);
        if (record is null)
        {
            return;
        }

        var result = await recovery.RecoverAsync(record, CancellationToken.None);
        if (result.ToState == "RecoveryFailed")
        {
            throw new WinBrewException(WinBrewErrorCode.PartialOrRecoveryRequired,
                $"{original.Message} — and undoing the operation failed: {result.Error}",
                new Dictionary<string, object?> { ["opId"] = op.OpId });
        }
    }

    // ---- per package: manifest → artifact → extract → validate --------------------------------

    private async Task<Prepared> PreparePackageAsync(
        JournaledOperation op, PlanItem item, Manifest manifest, string staging, LifecycleSettings settings, bool offline,
        Dictionary<string, string> claimedShims, List<string> warnings, CancellationToken ct)
    {
        var artifact = manifest.Artifacts.Single(a => a.Architecture == item.Arch);
        if (artifact.Format != "zip" || artifact.InstallType != "portable")
        {
            throw new WinBrewException(WinBrewErrorCode.GeneralFailure,
                $"{item.Package}@{item.Version} ({item.Arch}) is a {artifact.InstallType}/{artifact.Format} package; " +
                "MSI/EXE installers arrive in v1.1 (NotAvailableInThisBuild, Phase E).");
        }

        if (manifest.WritesToInstallDir)
        {
            warnings.Add($"{item.Id} writes to its install directory; that state is not kept across upgrades (A3).");
        }

        var packageStaging = Path.Combine(staging, item.Id);
        await s.Files.CreateDirectoryAsync(packageStaging, ct);
        var cachedArtifact = await AcquireArtifactAsync(op, artifact, packageStaging, settings, offline, ct);

        var extractDir = Path.Combine(packageStaging, StagingExtractName);
        await op.StepAsync(FaultCheckpoints.ExtractCompleted, new StagingIntent(extractDir),
            (_, token) => s.Extractor.ExtractZipAsync(cachedArtifact, extractDir, settings.ArchiveLimits, token),
            ct, OperationState.Extracted);

        var packageDir = LifecyclePaths.PackageDirectory(s.Layout, item.Id, item.Version, item.Arch);
        var shims = await ValidateAsync(item, manifest, extractDir, packageDir, claimedShims, warnings, ct);
        await op.StepAsync(FaultCheckpoints.ValidateCompleted, new StagingIntent(extractDir),
            (_, _) => Task.CompletedTask, ct, OperationState.Validated);

        return new Prepared(item, packageDir, shims);
    }

    /// <summary>Returns the verified manifest, its cache path, and the bytes to cache (null when the cached copy is already current).</summary>
    private async Task<(Manifest Manifest, string CachePath, byte[]? ToCache)> FetchVerifiedManifestAsync(
        PlanItem item, LifecycleSettings settings, bool offline, CancellationToken ct)
    {
        var catalogPackage = await s.Catalog.GetPackageAsync(item.Repo, item.Id, ct)
            ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"{item.Package} vanished from the catalog.");
        var entry = catalogPackage.Versions.Single(v => v.Version == item.Version);
        var repository = await s.Catalog.GetRepositoryAsync(item.Repo, ct)
            ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"Repository '{item.Repo}' is not configured.");

        var cachedPath = LifecyclePaths.CachedManifest(s.Layout, item.Repo, item.Id, item.Version);
        byte[]? bytes = null;

        var cached = await s.Files.ReadFileAsync(cachedPath, ct);
        if (cached is not null && Sha256Hex(cached) == entry.ManifestSha256)
        {
            bytes = cached;
        }

        if (bytes is null)
        {
            if (offline)
            {
                throw new WinBrewException(WinBrewErrorCode.NetworkFailure,
                    $"--offline: the manifest of {item.Package}@{item.Version} is not in the verified cache (A15).");
            }

            var url = new Uri(new Uri(repository.Url.TrimEnd('/') + "/"), $"manifests/{item.Id}/{item.Version}.json");
            var (body, result) = await s.Http.GetBytesAsync(new FetchRequest(
                url, entry.ManifestSize, Math.Min(settings.MaxMetadataBytes, Math.Max(entry.ManifestSize, 0)),
                settings.DownloadTimeout, settings.DownloadRetries, settings.Localhost), ct);

            if (result.Sha256 != entry.ManifestSha256)
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"Manifest digest mismatch for {item.Package}@{item.Version}: index.json says " +
                    $"{entry.ManifestSha256}, received {result.Sha256} (D6).",
                    new Dictionary<string, object?> { ["expected"] = entry.ManifestSha256, ["actual"] = result.Sha256 });
            }

            bytes = body;
        }

        var manifest = ManifestParser.Parse(bytes, s.Flags.TestHooksBuild);
        EnsureMatchesCatalog(item, manifest, entry);
        return (manifest, cachedPath, cached is not null && cached.AsSpan().SequenceEqual(bytes) ? null : bytes);
    }

    /// <summary>A10: the manifest's id, version, dependencies and architectures must EXACTLY match the catalog entry (exit 5).</summary>
    public static void EnsureMatchesCatalog(PlanItem item, Manifest manifest, CatalogVersion entry)
    {
        var problems = new List<string>();
        if (manifest.Id != item.Id)
        {
            problems.Add($"id '{manifest.Id}' ≠ '{item.Id}'");
        }

        if (manifest.Version != item.Version)
        {
            problems.Add($"version '{manifest.Version}' ≠ '{item.Version}'");
        }

        var manifestArchs = manifest.Artifacts.Select(a => a.Architecture).OrderBy(a => a, StringComparer.Ordinal).ToList();
        var catalogArchs = entry.Architectures.OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (!manifestArchs.SequenceEqual(catalogArchs, StringComparer.Ordinal))
        {
            problems.Add($"architectures [{string.Join(",", manifestArchs)}] ≠ index [{string.Join(",", catalogArchs)}]");
        }

        var manifestDeps = manifest.Dependencies.Select(d => (d.Id, d.Range)).ToList();
        var catalogDeps = entry.Dependencies.Select(d => (d.Id, d.Range)).ToList();
        if (!manifestDeps.SequenceEqual(catalogDeps))
        {
            problems.Add($"dependencies [{string.Join(", ", manifestDeps.Select(d => $"{d.Id} {d.Range}"))}] ≠ index " +
                         $"[{string.Join(", ", catalogDeps.Select(d => $"{d.Id} {d.Range}"))}]");
        }

        if (problems.Count > 0)
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                $"Manifest of {item.Package}@{item.Version} does not match its index.json entry: {string.Join("; ", problems)} (A10, D6).");
        }
    }

    private async Task<string> AcquireArtifactAsync(
        JournaledOperation op, ManifestArtifact artifact, string staging, LifecycleSettings settings, bool offline, CancellationToken ct)
    {
        var cached = await s.Cache.TryGetVerifiedAsync(artifact.Sha256, ct);
        if (cached is not null)
        {
            await op.TransitionAsync(OperationState.Downloaded, ct);
            await op.TransitionAsync(OperationState.Verified, ct);
            return cached;
        }

        if (offline)
        {
            throw new WinBrewException(WinBrewErrorCode.NetworkFailure, "--offline: the artifact is not in the verified cache (A15).");
        }

        if (artifact.Size > settings.DownloadMaxBytes)
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                $"The artifact declares {artifact.Size} bytes, above download.maxBytes ({settings.DownloadMaxBytes}).");
        }

        var tmp = Path.Combine(staging, StagingDownloadName);
        var url = new Uri(artifact.Url);
        var intent = new ArtifactIntent(Core.Net.UrlPolicy.Redact(url), artifact.Sha256, artifact.Size);

        var fetched = await op.StepAsync(FaultCheckpoints.DownloadCompleted, intent,
            (_, token) => s.Http.DownloadToFileAsync(new FetchRequest(url, artifact.Size, settings.DownloadMaxBytes,
                settings.DownloadTimeout, settings.DownloadRetries, settings.Localhost), tmp, token),
            ct, OperationState.Downloaded);

        await op.StepAsync(FaultCheckpoints.ArtifactVerified, intent, async (_, token) =>
        {
            if (fetched.Sha256 != artifact.Sha256)
            {
                await s.Files.DeleteFileAsync(tmp, token);
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"Checksum mismatch: expected {artifact.Sha256}, downloaded {fetched.Sha256} (A11 step 4).",
                    new Dictionary<string, object?> { ["expected"] = artifact.Sha256, ["actual"] = fetched.Sha256 });
            }
        }, ct, OperationState.Verified);

        return await op.StepAsync(FaultCheckpoints.ArtifactCached, intent,
            (_, token) => s.Cache.AddVerifiedAsync(tmp, artifact.Sha256, token), ct);
    }

    private async Task<List<ShimPlan>> ValidateAsync(
        PlanItem item, Manifest manifest, string extractDir, string packageDir,
        Dictionary<string, string> claimedShims, List<string> warnings, CancellationToken ct)
    {
        var host = LifecyclePaths.HostArchitecture(s.Probe);
        var extractRoot = Path.GetFullPath(extractDir);
        var plans = new List<ShimPlan>();

        foreach (var exe in manifest.Executables)
        {
            var relative = exe.Target.Replace('/', Path.DirectorySeparatorChar);
            var staged = Path.GetFullPath(Path.Combine(extractRoot, relative));
            if (!ArchiveEntryRules.IsContained(extractRoot, staged, OperatingSystem.IsWindows()))
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"Executable '{exe.Name}' target '{exe.Target}' escapes the package directory.");
            }

            if (!s.Files.IsRegularFile(staged))
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"Executable '{exe.Name}': '{exe.Target}' is missing from the archive or is not a regular file (A11 step 6).");
            }

            var pe = await s.Files.ReadPeInfoAsync(staged, ct)
                ?? throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"Executable '{exe.Name}': '{exe.Target}' is not a valid PE image (A11 step 6).");

            if (!HostArchitectureRules.IsAcceptedMachine(host, pe.Machine))
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"Executable '{exe.Name}': PE machine {HostArchitectureRules.MachineName(pe.Machine)} does not run on this " +
                    $"{HostArchitectureRules.HostName(host)} host (A10).");
            }

            var subsystem = pe.SubsystemKind switch
            {
                PeSubsystemKind.Console => ShimSidecar.Console,
                PeSubsystemKind.Gui => ShimSidecar.Gui,
                _ => throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"Executable '{exe.Name}': PE subsystem {pe.Subsystem} is neither console nor GUI."),
            };

            if (claimedShims.TryGetValue(exe.Name, out var other))
            {
                throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                    $"Shim collision inside the plan: '{exe.Name}' is provided by both {other} and {item.Package} (A12).");
            }

            claimedShims[exe.Name] = item.Package;
            await CheckCollisionAsync(item, exe.Name, ct);
            WarnIfEarlierOnPath(exe.Name, warnings);
            plans.Add(new ShimPlan(exe.Name, Path.Combine(packageDir, relative), subsystem));
        }

        var deepest = s.Files.DeepestPathLength(extractDir, packageDir);
        if (deepest > 260)
        {
            warnings.Add($"{item.Package}: the deepest installed path is {deepest} characters (> 260); tools without long-path support may fail (A12).");
        }

        return plans;
    }

    /// <summary>A12/D4: collisions with another package's shim or an unmanaged file are refused (exit 6).</summary>
    private async Task CheckCollisionAsync(PlanItem item, string name, CancellationToken ct)
    {
        if (string.Equals(name, ShimSidecar.SelfShimName, StringComparison.OrdinalIgnoreCase))
        {
            throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused, $"Shim name '{name}' is reserved for WinBrew itself (D4).");
        }

        var record = await s.Installed.GetShimAsync(name, ct);
        if (record is not null)
        {
            if (record.OwnerRepo != item.Repo || record.OwnerPackage != item.Id)
            {
                throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                    $"Shim collision: '{name}' is already provided by {record.OwnerRepo}/{record.OwnerPackage} (A12).",
                    new Dictionary<string, object?> { ["shim"] = name, ["owner"] = $"{record.OwnerRepo}/{record.OwnerPackage}" });
            }

            return;
        }

        if (s.Shims.BinaryExists(name) || s.Files.FileExists(s.Shims.SidecarPath(name)))
        {
            throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                $"Shim collision: bin\\{name}.exe exists but is not managed by WinBrew; unmanaged files are never overwritten (D4, A12).",
                new Dictionary<string, object?> { ["shim"] = name, ["owner"] = null });
        }
    }

    private void WarnIfEarlierOnPath(string name, List<string> warnings)
    {
        var entries = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
        var bin = PathList.NormalizeForCompare(s.Shims.BinDirectory);
        foreach (var entry in entries)
        {
            if (PathList.NormalizeForCompare(entry) == bin)
            {
                return;
            }

            if (s.Files.FileExists(Path.Combine(entry, name + ".exe")))
            {
                warnings.Add($"'{name}' also exists at {Path.Combine(entry, name + ".exe")}, earlier on PATH than WinBrew's bin\\ (A12).");
                return;
            }
        }
    }

    private async Task PlaceAsync(JournaledOperation op, string extractDir, string packageDir, CancellationToken ct)
    {
        var displaced = s.Files.DirectoryExists(packageDir) ? packageDir + ".old." + Guid.NewGuid().ToString("N") : null;
        await op.StepAsync(FaultCheckpoints.PackagePlaced, new PlaceIntent(packageDir, extractDir, displaced), async (seq, token) =>
        {
            if (displaced is not null)
            {
                await op.RecordFileAsync(seq, displaced, "old_dir", token);
                await s.Files.MoveDirectoryAsync(packageDir, displaced, token);
            }

            await s.Files.CreateDirectoryAsync(Path.GetDirectoryName(packageDir)!, token);
            await s.Files.MoveDirectoryAsync(extractDir, packageDir, token);
        }, ct, OperationState.Placed);
    }

    private async Task<List<ShimRecord>> ActivateShimsAsync(
        JournaledOperation op, PlanItem item, List<ShimPlan> shims, string? template, string? templateSha, CancellationToken ct)
    {
        var records = new List<ShimRecord>();
        foreach (var shim in shims.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var sidecar = new ShimSidecar(shim.TargetPath, item.Package, item.Version, shim.Subsystem).ToUtf8Bytes();
            var previous = await s.Shims.ReadSidecarAsync(shim.Name, ct);

            await op.StepAsync(FaultCheckpoints.ShimSidecarReplaced,
                new SidecarIntent(shim.Name, previous is null ? null : Convert.ToBase64String(previous), Convert.ToBase64String(sidecar)),
                (_, token) => s.Shims.WriteSidecarAsync(shim.Name, sidecar, token), ct);

            // A managed binary that already matches the template is left alone: a running shim never blocks (D4).
            var existed = s.Shims.BinaryExists(shim.Name);
            await op.StepAsync(FaultCheckpoints.ShimBinaryReplaced, new BinaryIntent(shim.Name, existed),
                (seq, token) => s.Shims.PlaceBinaryAsync(shim.Name, template!, templateSha!,
                    (path, t) => op.RecordFileAsync(seq, path, "old_shim", t), token), ct);

            records.Add(new ShimRecord(shim.Name, item.Repo, item.Id, item.Version, shim.TargetPath, shim.Subsystem, templateSha!));
        }

        return records;
    }

    /// <summary>retainVersions (D10): inactive versions kept for recovery, highest first; older ones pruned after commit — never the active version.</summary>
    private List<string> ComputePruneList(PlanItem item, int retainVersions)
    {
        var root = LifecyclePaths.PackageRoot(s.Layout, item.Id);
        var inactive = new List<(NuGetVersion Version, string Dir)>();

        foreach (var versionName in s.Files.ListDirectories(root))
        {
            if (!NuGetVersion.TryParse(versionName, out var parsed))
            {
                continue; // .old.<guid> leftovers are journal-owned, never pattern-deleted
            }

            foreach (var archName in s.Files.ListDirectories(Path.Combine(root, versionName)))
            {
                if ((versionName == item.Version && archName == item.Arch) || archName is not ("x64" or "arm64" or "x86"))
                {
                    continue;
                }

                inactive.Add((parsed, Path.Combine(root, versionName, archName)));
            }
        }

        var keep = inactive.Select(i => i.Version).Distinct().OrderByDescending(v => v).Take(retainVersions).ToHashSet();
        return inactive.Where(i => !keep.Contains(i.Version)).Select(i => i.Dir).ToList();
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
}
