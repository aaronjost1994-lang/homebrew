using NuGet.Versioning;
using WinBrew.Application.Catalog;
using WinBrew.Application.Lifecycle;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Artifacts;
using WinBrew.Core.Operations;
using WinBrew.Core.Release;
using WinBrew.Core.Shims;
using WinBrew.Core.Trust;

namespace WinBrew.Application.SelfManagement;

public sealed record SelfUpdateResult(
    string CurrentVersion,
    string? AvailableVersion,
    bool Updated,
    bool DryRun,
    string? AppDirectory,
    int ShimsReplaced,
    int FeedRootVersion,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string> Warnings);

/// <summary>Approves "update WinBrew &lt;current&gt; → &lt;next&gt;" (A8: --yes approves the displayed plan).</summary>
public delegate Task<bool> SelfUpdateApproval(string current, string next, CancellationToken ct);

/// <summary>
/// <c>wbrew self update [--dry-run]</c> (A14). The signed release feed uses
/// the SAME envelope, roles and key model as D6: the initial release root
/// compiled into the build (N=1) or the last accepted feed root, walked
/// N→N+1 (each rotation cross-signed), then timestamp → releases index. The
/// new version is downloaded (SHA-256), extracted with the A13 defenses,
/// placed in app\&lt;newver&gt;\ including its wbrew-shim.exe template, and then
/// ONE journaled bulk operation switches the wbrew sidecar and replaces every
/// shim copy with the new template (D4). The running executable is never
/// overwritten; the previous app\&lt;ver&gt;\ stays.
/// </summary>
public sealed class SelfUpdateUseCase(
    LifecycleServices s, MutationSession session, RecoveryService recovery, RepositoryMetadataService metadata,
    IReleaseFeed feed, IVersionInfo version)
{
    private const string DownloadName = "wbrew-release.zip";

    public async Task<SelfUpdateResult> ExecuteAsync(bool dryRun, SelfUpdateApproval approve, CancellationToken ct)
    {
        var configuration = feed.Configuration
            ?? throw new WinBrewException(WinBrewErrorCode.NotFound, "self-update not configured for this build",
                new Dictionary<string, object?> { ["reason"] = "NoReleaseFeed" });
        var current = SelfInstallUseCase.AppVersionName(version.Version);

        if (dryRun)
        {
            // Read-only: verify the feed and report; nothing (not even the feed's trust state) is written.
            var settings = await LifecycleSettings.LoadAsync(s.Config, s.Flags, ct);
            var (_, _, dryChoice, dryRoot) = await VerifyFeedAsync(configuration, settings, ct);
            return new SelfUpdateResult(current, dryChoice?.Version, false, true, null, 0, dryRoot, []);
        }

        using var scope = await session.BeginAsync("self update", ct);
        var (state, index, choice, rootVersion) = await VerifyFeedAsync(configuration, scope.Settings, ct);
        await s.Catalog.CommitSelfUpdateTrustAsync(state, ct);

        if (choice is null)
        {
            return new SelfUpdateResult(current, null, false, false, null, 0, rootVersion, [$"WinBrew {current} is up to date (feed index v{index.Version})."]);
        }

        // The wbrew shim already starts that version (this process is the previous one).
        if (await s.Shims.ReadSidecarAsync(ShimSidecar.SelfShimName, ct) is { } bytes &&
            ShimSidecar.TryParse(bytes) is { } active && active.Version == choice.Version)
        {
            return new SelfUpdateResult(current, choice.Version, false, false, LifecyclePaths.AppDirectory(s.Layout, choice.Version), 0, rootVersion,
                [$"WinBrew {choice.Version} is already installed and starts from the wbrew shim; this process is {current} (A14)."]);
        }

        if (!await approve(current, choice.Version, ct))
        {
            throw new OperationCanceledException("The self update was not approved.");
        }

        var (appDir, shims) = await InstallAsync(choice, scope.Settings, ct);
        return new SelfUpdateResult(current, choice.Version, true, false, appDir, shims, rootVersion,
            [$"WinBrew {choice.Version} is active; {current} stays in place until it is no longer running (A14)."]);
    }

    private sealed record Choice(string Version, ReleaseArtifact Artifact);

    private async Task<(SelfUpdateTrustState State, ReleaseIndex Index, Choice? Choice, int RootVersion)> VerifyFeedAsync(
        ReleaseFeedConfiguration configuration, LifecycleSettings settings, CancellationToken ct)
    {
        var now = s.Time.GetUtcNow();
        var skew = settings.MaxClockSkew;
        var url = configuration.Url.ToString().TrimEnd('/');

        var initial = TrustChain.LoadAccepted(configuration.InitialRoot);
        var stored = await s.Catalog.GetSelfUpdateTrustAsync(ct);
        var start = stored is not null && stored.RootVersion >= initial.Version ? TrustChain.LoadAccepted(stored.RootEnvelope) : initial;

        var (root, _) = await metadata.WalkRootsAsync(url, start, settings, ct);
        TrustChain.EnsureCurrent(root, now, skew);

        var timestampBytes = await metadata.GetAsync(url, "timestamp.json", null, settings, ct)
            ?? throw new WinBrewException(WinBrewErrorCode.NotFound, "The release feed has no timestamp.json (A14).");
        var timestamp = TrustChain.VerifyTimestamp(root, timestampBytes, stored?.TimestampVersion, now, skew);
        if (timestamp.Index.Length > settings.MaxMetadataBytes)
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                $"The release index declares {timestamp.Index.Length} bytes, above trust.maxMetadataBytes (A14, D6).");
        }

        var indexBytes = await metadata.GetAsync(url, TrustRoles.IndexFile, timestamp.Index.Length, settings, ct)
            ?? throw new WinBrewException(WinBrewErrorCode.NotFound, "The release feed has no index.json (A14).");
        var index = ReleaseIndexParser.Parse(TrustChain.VerifyIndexEnvelope(root, indexBytes, timestamp.Index, TrustRoles.ReleasesPayloadType));
        TrustChain.EnsureIndexFresh(index.Version, index.Expires, stored?.IndexVersion, now, skew);

        var state = new SelfUpdateTrustState(root.Envelope, root.Version, timestamp.Version, index.Version, now);
        return (state, index, Choose(index), root.Version);
    }

    /// <summary>The highest release above the running version with an artifact for the host (A10 order); prereleases only from a prerelease.</summary>
    private Choice? Choose(ReleaseIndex index)
    {
        var current = NuGetVersion.TryParse(SelfInstallUseCase.AppVersionName(version.Version), out var v) ? v : new NuGetVersion(0, 0, 0);
        var host = LifecyclePaths.HostArchitecture(s.Probe);
        foreach (var (name, entry) in index.Releases.OrderByDescending(r => NuGetVersion.Parse(r.Key)))
        {
            var candidate = NuGetVersion.Parse(name);
            if (candidate <= current || (candidate.IsPrerelease && !current.IsPrerelease))
            {
                continue;
            }

            var selection = HostArchitectureRules.Select(host, entry.Artifacts.Select(a => a.Architecture));
            if (selection is not null)
            {
                return new Choice(name, entry.Artifacts.Single(a => a.Architecture == selection.Architecture));
            }
        }

        return null;
    }

    private async Task<(string AppDirectory, int Shims)> InstallAsync(Choice choice, LifecycleSettings settings, CancellationToken ct)
    {
        var op = await JournaledOperation.StartAsync(s.Journal, s.Faults, OperationType.SelfUpdate, $"self update {choice.Version}", ct);
        var staging = LifecyclePaths.StagingDirectory(s.Layout, op.OpId);
        var appDir = LifecyclePaths.AppDirectory(s.Layout, choice.Version);
        var intent = new SelfInstallIntent(choice.Version, appDir,
            Path.Combine(s.Layout.Directories["app"], $".{choice.Version}.tmp-{op.OpId}"),
            AppDirectoryExisted: s.Files.DirectoryExists(appDir), ModifyPath: false, BinDirectory: s.Shims.BinDirectory);
        var commit = new CommitIntent([], [], staging);
        var replaced = 0;

        try
        {
            await op.StepAsync(FaultCheckpoints.DownloadStarted, new StagingIntent(staging),
                (_, token) => s.Files.CreateDirectoryAsync(staging, token), ct);

            var zip = Path.Combine(staging, DownloadName);
            var artifact = choice.Artifact;
            var artifactIntent = new ArtifactIntent(Core.Net.UrlPolicy.Redact(new Uri(artifact.Url)), artifact.Sha256, artifact.Size);
            var fetched = await op.StepAsync(FaultCheckpoints.DownloadCompleted, artifactIntent,
                (_, token) => s.Http.DownloadToFileAsync(new FetchRequest(new Uri(artifact.Url), artifact.Size, settings.DownloadMaxBytes,
                    settings.DownloadTimeout, settings.DownloadRetries, settings.Localhost), zip, token),
                ct, OperationState.Downloaded);

            await op.StepAsync(FaultCheckpoints.ArtifactVerified, artifactIntent, (_, _) =>
            {
                if (fetched.Sha256 != artifact.Sha256)
                {
                    throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                        $"The release zip's SHA-256 is {fetched.Sha256}; the signed release index says {artifact.Sha256} (A14).");
                }

                return Task.CompletedTask;
            }, ct, OperationState.Verified);

            var extract = Path.Combine(staging, "extract");
            await op.StepAsync(FaultCheckpoints.ExtractCompleted, new StagingIntent(extract),
                (_, token) => s.Extractor.ExtractZipAsync(zip, extract, settings.ArchiveLimits, token), ct, OperationState.Extracted);

            var missing = SelfInstallUseCase.RequiredFiles.Where(f => !s.Files.IsRegularFile(Path.Combine(extract, f))).ToList();
            if (missing.Count > 0)
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    $"The release zip for {choice.Version} lacks {string.Join(", ", missing)} (A14).");
            }

            await op.StepAsync(FaultCheckpoints.SelfUpdateAppCopied, intent, (_, token) => PlaceAppAsync(extract, intent, token),
                ct, OperationState.Placed);

            // ONE journaled bulk operation: the wbrew sidecar, then every shim copy (D4).
            var template = Path.Combine(appDir, LifecyclePaths.ShimTemplateFileName);
            var templateSha = await s.Files.Sha256Async(template, ct)
                ?? throw new WinBrewException(WinBrewErrorCode.GeneralFailure, $"Shim template '{template}' is missing.");
            var self = ShimSidecar.SelfShimName;
            var sidecar = new ShimSidecar(Path.Combine(appDir, "wbrew.exe"), "winbrew", choice.Version, ShimSidecar.Console).ToUtf8Bytes();
            var previous = await s.Shims.ReadSidecarAsync(self, ct);
            await op.StepAsync(FaultCheckpoints.ShimSidecarReplaced,
                new SidecarIntent(self, previous is null ? null : Convert.ToBase64String(previous), Convert.ToBase64String(sidecar)),
                (_, token) => s.Shims.WriteSidecarAsync(self, sidecar, token), ct);

            var names = new List<string> { self };
            foreach (var package in await s.Installed.ListInstalledAsync(ct))
            {
                names.AddRange((await s.Installed.GetShimsForAsync(package.Repo, package.Id, ct)).Select(r => r.Name));
            }

            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var existed = s.Shims.BinaryExists(name);
                await op.StepAsync(FaultCheckpoints.ShimBinaryReplaced, new BinaryIntent(name, existed),
                    (seq, token) => s.Shims.PlaceBinaryAsync(name, template, templateSha,
                        (path, t) => op.RecordFileAsync(seq, path, "old_shim", t), token), ct);
                replaced++;
            }

            await op.TransitionAsync(OperationState.ShimsActivated, ct);
            await op.StepAsync(FaultCheckpoints.StateCommitted, commit, async (_, token) =>
            {
                await s.Installed.CommitTemplateAsync(op.OpId, templateSha, token);
                op.AssumeState(OperationState.StateCommitted);
            }, ct);
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
                        $"{ex.Message} — and undoing the self update failed: {undo.Error}");
                }
            }

            throw;
        }

        await PostCommit.FinishInstallAsync(s, op, commit, ct);
        await recovery.CleanupJournaledFilesAsync(ct);
        return (appDir, replaced);
    }

    private async Task PlaceAppAsync(string extract, SelfInstallIntent intent, CancellationToken ct)
    {
        if (s.Files.DirectoryExists(intent.AppDirectory))
        {
            foreach (var file in SelfInstallUseCase.RequiredFiles)
            {
                if (await s.Files.Sha256Async(Path.Combine(extract, file), ct) != await s.Files.Sha256Async(Path.Combine(intent.AppDirectory, file), ct))
                {
                    throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                        $"{intent.AppDirectory} already exists with a different '{file}'; WinBrew never overwrites an installed version (A14).");
                }
            }

            return;
        }

        await s.Files.CreateDirectoryAsync(intent.TemporaryDirectory, ct);
        foreach (var file in SelfInstallUseCase.FilesToCopy(s.Files, extract))
        {
            await s.Files.CopyFileAsync(Path.Combine(extract, file), Path.Combine(intent.TemporaryDirectory, file), overwrite: true, ct);
        }

        await s.Files.MoveDirectoryAsync(intent.TemporaryDirectory, intent.AppDirectory, ct);
    }
}
