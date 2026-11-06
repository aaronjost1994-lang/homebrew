using System.Runtime.InteropServices;
using WinBrew.Application.Configuration;
using WinBrew.Application.Doctor;
using WinBrew.Application.Ports;

namespace WinBrew.TestFixtures;

/// <summary>Fake IVersionInfo with a stable identity.</summary>
public sealed class FakeVersionInfo(string version = "0.1.0-test", string rid = "win-x64") : IVersionInfo
{
    public string Version { get; } = version;
    public string RuntimeIdentifier { get; } = rid;
}

/// <summary>
/// Fake IEnvironmentProbe: every answer is configurable so tests can drive
/// the build-vs-OS mismatch, long-paths, holder-liveness and volume checks.
/// </summary>
public sealed class FakeEnvironmentProbe : IEnvironmentProbe
{
    public Architecture? NativeMachine { get; set; } = Architecture.X64;
    public Architecture ProcessArchitecture { get; set; } = Architecture.X64;
    public bool? LongPathsEnabled { get; set; } = true;
    public bool UserPathValueExists { get; set; }
    public bool SameVolumeAnswer { get; set; } = true;
    public Func<int, string?, bool>? IsAliveOverride { get; set; }
    public int LastQueriedPid { get; private set; }

    public bool AreSameVolume(string pathA, string pathB) => SameVolumeAnswer;

    public bool IsProcessAlive(int pid, string? processStartTime)
    {
        LastQueriedPid = pid;
        return IsAliveOverride?.Invoke(pid, processStartTime) ?? true;
    }
}

/// <summary>Fake IStorageLayout over a caller-provided directory map.</summary>
public sealed class FakeStorageLayout(string root, StorageLayoutStatus? status = null) : IStorageLayout
{
    public string Root { get; } = root;

    public IReadOnlyDictionary<string, string> Directories { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["state"] = Path.Combine(root, "state"),
            ["staging"] = Path.Combine(root, "staging"),
            ["packages"] = Path.Combine(root, "packages"),
            ["cache"] = Path.Combine(root, "cache"),
            ["bin"] = Path.Combine(root, "bin"),
            ["logs"] = Path.Combine(root, "logs"),
            ["app"] = Path.Combine(root, "app"),
            ["repositories"] = Path.Combine(root, "repositories"),
            ["backups"] = Path.Combine(root, "state", "backups"),
        };

    public StorageLayoutStatus Status { get; set; } =
        status ?? StorageLayoutStatus.Ok(rootExists: true, allPresent: true);

    public int EnsureCalls { get; private set; }
    public int InspectCalls { get; private set; }

    public Task<StorageLayoutStatus> EnsureAsync(CancellationToken ct)
    {
        EnsureCalls++;
        return Task.FromResult(Status);
    }

    public Task<StorageLayoutStatus> InspectAsync(CancellationToken ct)
    {
        InspectCalls++;
        return Task.FromResult(Status);
    }
}

/// <summary>
/// Fake IConfigStore backed by a dictionary. Problems can be injected to
/// simulate a broken config.json for doctor.
/// </summary>
public sealed class FakeConfigStore : IConfigStore
{
    public Dictionary<string, object?> Values { get; set; } = new(StringComparer.Ordinal);
    public IReadOnlyList<ConfigProblem> Problems { get; set; } = [];
    public int SaveCalls { get; private set; }

    public Task<ConfigDocument> LoadAsync(CancellationToken ct) =>
        Task.FromResult(new ConfigDocument("1", Values));

    public Task<ConfigInspection> InspectAsync(CancellationToken ct) =>
        Task.FromResult(new ConfigInspection(new ConfigDocument("1", Values), Problems));

    public Task SaveAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        SaveCalls++;
        Values = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        return Task.CompletedTask;
    }
}

/// <summary>Fake IStateDatabase with configurable schema state and FTS5 answer.</summary>
public sealed class FakeStateDatabase(int currentVersion = -1, int maxVersion = 1, bool fts5 = true) : IStateDatabase
{
    public int MaxSupportedSchemaVersion { get; } = maxVersion;
    public int CurrentVersion { get; set; } = currentVersion;
    public bool Fts5 { get; set; } = fts5;
    public int MigrationsApplied { get; private set; }

    public Task<int> GetCurrentSchemaVersionAsync(CancellationToken ct) => Task.FromResult(CurrentVersion);

    public Task ApplyMigrationsAsync(CancellationToken ct)
    {
        MigrationsApplied++;
        return Task.CompletedTask;
    }

    public Task<bool> SupportsFts5Async(CancellationToken ct) => Task.FromResult(Fts5);

    public Task DeleteDatabaseAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Fake IMutationLock: records acquisitions, can simulate a busy holder and
/// reports what the caller passed (command + timeout).
/// </summary>
public sealed class FakeMutationLock : IMutationLock
{
    public LockHolderInfo? HolderAnswer { get; set; }
    public TimeSpan? LastTimeout { get; private set; }
    public string? LastCommand { get; private set; }
    public int AcquireCalls { get; private set; }

    public Task<ILockHandle> AcquireAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        AcquireCalls++;
        LastCommand = command;
        LastTimeout = timeout;
        return Task.FromResult<ILockHandle>(new FakeLockHandle(new LockHolderInfo(Environment.ProcessId, command, null, null)));
    }

    public Task<LockHolderInfo?> ReadHolderAsync(CancellationToken ct) => Task.FromResult(HolderAnswer);

    private sealed class FakeLockHandle(LockHolderInfo holder) : ILockHandle
    {
        public LockHolderInfo Holder { get; } = holder;
        public void Dispose() { }
    }
}
