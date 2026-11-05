using WinBrew.Core.Operations;

namespace WinBrew.Application.Lifecycle;

/// <summary>
/// Work after StateCommitted (A11). Shared by the normal command path and
/// by recovery's roll-forward, so a crash anywhere in here is finished by
/// the next mutating command. Every action is idempotent.
/// </summary>
public static class PostCommit
{
    /// <summary>Install/upgrade: stale shims, version pruning (retainVersions), staging cleanup → Completed.</summary>
    public static async Task FinishInstallAsync(LifecycleServices s, JournaledOperation op, CommitIntent intent, CancellationToken ct)
    {
        foreach (var name in intent.StaleShims)
        {
            await RemoveShimAsync(s, op, name, ct);
        }

        foreach (var dir in intent.PruneDirectories)
        {
            await s.Files.DeleteDirectoryAsync(dir, ct);
            var versionDir = Path.GetDirectoryName(dir);
            if (versionDir is not null && s.Files.IsEmptyDirectory(versionDir))
            {
                await s.Files.DeleteDirectoryAsync(versionDir, ct);
            }
        }

        if (intent.StagingDirectory is not null)
        {
            await s.Files.DeleteDirectoryAsync(intent.StagingDirectory, ct);
        }

        await CompleteAsync(op, ct);
    }

    /// <summary>Uninstall: shims, then the whole packages\&lt;id&gt;\ tree → Completed.</summary>
    public static async Task FinishUninstallAsync(LifecycleServices s, JournaledOperation op, UninstallIntent intent, CancellationToken ct)
    {
        foreach (var name in intent.Shims)
        {
            await RemoveShimAsync(s, op, name, ct);
        }

        await op.StepAsync(FaultCheckpoints.UninstallPackageRemoved, intent,
            (_, token) => s.Files.DeleteDirectoryAsync(intent.PackageRoot, token), ct);

        await CompleteAsync(op, ct);
    }

    /// <summary>Self install: the consented PATH change (idempotent: no duplicates, A12) → Completed.</summary>
    public static async Task FinishSelfInstallAsync(LifecycleServices s, JournaledOperation op, SelfInstallIntent intent, CancellationToken ct)
    {
        if (intent.ModifyPath && s.UserPath.IsSupported)
        {
            await op.StepAsync(FaultCheckpoints.PathUpdated, new PathIntent(intent.BinDirectory), async (_, token) =>
            {
                await s.UserPath.AddAsync(intent.BinDirectory, token);
                await SetPathManagedAsync(s, token);
            }, ct);
        }

        await CompleteAsync(op, ct);
    }

    /// <summary>Removes bin\&lt;name&gt;.shim.json and bin\&lt;name&gt;.exe (a running binary becomes a journaled .old).</summary>
    public static Task RemoveShimAsync(LifecycleServices s, JournaledOperation op, string name, CancellationToken ct) =>
        op.StepAsync(FaultCheckpoints.UninstallShimRemoved, new ShimRemoveIntent(name), async (seq, token) =>
        {
            await s.Shims.DeleteSidecarAsync(name, token);
            await s.Shims.RemoveBinaryAsync(name, (path, t) => op.RecordFileAsync(seq, path, "old_shim", t), token);
        }, ct);

    public static async Task SetPathManagedAsync(LifecycleServices s, CancellationToken ct)
    {
        var doc = await s.Config.LoadAsync(ct);
        if (doc.Values.TryGetValue("path.managed", out var v) && v is true)
        {
            return;
        }

        var values = new Dictionary<string, object?>(doc.Values, StringComparer.Ordinal) { ["path.managed"] = true };
        await s.Config.SaveAsync(values, ct);
    }

    private static async Task CompleteAsync(JournaledOperation op, CancellationToken ct)
    {
        await op.TransitionAsync(OperationState.Completed, ct);
        await op.CheckpointAsync(FaultCheckpoints.OperationCompleted, ct);
    }
}
