using WinBrew.Application.Configuration;
using WinBrew.Application.Doctor;
using WinBrew.Application.Ports;
using WinBrew.TestFixtures;

namespace WinBrew.UnitTests;

/// <summary>Doctor checks (Phase A scope) driven through fake ports.</summary>
public class DoctorUseCaseTests
{
    private static DoctorUseCase Create(
        FakeEnvironmentProbe? probe = null,
        FakeStorageLayout? layout = null,
        FakeConfigStore? config = null,
        FakeStateDatabase? db = null,
        FakeMutationLock? mutationLock = null,
        bool testHooks = false)
    {
        return new DoctorUseCase(
            new FakeVersionInfo(),
            layout ?? new FakeStorageLayout("/fake/root"),
            config ?? new FakeConfigStore(),
            db ?? new FakeStateDatabase(),
            mutationLock ?? new FakeMutationLock(),
            probe ?? new FakeEnvironmentProbe(),
            testHooks);
    }

    [Fact]
    public async Task HealthySystem_AllOk_ExitsHealthy()
    {
        var report = await Create().ExecuteAsync(CancellationToken.None);

        Assert.True(report.Healthy);
        Assert.Contains(report.Checks, c => c.Name == "layout.root" && c.Status == DoctorStatus.Ok);
        Assert.Contains(report.Checks, c => c.Name == "host.architecture" && c.Status == DoctorStatus.Ok);
        Assert.Contains(report.Checks, c => c.Name == "sqlite.fts5");
        Assert.Contains(report.Checks, c => c.Name == "build.testhooks" && c.Status == DoctorStatus.Ok);
    }

    [Fact]
    public async Task BuildOsMismatch_WarnsAndNamesNativeArchitecture() // A10 build-vs-OS warning
    {
        var probe = new FakeEnvironmentProbe { NativeMachine = System.Runtime.InteropServices.Architecture.Arm64 };
        var report = await Create(probe: probe).ExecuteAsync(CancellationToken.None);

        var check = report.Checks.Single(c => c.Name == "host.architecture");
        Assert.Equal(DoctorStatus.Warn, check.Status);
        Assert.Contains("Arm64", check.Detail);
    }

    [Fact]
    public async Task UnknownNativeMachine_WarnsNotFails()
    {
        var probe = new FakeEnvironmentProbe { NativeMachine = null };
        var report = await Create(probe: probe).ExecuteAsync(CancellationToken.None);
        Assert.Equal(DoctorStatus.Warn, report.Checks.Single(c => c.Name == "host.architecture").Status);
        Assert.True(report.Healthy);
    }

    [Fact]
    public async Task VolumeConflict_IsAFail_MakingUnhealthy() // A6 one-volume rule
    {
        var layout = new FakeStorageLayout("/fake/root", new WinBrew.Application.Ports.StorageLayoutStatus(
            RootExists: true, AllDirectoriesPresent: true, Writable: true,
            VolumeConflicts: ["staging"], Problem: null));
        var report = await Create(layout: layout).ExecuteAsync(CancellationToken.None);

        Assert.False(report.Healthy);
        Assert.Equal(DoctorStatus.Fail, report.Checks.Single(c => c.Name == "layout.volumes").Status);
        Assert.Contains("staging", report.Checks.Single(c => c.Name == "layout.volumes").Detail);
    }

    [Fact]
    public async Task NewerDatabaseThanBinary_IsAFail() // A6: newer schema never downgraded
    {
        var db = new FakeStateDatabase(currentVersion: 9, maxVersion: 1);
        var report = await Create(db: db).ExecuteAsync(CancellationToken.None);

        Assert.False(report.Healthy);
        var check = report.Checks.Single(c => c.Name == "state.database");
        Assert.Equal(DoctorStatus.Fail, check.Status);
        Assert.Contains("NEWER", check.Detail);
    }

    [Fact]
    public async Task PendingMigration_IsWarningWithBackupAdvice() // A6 backup-before-migrate messaging
    {
        var db = new FakeStateDatabase(currentVersion: 0, maxVersion: 1);
        var report = await Create(db: db).ExecuteAsync(CancellationToken.None);

        var check = report.Checks.Single(c => c.Name == "state.database");
        Assert.Equal(DoctorStatus.Warn, check.Status);
        Assert.Contains("backup", check.Detail);
    }

    [Fact]
    public async Task LiveHolder_WarnsWithPidAndCommand() // D5 doctor holder check
    {
        var mutationLock = new FakeMutationLock
        {
            HolderAnswer = new LockHolderInfo(4242, "install", "2026-11-05T10:00:00.0000000Z", "2026-11-05T09:59:00.0000000Z"),
        };
        var report = await Create(mutationLock: mutationLock).ExecuteAsync(CancellationToken.None);

        var check = report.Checks.Single(c => c.Name == "mutation.lock");
        Assert.Equal(DoctorStatus.Warn, check.Status);
        Assert.Contains("4242", check.Detail);
        Assert.Contains("install", check.Detail);
    }

    [Fact]
    public async Task DeadHolder_IsOk_NoStaleLockBreaking() // D5: PID+start-time mismatch → not running
    {
        var probe = new FakeEnvironmentProbe { IsAliveOverride = (_, _) => false };
        var mutationLock = new FakeMutationLock
        {
            HolderAnswer = new LockHolderInfo(4242, "install", null, "2020-01-01T00:00:00.0000000Z"),
        };
        var report = await Create(probe: probe, mutationLock: mutationLock).ExecuteAsync(CancellationToken.None);

        var check = report.Checks.Single(c => c.Name == "mutation.lock");
        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.Contains("no stale-lock breaking", check.Detail);
    }

    [Fact]
    public async Task UnknownHolder_ToleratedAsOk() // D5 torn metadata = holder unknown
    {
        var mutationLock = new FakeMutationLock { HolderAnswer = null };
        var report = await Create(mutationLock: mutationLock).ExecuteAsync(CancellationToken.None);
        Assert.Equal(DoctorStatus.Ok, report.Checks.Single(c => c.Name == "mutation.lock").Status);
    }

    [Fact]
    public async Task TestingKeyInConfig_ReportedAsWarning() // D10: doctor reports testing.* keys
    {
        var config = new FakeConfigStore
        {
            Problems = [new ConfigProblem("testing.allowInsecureLocalhost", "honored only in test-hooks builds")],
        };
        var report = await Create(config: config).ExecuteAsync(CancellationToken.None);

        var check = report.Checks.Single(c => c.Name == "config.testing");
        Assert.Equal(DoctorStatus.Warn, check.Status);
        Assert.Contains("test-hooks", check.Detail);
    }

    [Fact]
    public async Task InvalidConfig_IsAFail()
    {
        var config = new FakeConfigStore
        {
            Problems = [new ConfigProblem("download.retries", "must be 0..5")],
        };
        var report = await Create(config: config).ExecuteAsync(CancellationToken.None);

        Assert.False(report.Healthy);
        Assert.Equal(DoctorStatus.Fail, report.Checks.Single(c => c.Name == "config.file").Status);
    }

    [Fact]
    public async Task TestHooksBuild_SelfReports()
    {
        var report = await Create(testHooks: true).ExecuteAsync(CancellationToken.None);
        var check = report.Checks.Single(c => c.Name == "build.testhooks");
        Assert.Equal(DoctorStatus.Warn, check.Status);
        Assert.Contains("Never ship", check.Detail);
    }

    [Fact]
    public async Task ReleaseBuildWithTestModeEnv_Warns() // A13: env var has no effect in release
    {
        Environment.SetEnvironmentVariable("WINBREW_TEST_MODE", "1");
        try
        {
            var report = await Create(testHooks: false).ExecuteAsync(CancellationToken.None);
            Assert.Contains(report.Checks, c => c.Name == "build.testmode" && c.Status == DoctorStatus.Warn);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINBREW_TEST_MODE", null);
        }
    }

    [Fact]
    public async Task MissingDatabase_ReportedAsWarn_NotFail()
    {
        var db = new FakeStateDatabase(currentVersion: -1);
        var report = await Create(db: db).ExecuteAsync(CancellationToken.None);
        Assert.True(report.Healthy);
        Assert.Equal(DoctorStatus.Warn, report.Checks.Single(c => c.Name == "state.database").Status);
    }
}
