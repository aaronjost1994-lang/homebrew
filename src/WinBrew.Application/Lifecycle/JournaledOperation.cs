using WinBrew.Application.Ports;
using WinBrew.Core.Operations;

namespace WinBrew.Application.Lifecycle;

/// <summary>
/// One journaled operation (A11). Every durable transition follows the same
/// order: intent row → filesystem action → outcome row → fault checkpoint.
/// A crash at a checkpoint therefore always finds the action AND its
/// outcome recorded; a crash inside an action finds only the intent, and
/// every compensating/roll-forward action is idempotent, so recovery is
/// correct either way.
/// </summary>
public sealed class JournaledOperation(IOperationJournal journal, IFaultInjector faults, string opId)
{
    public string OpId { get; } = opId;

    public OperationState State { get; private set; } = OperationState.Planned;

    public static async Task<JournaledOperation> StartAsync(
        IOperationJournal journal, IFaultInjector faults, OperationType type, string command, CancellationToken ct)
    {
        var opId = Guid.NewGuid().ToString("N");
        await journal.CreateAsync(opId, type, command, ct);
        return new JournaledOperation(journal, faults, opId);
    }

    /// <summary>
    /// Records intent, runs the action, records the outcome, optionally moves
    /// the operation to <paramref name="reach"/>, then fires the checkpoint.
    /// </summary>
    public async Task<T> StepAsync<TIntent, T>(
        string checkpoint,
        TIntent intent,
        Func<int, CancellationToken, Task<T>> action,
        CancellationToken ct,
        OperationState? reach = null)
    {
        var seq = await journal.BeginStepAsync(OpId, checkpoint, JournalJson.Write(intent), State, ct);
        var result = await action(seq, ct);
        await journal.CompleteStepAsync(OpId, seq, JournalJson.Write(StepOutcome.Done), ct);
        if (reach is OperationState next)
        {
            await TransitionAsync(next, ct);
        }

        await faults.CheckpointAsync(checkpoint, ct);
        return result;
    }

    public async Task StepAsync<TIntent>(
        string checkpoint,
        TIntent intent,
        Func<int, CancellationToken, Task> action,
        CancellationToken ct,
        OperationState? reach = null)
    {
        await StepAsync<TIntent, bool>(checkpoint, intent, async (seq, token) =>
        {
            await action(seq, token);
            return true;
        }, ct, reach);
    }

    /// <summary>A step with no filesystem action (pure plan/context record). No checkpoint fires.</summary>
    public async Task RecordAsync<TIntent>(string name, TIntent intent, CancellationToken ct)
    {
        var seq = await journal.BeginStepAsync(OpId, name, JournalJson.Write(intent), State, ct);
        await journal.CompleteStepAsync(OpId, seq, JournalJson.Write(StepOutcome.Done), ct);
    }

    /// <summary>Moves the operation to a new state (written before the matching checkpoint fires).</summary>
    public async Task TransitionAsync(OperationState state, CancellationToken ct, string? error = null)
    {
        await journal.SetStateAsync(OpId, state, error, ct);
        State = state;
    }

    /// <summary>
    /// Sets the state without touching the journal — used after a SQLite
    /// transaction that wrote the state itself (StateCommitted).
    /// </summary>
    public void AssumeState(OperationState state) => State = state;

    public Task RecordFileAsync(int seq, string path, string kind, CancellationToken ct) =>
        journal.RecordFileAsync(OpId, seq, path, kind, ct);

    public ValueTask CheckpointAsync(string name, CancellationToken ct) => faults.CheckpointAsync(name, ct);
}
