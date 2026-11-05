namespace WinBrew.Core.Operations;

/// <summary>
/// Registry of fault-injection checkpoint names (A11). Names are static and
/// enumerable so the test suite can enumerate them and never hardcode a
/// count. The registry grows as phases implement durable transitions;
/// additions are backward-compatible (PLAN §6).
///
/// Phase A registers the seam only — no durable package/journal transitions
/// exist yet (the lifecycle lands in Phase B, trust/catalog in Phase D). The
/// first named checkpoints therefore arrive with Phase B:
/// download.started, download.completed, artifact.verified, artifact.cached…
/// </summary>
public static class FaultCheckpoints
{
    /// <summary>All registered checkpoint names, sorted. Tests enumerate this list.</summary>
    public static IReadOnlyList<string> All { get; } = [];

    /// <summary>Throws when a phase tries to fire an unregistered name — keeps the registry authoritative.</summary>
    public static void EnsureRegistered(string name)
    {
        if (!All.Contains(name, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Fault checkpoint '{name}' is not registered in {nameof(FaultCheckpoints)}. " +
                "Register durable-transition names here so tests can enumerate them (A11).");
        }
    }
}
