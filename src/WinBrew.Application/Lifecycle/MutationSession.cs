using WinBrew.Application.Ports;
using WinBrew.Core;

namespace WinBrew.Application.Lifecycle;

/// <summary>An open mutating session: the held lock plus what startup recovery did.</summary>
public sealed class MutationScope(ILockHandle lockHandle, LifecycleSettings settings, IReadOnlyList<RecoveredOperation> recovered) : IDisposable
{
    public LifecycleSettings Settings { get; } = settings;

    public IReadOnlyList<RecoveredOperation> Recovered { get; } = recovered;

    public void Dispose() => lockHandle.Dispose();
}

/// <summary>
/// Startup of every mutating command (A11, D5): load settings, take the
/// mutation lock, ensure the layout, apply migrations, then recover any
/// incomplete operation BEFORE the command does its own work. A recovery
/// that ends in RecoveryFailed stops the command with exit 8.
/// </summary>
public sealed class MutationSession(
    IMutationLock mutationLock,
    IStorageLayout layout,
    IStateDatabase database,
    IConfigStore config,
    RuntimeFlags flags,
    RecoveryService recovery)
{
    public async Task<MutationScope> BeginAsync(string command, CancellationToken ct, bool failOnRecoveryFailure = true)
    {
        var settings = await LifecycleSettings.LoadAsync(config, flags, ct);
        var handle = await mutationLock.AcquireAsync(command, settings.LockTimeout, ct);
        try
        {
            await layout.EnsureAsync(ct);
            await database.ApplyMigrationsAsync(ct);
            var recovered = await recovery.RecoverAllAsync(ct);

            var failed = recovered.Where(r => r.ToState == "RecoveryFailed").ToList();
            if (failed.Count > 0 && failOnRecoveryFailure)
            {
                throw new WinBrewException(
                    WinBrewErrorCode.PartialOrRecoveryRequired,
                    "An interrupted operation could not be recovered to a consistent state: " +
                    string.Join(" | ", failed.Select(f => f.Error)),
                    new Dictionary<string, object?>
                    {
                        ["recoveryFailed"] = failed.Select(f => f.OpId).ToList(),
                    });
            }

            return new MutationScope(handle, settings, recovered);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }
}
