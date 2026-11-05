namespace WinBrew.Application.Ports;

/// <summary>
/// Fault-injection seam (A11). The production implementation does nothing.
/// In test-hooks builds (-p:WinBrewTestHooks=true) the implementation reads
/// WINBREW_FAULT_AT=&lt;name&gt; (with optional &lt;name&gt;#N occurrence syntax,
/// PLAN §1) and terminates the process via Environment.FailFast at the
/// named checkpoint. This one seam is compiled out of release builds.
/// </summary>
public interface IFaultInjector
{
    ValueTask CheckpointAsync(string name, CancellationToken ct);
}

/// <summary>Always-on no-op used by release builds (A11).</summary>
public sealed class NoOpFaultInjector : IFaultInjector
{
    public static readonly NoOpFaultInjector Instance = new();

    public ValueTask CheckpointAsync(string name, CancellationToken ct) => ValueTask.CompletedTask;
}
