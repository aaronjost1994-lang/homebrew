using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Operations;

namespace WinBrew.Application.Lifecycle;

/// <summary>What recovery did to one operation.</summary>
public sealed record RecoveredOperation(
    string OpId,
    string Type,
    string FromState,
    string ToState,
    string Action,
    string? Error);

/// <summary>
/// Journal recovery (A11). Runs under the mutation lock at the start of
/// every mutating command and in <c>wbrew recover</c>. Operations that
/// reached StateCommitted roll FORWARD; anything earlier is COMPENSATED
/// (walking the recorded intents in reverse, every action idempotent).
/// When a consistent state cannot be reached the operation is marked
/// RecoveryFailed with an exact report and the command exits 8 — it never
/// claims success.
/// </summary>
public sealed class RecoveryService(LifecycleServices s)
{
    public async Task<IReadOnlyList<RecoveredOperation>> RecoverAllAsync(CancellationToken ct)
    {
        var results = new List<RecoveredOperation>();
        foreach (var op in await s.Journal.GetIncompleteAsync(ct))
        {
            results.Add(await RecoverAsync(op, ct));
        }

        await CleanupJournaledFilesAsync(ct);
        return results;
    }

    /// <summary>Recovers one incomplete operation (also used by a failing command to undo itself).</summary>
    public async Task<RecoveredOperation> RecoverAsync(OperationRecord op, CancellationToken ct)
    {
        var from = OperationStateWire.ToWire(op.State);
        var type = OperationTypeWire.ToWire(op.Type);
        var steps = await s.Journal.GetStepsAsync(op.OpId, ct);
        var journaled = new JournaledOperation(s.Journal, s.Faults, op.OpId);
        journaled.AssumeState(op.State);

        try
        {
            if (op.State == OperationState.StateCommitted)
            {
                await RollForwardAsync(op, journaled, steps, ct);
                return new RecoveredOperation(op.OpId, type, from, "Completed", "rolled forward", null);
            }

            await CompensateAsync(journaled, steps, ct);
            return new RecoveredOperation(op.OpId, type, from, "RolledBack", "compensated", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var report = $"Recovery of {type} operation {op.OpId} (state {from}) failed: {ex.Message}";
            await journaled.TransitionAsync(OperationState.RecoveryFailed, CancellationToken.None, report);
            await journaled.CheckpointAsync(FaultCheckpoints.RecoveryFailed, CancellationToken.None);
            return new RecoveredOperation(op.OpId, type, from, "RecoveryFailed", "failed", report);
        }
    }

    // ---- compensation (state < StateCommitted) --------------------------------

    private async Task CompensateAsync(JournaledOperation op, IReadOnlyList<OperationStep> steps, CancellationToken ct)
    {
        if (op.State != OperationState.Compensating)
        {
            await op.TransitionAsync(OperationState.Compensating, ct);
            await op.CheckpointAsync(FaultCheckpoints.CompensateStarted, ct);
        }

        foreach (var step in steps.OrderByDescending(st => st.Seq))
        {
            switch (step.Name)
            {
                case FaultCheckpoints.ShimSidecarReplaced:
                    await RevertSidecarAsync(JournalJson.Read<SidecarIntent>(step.IntentJson), ct);
                    await op.CheckpointAsync(FaultCheckpoints.CompensateShimReverted, ct);
                    break;

                case FaultCheckpoints.ShimBinaryReplaced:
                    var binary = JournalJson.Read<BinaryIntent>(step.IntentJson);
                    if (!binary.BinaryExisted)
                    {
                        // We created it: remove it (a running copy is renamed to a journaled .old).
                        await s.Shims.RemoveBinaryAsync(binary.Name,
                            (path, token) => op.RecordFileAsync(step.Seq, path, "old_shim", token), ct);
                    }

                    // An existing binary that was replaced by a newer template copy stays:
                    // every template copy is a valid shim, and ownership lives in the database (D4).
                    break;

                case FaultCheckpoints.PackagePlaced:
                    await RevertPlacementAsync(JournalJson.Read<PlaceIntent>(step.IntentJson), ct);
                    await op.CheckpointAsync(FaultCheckpoints.CompensatePackageRemoved, ct);
                    break;

                case FaultCheckpoints.SelfInstallAppCopied:
                case FaultCheckpoints.SelfUpdateAppCopied:
                    var self = JournalJson.Read<SelfInstallIntent>(step.IntentJson);
                    await s.Files.DeleteDirectoryAsync(self.TemporaryDirectory, ct);
                    if (!self.AppDirectoryExisted)
                    {
                        await s.Files.DeleteDirectoryAsync(self.AppDirectory, ct);
                    }

                    break;

                case FaultCheckpoints.DownloadStarted:
                    await s.Files.DeleteDirectoryAsync(JournalJson.Read<StagingIntent>(step.IntentJson).StagingDirectory, ct);
                    break;

                default:
                    // plan/context rows and staging-only steps need no compensation:
                    // everything they touched lives under staging\<op>\ (removed above)
                    // or in the content-addressed cache (valid by construction).
                    break;
            }
        }

        await op.TransitionAsync(OperationState.RolledBack, ct);
        await op.CheckpointAsync(FaultCheckpoints.RollbackCompleted, ct);
    }

    private async Task RevertSidecarAsync(SidecarIntent intent, CancellationToken ct)
    {
        if (intent.PreviousSidecarBase64 is null)
        {
            await s.Shims.DeleteSidecarAsync(intent.Name, ct);
            return;
        }

        var previous = Convert.FromBase64String(intent.PreviousSidecarBase64);
        var current = await s.Shims.ReadSidecarAsync(intent.Name, ct);
        if (current is null || !current.AsSpan().SequenceEqual(previous))
        {
            await s.Shims.WriteSidecarAsync(intent.Name, previous, ct);
        }
    }

    private async Task RevertPlacementAsync(PlaceIntent intent, CancellationToken ct)
    {
        if (intent.DisplacedDirectory is not null && s.Files.DirectoryExists(intent.DisplacedDirectory))
        {
            await s.Files.DeleteDirectoryAsync(intent.PackageDirectory, ct);
            await s.Files.MoveDirectoryAsync(intent.DisplacedDirectory, intent.PackageDirectory, ct);
            return;
        }

        if (intent.DisplacedDirectory is null)
        {
            // Under the lock, no package directory existed at intent time:
            // whatever exists there now was placed by this operation.
            await s.Files.DeleteDirectoryAsync(intent.PackageDirectory, ct);
            await PruneEmptyParentsAsync(intent.PackageDirectory, ct);
        }
    }

    /// <summary>Removes packages\&lt;id&gt;\&lt;version&gt;\ and packages\&lt;id&gt;\ when compensation left them EMPTY (never anything else).</summary>
    private async Task PruneEmptyParentsAsync(string packageDirectory, CancellationToken ct)
    {
        var packagesRoot = Path.GetFullPath(LifecyclePaths.PackagesRoot(s.Layout));
        var dir = Path.GetDirectoryName(Path.GetFullPath(packageDirectory));
        for (var level = 0; level < 2 && dir is not null; level++)
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(packagesRoot), StringComparison.OrdinalIgnoreCase) ||
                !s.Files.IsEmptyDirectory(dir))
            {
                return;
            }

            await s.Files.DeleteDirectoryAsync(dir, ct);
            dir = Path.GetDirectoryName(dir);
        }
    }

    // ---- roll forward (state == StateCommitted) ---------------------------------

    private async Task RollForwardAsync(
        OperationRecord record, JournaledOperation op, IReadOnlyList<OperationStep> steps, CancellationToken ct)
    {
        switch (record.Type)
        {
            case OperationType.Install:
            case OperationType.Upgrade:
            case OperationType.SelfUpdate:
                var commit = steps.LastOrDefault(st => st.Name == FaultCheckpoints.StateCommitted)
                    ?? throw new InvalidDataException("the state.committed step is missing from the journal");
                await PostCommit.FinishInstallAsync(s, op, JournalJson.Read<CommitIntent>(commit.IntentJson), ct);
                break;

            case OperationType.Uninstall:
                var plan = steps.FirstOrDefault(st => st.Name == JournalStepNames.Plan)
                    ?? throw new InvalidDataException("the uninstall plan step is missing from the journal");
                await PostCommit.FinishUninstallAsync(s, op, JournalJson.Read<UninstallIntent>(plan.IntentJson), ct);
                break;

            case OperationType.SelfInstall:
                var selfStep = steps.FirstOrDefault(st => st.Name == FaultCheckpoints.SelfInstallAppCopied)
                    ?? throw new InvalidDataException("the self-install step is missing from the journal");
                await PostCommit.FinishSelfInstallAsync(s, op, JournalJson.Read<SelfInstallIntent>(selfStep.IntentJson), ct);
                break;

            default:
                throw new InvalidDataException(
                    $"operation type '{OperationTypeWire.ToWire(record.Type)}' has no recovery handler");
        }
    }

    // ---- journaled file cleanup (D4 + PLAN §1) -----------------------------------------

    /// <summary>
    /// Deletes ONLY recorded files (never pattern matches) whose operation is
    /// terminal; files still locked stay recorded and are retried later.
    /// </summary>
    public async Task<int> CleanupJournaledFilesAsync(CancellationToken ct)
    {
        var remaining = 0;
        foreach (var file in await s.Journal.GetPendingCleanupAsync(ct))
        {
            bool deleted;
            if (file.Kind == "old_dir" || s.Files.DirectoryExists(file.Path))
            {
                try
                {
                    await s.Files.DeleteDirectoryAsync(file.Path, ct);
                    deleted = true;
                }
                catch (WinBrewException)
                {
                    deleted = false;
                }
            }
            else
            {
                deleted = await s.Files.TryDeleteFileAsync(file.Path, ct);
            }

            if (deleted)
            {
                await s.Journal.MarkFileDeletedAsync(file, ct);
            }
            else
            {
                remaining++;
            }
        }

        return remaining;
    }
}

/// <summary>Step names that are not fault checkpoints.</summary>
public static class JournalStepNames
{
    public const string Plan = "plan";
}
