using WinBrew.Application.Ports;
using WinBrew.Core.Operations;

namespace WinBrew.Infrastructure.FaultInjection;

/// <summary>
/// Fault-injection seam (A11). Production: no-op. Test-hooks builds
/// (-p:WinBrewTestHooks=true): reads WINBREW_FAULT_AT=&lt;name&gt; or
/// &lt;name&gt;#N (occurrence suffix, PLAN §1 — deterministic per-shim faults)
/// and terminates via Environment.FailFast at the matching checkpoint.
/// Compiled out of release builds entirely.
/// </summary>
public static class FaultInjectorFactory
{
    public static IFaultInjector Create(bool testHooksBuild)
    {
#if WINBREW_TEST_HOOKS
        return testHooksBuild ? TestHooksFaultInjector.Instance : NoOpFaultInjector.Instance;
#else
        _ = testHooksBuild;
        return NoOpFaultInjector.Instance;
#endif
    }
}

#if WINBREW_TEST_HOOKS
/// <summary>Environment-variable-driven FailFast at registered checkpoints (A11).</summary>
public sealed class TestHooksFaultInjector : IFaultInjector
{
    public static readonly TestHooksFaultInjector Instance = new();

    private readonly string? _targetName;
    private readonly int _targetOccurrence;
    private readonly Dictionary<string, int> _firings = new(StringComparer.Ordinal);

    private TestHooksFaultInjector()
    {
        var raw = Environment.GetEnvironmentVariable("WINBREW_FAULT_AT");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var hash = raw.IndexOf('#');
        if (hash > 0)
        {
            _targetName = raw[..hash];
            _targetOccurrence = int.TryParse(raw[(hash + 1)..], out var n) && n > 0 ? n : 1;
        }
        else
        {
            _targetName = raw;
            _targetOccurrence = 1;
        }
    }

    public ValueTask CheckpointAsync(string name, CancellationToken ct)
    {
        // The registry is authoritative: firing an unregistered name is a bug.
        FaultCheckpoints.EnsureRegistered(name);

        if (_targetName is null || !string.Equals(name, _targetName, StringComparison.Ordinal))
        {
            return ValueTask.CompletedTask;
        }

        _firings.TryGetValue(name, out var count);
        count++;
        _firings[name] = count;

        if (count == _targetOccurrence)
        {
            Console.Error.WriteLine(
                $"[fault-injection] FailFast at checkpoint '{name}' (occurrence {count}).");
            Environment.FailFast($"Fault injection at checkpoint '{name}' (occurrence {count}).");
        }

        return ValueTask.CompletedTask;
    }
}
#endif
