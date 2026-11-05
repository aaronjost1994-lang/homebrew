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

public sealed record InstallRequest(string Package, string? VersionOption, bool DryRun, bool Offline);

/// <summary>The operation plan shown before consent (A8, A11 step 2).</summary>
/// <param name="Action">install | upgrade | none (already installed).</param>
public sealed record InstallPlan(
    string Action,
    string Repo,
    string Id,
    string Version,
    string Arch,
    string? PreviousVersion,
    string? PreviousArch,
    bool ArchitectureFallback,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings);

public sealed record InstallResult(
    InstallPlan Plan,
    bool Changed,
    bool DryRun,
    string? PackageDirectory,
    IReadOnlyList<string> Shims,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings,
    IReadOnlyList<RecoveredOperation> Recovered);

/// <summary>Renders the plan and returns consent (the CLI shows it; --yes approves; non-interactive → exit 10).</summary>
public delegate Task<bool> PlanApproval(InstallPlan plan, CancellationToken ct);

/// <summary>
/// <c>wbrew install</c> for single packages with no dependencies (Phase B
/// scope; the PubGrub resolver arrives in Phase C). Planning reads ONLY the
/// SQLite catalog (D2/D6): the manifest is fetched after planning, verified
/// against the catalog digest, and must match the catalog entry exactly
/// (A10). Installing a different version of an installed package is an
/// upgrade: the previous version's directory and shims stay intact until
/// StateCommitted, and compensation points the shims back (A11).
/// </summary>
public sealed class InstallUseCase(LifecycleServices s, MutationSession session, RecoveryService recovery)
{
    private const string StagingDownloadName = "download.tmp";
    private const string StagingExtractName = "extract";

    public async Task<InstallResult> ExecuteAsync(InstallRequest request, PlanApproval approve, CancellationToken ct)
    {
        var parsed = PackageLookup.Parse(request.Package, request.VersionOption);

        if (request.DryRun)
        {
            // --dry-run: the full plan from the EXISTING catalog; no lock, no
            // downloads, no state writes, no shims, never a metadata refresh (A8).
            var dryPlan = await PlanAsync(parsed, ct);
            return new InstallResult(dryPlan, Changed: false, DryRun: true, null, [], dryPlan.Warnings, []);
        }

        using var scope = await session.BeginAsync("install", ct);
        var plan = await PlanAsync(parsed, ct);

        if (plan.Action == "none")
        {
            return new InstallResult(plan, Changed: false, DryRun: false, null, [], plan.Warnings, scope.Recovered);
        }

        if (!await approve(plan, ct))
        {
            throw new OperationCanceledException("The operation plan was not approved.");
        }

        return await RunAsync(plan, request.Offline, scope, ct);
    }

    // ---- planning (catalog only) -------------------------------------------------------

    private async Task<InstallPlan> PlanAsync(PackageRequest request, CancellationToken ct)
    {
        var package = await PackageLookup.FindAsync(s.Catalog, request, ct);
        var host = LifecyclePaths.HostArchitecture(s.Probe);
        var (version, arch) = PackageLookup.SelectVersion(package, request.Version, host);
        var qualified = Identifiers.Qualified(package.Repo, package.Id);

        if (version.Dependencies.Count > 0)
        {
            throw new WinBrewException(WinBrewErrorCode.GeneralFailure,
                $"{qualified}@{version.Version} has dependencies; installing packages with dependencies needs " +
                "the PubGrub resolver (NotAvailableInThisBuild; planned for Phase C).",
                new Dictionary<string, object?> { ["reason"] = "NotAvailableInThisBuild", ["phase"] = "Phase C (resolver)" });
        }

        var warnings = new List<string>();
        if (arch.IsFallback)
        {
            warnings.Add($"Architecture fallback: {qualified}@{version.Version} has no {HostArchitectureRules.HostName(host)} " +
                         $"artifact; using {arch.Architecture} (A10 order {string.Join(" → ", HostArchitectureRules.SelectionOrder(host))}).");
        }

        var installed = await s.Installed.GetInstalledAsync(package.Repo, package.Id, ct);
        var action = installed is null ? "install"
            : installed.Version == version.Version && installed.Arch == arch.Architecture ? "none"
            : "upgrade";

        if (installed is not null && installed.Arch != arch.Architecture && action == "upgrade")
        {
            warnings.Add($"Architecture re-selection: {installed.Arch} → {arch.Architecture} (A10).");
        }

        return new InstallPlan(action, package.Repo, package.Id, version.Version, arch.Architecture,
            installed?.Version, installed?.Arch, arch.IsFallback, warnings);
    }

    // ---- execution (journaled, A11) -----------------------------------------------------

    private async Task<InstallResult> RunAsync(InstallPlan plan, bool offline, MutationScope scope, CancellationToken ct)
    {
        var settings = scope.Settings;
        var warnings = new List<string>(plan.Warnings);
        var catalogPackage = await s.Catalog.GetPackageAsync(plan.Repo, plan.Id, ct)
            ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"{plan.Repo}/{plan.Id} vanished from the catalog.");
        var entry = catalogPackage.Versions.Single(v => v.Version == plan.Version);
        var repository = await s.Catalog.GetRepositoryAsync(plan.Repo, ct)
            ?? throw new WinBrewException(WinBrewErrorCode.NotFound, $"Repository '{plan.Repo}' is not configured.");

        var type = plan.Action == "upgrade" ? OperationType.Upgrade : OperationType.Install;
        var op = await JournaledOperation.StartAsync(s.Journal, s.Faults, type, $"install {plan.Repo}/{plan.Id}@{plan.Version}", ct);
        var staging = LifecyclePaths.StagingDirectory(s.Layout, op.OpId);
        var packageDir = LifecyclePaths.PackageDirectory(s.Layout, plan.Id, plan.Version, plan.Arch);
        var shimNames = new List<string>();

        try
        {
            await op.RecordAsync(JournalStepNames.Plan, new PlanIntent(plan.Repo, plan.Id, plan.Version, plan.Arch,
                plan.PreviousVersion, plan.PreviousArch, staging), ct);

            // A6/A11 step 3: staging\<op>\ on the same volume as packages\.
            await op.StepAsync(FaultCheckpoints.DownloadStarted, new StagingIntent(staging),
                (_, token) => s.Files.CreateDirectoryAsync(staging, token), ct);

            // Manifest: bound to the catalog by digest, exact-match checked (D6, A10).
            var manifest = await FetchVerifiedManifestAsync(plan, repository, entry, settings, offline, ct);
            var artifact = manifest.Artifacts.Single(a => a.Architecture == plan.Arch);
            if (artifact.Format != "zip" || artifact.InstallType != "portable")
            {
                throw new WinBrewException(WinBrewErrorCode.GeneralFailure,
                    $"{plan.Repo}/{plan.Id}@{plan.Version} ({plan.Arch}) is a {artifact.InstallType}/{artifact.Format} package; " +
                    "MSI/EXE installers arrive in v1.1 (NotAvailableInThisBuild, Phase E).");
            }

            if (manifest.WritesToInstallDir)
            {
                warnings.Add($"{plan.Id} writes to its install directory; that state is not kept across upgrades (A3).");
            }

            // A11 steps 3–4: download (or verified cache hit), SHA-256 before extraction, cache.
            var cachedArtifact = await AcquireArtifactAsync(op, artifact, staging, settings, offline, ct);

            // A11 step 5: extract with every A13 defense.
            var extractDir = Path.Combine(staging, StagingExtractName);
            await op.StepAsync(FaultCheckpoints.ExtractCompleted, new StagingIntent(extractDir),
                (_, token) => s.Extractor.ExtractZipAsync(cachedArtifact, extractDir, settings.ArchiveLimits, token),
                ct, OperationState.Extracted);

            // A11 step 6: validate executables and shim collisions.
            var shimPlans = await ValidateAsync(plan, manifest, extractDir, packageDir, warnings, ct);
            shimNames.AddRange(shimPlans.Select(p => p.Name));
            await op.StepAsync(FaultCheckpoints.ValidateCompleted, new StagingIntent(extractDir),
                (_, _) => Task.CompletedTask, ct, OperationState.Validated);

            // A11 step 7: rename into packages\<id>\<version>\<arch>\.
            await PlaceAsync(op, extractDir, packageDir, ct);

            // A11 step 8: shims (sidecar first; binary only when new or the template changed).
            var shimRecords = await ActivateShimsAsync(op, plan, shimPlans, ct);
            await op.TransitionAsync(OperationState.ShimsActivated, ct);
            await op.CheckpointAsync(FaultCheckpoints.PackageActivated, ct);

            // A11: StateCommitted — one SQLite transaction (installed row, shims, operation state).
            var previousShims = plan.PreviousVersion is null ? [] : await s.Installed.GetShimsForAsync(plan.Repo, plan.Id, ct);
            var stale = previousShims
                .Where(p => !shimPlans.Any(n => string.Equals(n.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Name)
                .ToList();
            var prune = ComputePruneList(plan, settings.RetainVersions);
            var previous = await s.Installed.GetInstalledAsync(plan.Repo, plan.Id, ct);
            var now = LifecyclePaths.Now(s.Time);
            var commit = new InstallCommit(
                new InstalledPackage(plan.Repo, plan.Id, plan.Version, plan.Arch, Explicit: true,
                    InstalledAt: previous?.InstalledAt ?? now, UpdatedAt: now),
                shimRecords,
                stale);
            var commitIntent = new CommitIntent(stale, prune, staging);

            await op.StepAsync(FaultCheckpoints.StateCommitted, commitIntent, async (_, token) =>
            {
                await s.Installed.CommitInstallAsync(op.OpId, commit, token);
                op.AssumeState(OperationState.StateCommitted);
            }, ct);

            // Post-commit work is roll-forward only; a failure here leaves the
            // operation for recovery and never undoes the committed install.
            try
            {
                await PostCommit.FinishInstallAsync(s, op, commitIntent, ct);
                await recovery.CleanupJournaledFilesAsync(ct);
            }
            catch (Exception ex) when (ex is WinBrewException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Installed, but post-install cleanup did not finish ({ex.Message}); " +
                             "the next mutating command or 'wbrew recover' completes it.");
            }

            return new InstallResult(plan, Changed: true, DryRun: false, packageDir, shimNames, warnings, scope.Recovered);
        }
        catch (Exception ex) when (op.State is not (OperationState.StateCommitted or OperationState.Completed))
        {
            await UndoAsync(op, ex);
            throw;
        }
    }

    /// <summary>Compensates a failed install immediately (A11: staging cleaned up, state consistent).</summary>
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

    private async Task<Manifest> FetchVerifiedManifestAsync(
        InstallPlan plan, RepositoryRecord repository, CatalogVersion entry, LifecycleSettings settings, bool offline, CancellationToken ct)
    {
        var cachedPath = LifecyclePaths.CachedManifest(s.Layout, plan.Repo, plan.Id, plan.Version);
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
                    $"--offline: the manifest of {plan.Repo}/{plan.Id}@{plan.Version} is not in the verified cache (A15).");
            }

            var url = new Uri(new Uri(repository.Url.TrimEnd('/') + "/"), $"manifests/{plan.Id}/{plan.Version}.json");
            var (body, result) = await s.Http.GetBytesAsync(new FetchRequest(
                url, entry.ManifestSize, Math.Min(settings.MaxMetadataBytes, Math.Max(entry.ManifestSize, 0)),
                settings.DownloadTimeout, settings.DownloadRetries, settings.Localhost), ct);

            if (result.Sha256 != entry.ManifestSha256)
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"Manifest digest mismatch for {plan.Repo}/{plan.Id}@{plan.Version}: index.json says " +
                    $"{entry.ManifestSha256}, received {result.Sha256} (D6).",
                    new Dictionary<string, object?> { ["expected"] = entry.ManifestSha256, ["actual"] = result.Sha256 });
            }

            bytes = body;
        }

        var manifest = ManifestParser.Parse(bytes, s.Flags.TestHooksBuild);
        EnsureMatchesCatalog(plan, manifest, entry);

        // Keep the verified manifest (cache\metadata\<repo>\, A6) for info and --offline.
        if (cached is null || !cached.AsSpan().SequenceEqual(bytes))
        {
            await s.Files.WriteFileAtomicAsync(cachedPath, bytes, ct);
        }

        return manifest;
    }

    /// <summary>A10: the manifest's id, version, dependencies and architectures must EXACTLY match the catalog entry.</summary>
    private static void EnsureMatchesCatalog(InstallPlan plan, Manifest manifest, CatalogVersion entry)
    {
        var where = $"{plan.Repo}/{plan.Id}@{plan.Version}";
        var problems = new List<string>();

        if (manifest.Id != plan.Id)
        {
            problems.Add($"id '{manifest.Id}' ≠ '{plan.Id}'");
        }

        if (manifest.Version != plan.Version)
        {
            problems.Add($"version '{manifest.Version}' ≠ '{plan.Version}'");
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
            problems.Add("dependencies differ from index.json");
        }

        if (problems.Count > 0)
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                $"Manifest of {where} does not match its index.json entry: {string.Join("; ", problems)} (A10, D6).");
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
            throw new WinBrewException(WinBrewErrorCode.NetworkFailure,
                "--offline: the artifact is not in the verified cache (A15).");
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

    private sealed record ShimPlan(string Name, string TargetPath, string Subsystem);

    private async Task<List<ShimPlan>> ValidateAsync(
        InstallPlan plan, Manifest manifest, string extractDir, string packageDir, List<string> warnings, CancellationToken ct)
    {
        var host = LifecyclePaths.HostArchitecture(s.Probe);
        var ignoreCase = OperatingSystem.IsWindows();
        var extractRoot = Path.GetFullPath(extractDir);
        var plans = new List<ShimPlan>();

        foreach (var exe in manifest.Executables)
        {
            var relative = exe.Target.Replace('/', Path.DirectorySeparatorChar);
            var staged = Path.GetFullPath(Path.Combine(extractRoot, relative));
            if (!ArchiveEntryRules.IsContained(extractRoot, staged, ignoreCase))
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

            await CheckCollisionAsync(plan, exe.Name, ct);
            WarnIfEarlierOnPath(exe.Name, warnings);

            plans.Add(new ShimPlan(exe.Name, Path.Combine(packageDir, relative), subsystem));
        }

        var deepest = s.Files.DeepestPathLength(extractDir, packageDir);
        if (deepest > 260)
        {
            warnings.Add($"The deepest installed path is {deepest} characters (> 260); tools without long-path support may fail (A12).");
        }

        return plans;
    }

    /// <summary>A12/D4: collisions with another package's shim or an unmanaged file are refused (exit 6).</summary>
    private async Task CheckCollisionAsync(InstallPlan plan, string name, CancellationToken ct)
    {
        if (string.Equals(name, ShimSidecar.SelfShimName, StringComparison.OrdinalIgnoreCase))
        {
            throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                $"Shim name '{name}' is reserved for WinBrew itself (D4).");
        }

        var record = await s.Installed.GetShimAsync(name, ct);
        if (record is not null)
        {
            if (record.OwnerRepo != plan.Repo || record.OwnerPackage != plan.Id)
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
        var bin = PathListNormalize(s.Shims.BinDirectory);
        foreach (var entry in entries)
        {
            if (PathListNormalize(entry) == bin)
            {
                return; // bin\ comes first from here on
            }

            if (s.Files.FileExists(Path.Combine(entry, name + ".exe")))
            {
                warnings.Add($"'{name}' also exists at {Path.Combine(entry, name + ".exe")}, earlier on PATH than WinBrew's bin\\ (A12).");
                return;
            }
        }
    }

    private static string PathListNormalize(string entry) => Core.PathList.NormalizeForCompare(entry);

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

    private async Task<List<ShimRecord>> ActivateShimsAsync(JournaledOperation op, InstallPlan plan, List<ShimPlan> shims, CancellationToken ct)
    {
        var records = new List<ShimRecord>();
        if (shims.Count == 0)
        {
            return records;
        }

        var template = await LifecyclePaths.FindShimTemplateAsync(s, ct);
        var templateSha = await s.Files.Sha256Async(template, ct)
            ?? throw new WinBrewException(WinBrewErrorCode.GeneralFailure, $"Shim template '{template}' vanished.");

        foreach (var shim in shims.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var sidecar = new ShimSidecar(shim.TargetPath, Identifiers.Qualified(plan.Repo, plan.Id), plan.Version, shim.Subsystem).ToUtf8Bytes();
            var previous = await s.Shims.ReadSidecarAsync(shim.Name, ct);

            await op.StepAsync(FaultCheckpoints.ShimSidecarReplaced,
                new SidecarIntent(shim.Name, previous is null ? null : Convert.ToBase64String(previous), Convert.ToBase64String(sidecar)),
                (_, token) => s.Shims.WriteSidecarAsync(shim.Name, sidecar, token), ct);

            // Upgrade of a managed shim whose binary already matches the template:
            // only the sidecar changes, so a running shim never blocks (D4).
            var existed = s.Shims.BinaryExists(shim.Name);
            await op.StepAsync(FaultCheckpoints.ShimBinaryReplaced, new BinaryIntent(shim.Name, existed),
                (seq, token) => s.Shims.PlaceBinaryAsync(shim.Name, template, templateSha,
                    (path, t) => op.RecordFileAsync(seq, path, "old_shim", t), token), ct);

            records.Add(new ShimRecord(shim.Name, plan.Repo, plan.Id, plan.Version, shim.TargetPath, shim.Subsystem, templateSha));
        }

        return records;
    }

    /// <summary>
    /// retainVersions (D10): inactive versions kept for recovery, highest
    /// first; everything older is pruned after commit (A10 cleanup rule:
    /// never the active version).
    /// </summary>
    private List<string> ComputePruneList(InstallPlan plan, int retainVersions)
    {
        var root = LifecyclePaths.PackageRoot(s.Layout, plan.Id);
        var inactive = new List<(NuGetVersion Version, string Dir)>();

        foreach (var versionName in s.Files.ListDirectories(root))
        {
            if (!NuGetVersion.TryParse(versionName, out var parsed))
            {
                continue; // .old.<guid> leftovers are journal-owned, never pattern-deleted
            }

            foreach (var archName in s.Files.ListDirectories(Path.Combine(root, versionName)))
            {
                if (versionName == plan.Version && archName == plan.Arch)
                {
                    continue;
                }

                if (archName is "x64" or "arm64" or "x86")
                {
                    inactive.Add((parsed, Path.Combine(root, versionName, archName)));
                }
            }
        }

        var keepVersions = inactive
            .Select(i => i.Version)
            .Distinct()
            .OrderByDescending(v => v)
            .Take(retainVersions)
            .ToHashSet();

        return inactive.Where(i => !keepVersions.Contains(i.Version)).Select(i => i.Dir).ToList();
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
}
