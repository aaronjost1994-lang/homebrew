using System.Runtime.InteropServices;
using WinBrew.Application.Ports;
using WinBrew.Core.Config;
using WinBrew.Core;

namespace WinBrew.Application.Doctor;

/// <summary>
/// wbrew doctor (Phase A scope, phase-a.md): environment/layout checks, lock
/// inspection with PID+start-time matching (D5), the build-vs-OS architecture
/// warning (A10), test-only settings in release builds (D10/A13), the config
/// file, the state database, and the FTS5 probe result.
/// Doctor is read-only: it never creates directories, never migrates the
/// database, never takes the mutation lock.
/// </summary>
public sealed class DoctorUseCase(
    IVersionInfo versionInfo,
    IStorageLayout layout,
    IConfigStore configStore,
    IStateDatabase stateDatabase,
    IMutationLock mutationLock,
    IEnvironmentProbe environmentProbe,
    bool testHooksBuild)
{
    public async Task<DoctorReport> ExecuteAsync(CancellationToken ct)
    {
        var checks = new List<DoctorCheck>();

        await CheckLayoutAsync(checks, ct);
        CheckArchitecture(checks);
        await CheckConfigAsync(checks, ct);
        await CheckDatabaseAsync(checks, ct);
        await CheckLockAsync(checks, ct);
        await CheckFts5Async(checks, ct);
        CheckTestOnlySettings(checks);

        return new DoctorReport(versionInfo.Version, layout.Root, checks);
    }

    private async Task CheckLayoutAsync(List<DoctorCheck> checks, CancellationToken ct)
    {
        var status = await layout.InspectAsync(ct);

        if (status.VolumeConflicts.Count > 0)
        {
            checks.Add(new("layout.volumes", DoctorStatus.Fail,
                "staging\\, packages\\, cache\\ and bin\\ must share the volume with state\\ " +
                "(renames must be atomic, A6). Conflicts: " +
                string.Join(", ", status.VolumeConflicts) + ". Fix WINBREW_ROOT or move the layout."));
            return;
        }

        if (!status.RootExists)
        {
            checks.Add(new("layout.root", DoctorStatus.Warn,
                $"Layout root '{layout.Root}' does not exist yet (created by the first mutating command)."));
            return;
        }

        checks.Add(status.AllDirectoriesPresent && status.Writable
            ? new("layout.root", DoctorStatus.Ok, $"Root '{layout.Root}' present and writable.")
            : new("layout.root", DoctorStatus.Fail, status.Problem ?? "Layout is incomplete or not writable."));
    }

    private void CheckArchitecture(List<DoctorCheck> checks)
    {
        var process = environmentProbe.ProcessArchitecture;

        if (environmentProbe.NativeMachine is not Architecture native)
        {
            checks.Add(new("host.architecture", DoctorStatus.Warn,
                "Could not determine the native machine architecture (IsWow64Process2 unavailable)."));
            CheckLongPaths(checks);
            return;
        }

        if (native != process)
        {
            checks.Add(new("host.architecture", DoctorStatus.Warn,
                $"Build-vs-OS mismatch: this is a {process} build running on native {native} hardware " +
                "(likely emulation). Artifact selection prefers native artifacts (A10); install the " +
                $"'{native.ToString().ToLowerInvariant()}' build of wbrew for correct selection."));
        }
        else
        {
            checks.Add(new("host.architecture", DoctorStatus.Ok, $"Native machine: {native}; build: {process}."));
        }

        CheckLongPaths(checks);
    }

    private void CheckLongPaths(List<DoctorCheck> checks)
    {
        var longPaths = environmentProbe.LongPathsEnabled;
        if (longPaths is null)
        {
            checks.Add(new("system.longpaths", DoctorStatus.Warn, "LongPathsEnabled could not be read (A12)."));
        }
        else if (longPaths == false)
        {
            checks.Add(new("system.longpaths", DoctorStatus.Warn,
                "LongPathsEnabled is off; installs with paths deeper than 260 chars will warn (A12)."));
        }
        else
        {
            checks.Add(new("system.longpaths", DoctorStatus.Ok, "LongPathsEnabled is on."));
        }
    }

    private async Task CheckConfigAsync(List<DoctorCheck> checks, CancellationToken ct)
    {
        var inspection = await configStore.InspectAsync(ct);

        if (inspection.Document is null && inspection.Problems.Count == 0)
        {
            checks.Add(new("config.file", DoctorStatus.Ok,
                "config.json not found; all defaults apply (D10)."));
            return;
        }

        if (inspection.Problems.Count > 0)
        {
            foreach (var problem in inspection.Problems)
            {
                var isTestKey = ConfigDefinitions.Keys.TryGetValue(problem.Key, out var def) && def.TestHooksOnly;
                checks.Add(isTestKey
                    ? new("config.testing", DoctorStatus.Warn,
                        $"'{problem.Key}' found in config.json — testing.* keys are honored only in " +
                        "test-hooks builds; release builds ignore and reject them (D10/A13).")
                    : new("config.file", DoctorStatus.Fail, problem.Message));
            }
        }
        else
        {
            checks.Add(new("config.file", DoctorStatus.Ok, "config.json is present and valid."));
        }
    }

    private async Task CheckDatabaseAsync(List<DoctorCheck> checks, CancellationToken ct)
    {
        var current = await stateDatabase.GetCurrentSchemaVersionAsync(ct);
        var max = stateDatabase.MaxSupportedSchemaVersion;

        if (current < 0)
        {
            var dbPath = Path.Combine(layout.Directories["state"], "winbrew.db");
            checks.Add(new("state.database", DoctorStatus.Warn,
                $"No state database yet at {dbPath}; " +
                "it is created by the first mutating command (A6)."));
            return;
        }

        if (current > max)
        {
            checks.Add(new("state.database", DoctorStatus.Fail,
                $"Database schema v{current} is NEWER than this binary supports (v{max}). " +
                "Downgrades are never performed (A6); use a newer wbrew build. " +
                "Mutating commands will refuse with exit 1."));
            return;
        }

        checks.Add(current == max
            ? new("state.database", DoctorStatus.Ok, $"Database schema v{current} (this binary supports v{max}).")
            : new("state.database", DoctorStatus.Warn,
                $"Database schema v{current} will migrate to v{max} at the next mutating command " +
                "(a backup is taken first, A6)."));
    }

    private async Task CheckLockAsync(List<DoctorCheck> checks, CancellationToken ct)
    {
        var holder = await mutationLock.ReadHolderAsync(ct);

        if (holder is null || holder.Pid is null)
        {
            checks.Add(new("mutation.lock", DoctorStatus.Ok,
                "No mutation lock holder (or holder metadata unreadable — treated as unknown, D5)."));
            return;
        }

        var alive = environmentProbe.IsProcessAlive(holder.Pid.Value, holder.ProcessStartTime);
        checks.Add(alive
            ? new("mutation.lock", DoctorStatus.Warn,
                $"A mutation is in progress: pid {holder.Pid} ({holder.Command ?? "unknown command"}).")
            : new("mutation.lock", DoctorStatus.Ok,
                $"Stale lock metadata for pid {holder.Pid} (process not running or start time differs, D5 — " +
                "the lock itself is OS-managed and free; no stale-lock breaking is performed)."));
    }

    private async Task CheckFts5Async(List<DoctorCheck> checks, CancellationToken ct)
    {
        var fts5 = await stateDatabase.SupportsFts5Async(ct);
        checks.Add(fts5
            ? new("sqlite.fts5", DoctorStatus.Ok,
                "FTS5 is available in the bundled SQLite build; search MAY use an FTS5 index (D6).")
            : new("sqlite.fts5", DoctorStatus.Ok,
                "FTS5 is NOT available in the bundled SQLite build; search matches search_text directly (D6)."));
    }

    private void CheckTestOnlySettings(List<DoctorCheck> checks)
    {
        if (testHooksBuild)
        {
            checks.Add(new("build.testhooks", DoctorStatus.Warn,
                "This is a TEST-HOOKS build (-p:WinBrewTestHooks=true): fault injection, the localhost " +
                "HTTP exception and testing.* config keys are active (A11/A13). Never ship this build."));
        }
        else
        {
            var testMode = Environment.GetEnvironmentVariable("WINBREW_TEST_MODE");
            if (testMode == "1")
            {
                checks.Add(new("build.testmode", DoctorStatus.Warn,
                    "WINBREW_TEST_MODE=1 is set but this is a release build — the variable has no effect (A13)."));
            }
            else
            {
                checks.Add(new("build.testhooks", DoctorStatus.Ok, "Release build: no test-only behavior compiled in."));
            }
        }
    }
}
