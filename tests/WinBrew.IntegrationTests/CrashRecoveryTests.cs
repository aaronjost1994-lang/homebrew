using WinBrew.Core.Operations;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>
/// A16/A11: a crash at EVERY registered fault checkpoint, followed by
/// <c>wbrew recover</c>, ends in a consistent state (database ↔ bin\ ↔
/// packages\ ↔ staging\), or reports RecoveryFailed honestly. The real CLI
/// runs as a child process and the test-hooks seam FailFasts it.
/// </summary>
public sealed class CrashRecoveryTests
{
    public enum Scenario
    {
        /// <summary>Fresh install hello@1.0.0.</summary>
        Install,

        /// <summary>hello 1.0.0 → 2.0.0 with a NEW shim template (exercises the .old protocol).</summary>
        Upgrade,

        /// <summary>Uninstall hello 2.0.0 (two shims).</summary>
        Uninstall,

        /// <summary>Install crashes at package.activated, then recover itself crashes at the checkpoint.</summary>
        CrashDuringRecovery,

        /// <summary>The checkpoint fires during test-ingest.</summary>
        Ingest,

        /// <summary>wbrew self install (zip layout).</summary>
        SelfInstall,

        /// <summary>self install --modify-path against a disposable HKCU key (Windows only).</summary>
        SelfInstallModifyPath,

        /// <summary>Recovery is sabotaged so it must end in RecoveryFailed.</summary>
        SabotagedRecovery,
    }

    /// <summary>Every registered checkpoint → the scenarios that must fire it. Asserted to cover the registry exactly.</summary>
    public static readonly IReadOnlyDictionary<string, Scenario[]> Coverage = new Dictionary<string, Scenario[]>(StringComparer.Ordinal)
    {
        [FaultCheckpoints.DownloadStarted] = [Scenario.Install, Scenario.Upgrade],
        [FaultCheckpoints.DownloadCompleted] = [Scenario.Install, Scenario.Upgrade],
        [FaultCheckpoints.ArtifactVerified] = [Scenario.Install, Scenario.Upgrade],
        [FaultCheckpoints.ArtifactCached] = [Scenario.Install, Scenario.Upgrade],
        [FaultCheckpoints.ExtractCompleted] = [Scenario.Install, Scenario.Upgrade],
        [FaultCheckpoints.ValidateCompleted] = [Scenario.Install, Scenario.Upgrade],
        [FaultCheckpoints.PackagePlaced] = [Scenario.Install, Scenario.Upgrade],
        [FaultCheckpoints.ShimSidecarReplaced] = [Scenario.Install, Scenario.Upgrade, Scenario.SelfInstall],
        [FaultCheckpoints.ShimBinaryReplaced] = [Scenario.Install, Scenario.Upgrade, Scenario.SelfInstall],
        [FaultCheckpoints.PackageActivated] = [Scenario.Install, Scenario.Upgrade],
        [FaultCheckpoints.StateCommitted] = [Scenario.Install, Scenario.Upgrade, Scenario.Uninstall, Scenario.SelfInstall],
        [FaultCheckpoints.OperationCompleted] = [Scenario.Install, Scenario.Upgrade, Scenario.Uninstall, Scenario.SelfInstall],
        [FaultCheckpoints.CompensateStarted] = [Scenario.CrashDuringRecovery],
        [FaultCheckpoints.CompensateShimReverted] = [Scenario.CrashDuringRecovery],
        [FaultCheckpoints.CompensatePackageRemoved] = [Scenario.CrashDuringRecovery],
        [FaultCheckpoints.RollbackCompleted] = [Scenario.CrashDuringRecovery],
        [FaultCheckpoints.RecoveryFailed] = [Scenario.SabotagedRecovery],
        [FaultCheckpoints.UninstallShimRemoved] = [Scenario.Uninstall],
        [FaultCheckpoints.UninstallPackageRemoved] = [Scenario.Uninstall],
        [FaultCheckpoints.PathUpdated] = [Scenario.SelfInstallModifyPath],
        [FaultCheckpoints.CatalogIngested] = [Scenario.Ingest],
        [FaultCheckpoints.SelfInstallAppCopied] = [Scenario.SelfInstall],
    };

    public static TheoryData<string, Scenario> Cases()
    {
        var data = new TheoryData<string, Scenario>();
        foreach (var (checkpoint, scenarios) in Coverage.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            foreach (var scenario in scenarios.Where(s => s != Scenario.SelfInstallModifyPath))
            {
                data.Add(checkpoint, scenario);
            }
        }

        return data;
    }

    [Fact]
    public void CoverageTable_CoversEveryRegisteredCheckpoint_Exactly()
    {
        Assert.Equal(
            FaultCheckpoints.All.OrderBy(n => n, StringComparer.Ordinal),
            Coverage.Keys.OrderBy(n => n, StringComparer.Ordinal));
    }

    [TestHooksTheory]
    [MemberData(nameof(Cases))]
    public void CrashAtCheckpoint_ThenRecover_IsConsistent(string checkpoint, Scenario scenario)
    {
        using var h = new CliHarness().Ready();
        switch (scenario)
        {
            case Scenario.Install:
                AssertCrashed(h.RunFault(checkpoint, "install", "hello@1.0.0", "--yes"), checkpoint);
                CliHarness.Expect(0, h.Run("recover"));
                h.AssertConsistent(null, "1.0.0");
                break;

            case Scenario.Upgrade:
                CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
                h.SetTemplate("TEMPLATE-2"); // the binary must change → .old.<guid> path
                AssertCrashed(h.RunFault(checkpoint, "install", "hello@2.0.0", "--yes"), checkpoint);
                CliHarness.Expect(0, h.Run("recover"));
                h.AssertConsistent("1.0.0", "2.0.0");
                break;

            case Scenario.Uninstall:
                CliHarness.Expect(0, h.Run("install", "hello@2.0.0", "--yes"));
                AssertCrashed(h.RunFault(checkpoint, "uninstall", "hello", "--yes"), checkpoint);
                CliHarness.Expect(0, h.Run("recover"));
                h.AssertConsistent([null]);
                break;

            case Scenario.CrashDuringRecovery:
                AssertCrashed(h.RunFault(FaultCheckpoints.PackageActivated, "install", "hello@1.0.0", "--yes"), FaultCheckpoints.PackageActivated);
                AssertCrashed(h.RunFault(checkpoint, "recover"), checkpoint);
                CliHarness.Expect(0, h.Run("recover"));
                h.AssertConsistent([null]);
                break;

            case Scenario.Ingest:
                AssertCrashed(h.RunFault(checkpoint, "test-ingest", CliHarness.RepoName, h.Server.BaseUrl.ToString()), checkpoint);
                CliHarness.Expect(0, h.Run("recover"));
                // The ingest transaction committed before the checkpoint: the catalog is usable.
                CliHarness.Expect(0, h.Run("info", "hello"));
                h.AssertConsistent([null]);
                break;

            case Scenario.SelfInstall:
                ReleaseFiles.EnsureNextToHost();
                AssertCrashed(h.RunFault(checkpoint, "self", "install", "--non-interactive"), checkpoint);
                CliHarness.Expect(0, h.Run("recover"));
                SelfInstallTests.AssertSelfInstallConsistent(h);
                break;

            case Scenario.SabotagedRecovery:
                CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
                AssertCrashed(h.RunFault(FaultCheckpoints.ShimSidecarReplaced, "install", "hello@2.0.0", "--yes"), FaultCheckpoints.ShimSidecarReplaced);
                RecoveryFailedTests.SabotageSidecar(h);
                AssertCrashed(h.RunFault(checkpoint, "recover"), checkpoint);
                // RecoveryFailed is terminal and reported, never silently "fixed" (A11).
                var doctor = h.Run("doctor");
                Assert.Equal(1, doctor.ExitCode);
                Assert.Contains("RecoveryFailed", doctor.StdOut, StringComparison.Ordinal);
                break;

            default:
                throw new InvalidOperationException(scenario.ToString());
        }
    }

    [WindowsFact("path.updated needs HKCU (a disposable key via WINBREW_TEST_PATH_KEY)")]
    public void CrashAtPathUpdated_ThenRecover_RollsForward()
    {
        if (!TestBuild.TestHooks)
        {
            Assert.Fail("Needs the test-hooks build (-p:WinBrewTestHooks=true).");
        }

        using var h = new CliHarness().Ready();
        using var hive = new PathTestKey();
        ReleaseFiles.EnsureNextToHost();
        var env = new Dictionary<string, string?> { ["WINBREW_FAULT_AT"] = FaultCheckpoints.PathUpdated, ["WINBREW_TEST_PATH_KEY"] = hive.SubKey };
        AssertCrashed(h.RunWith(env, "self", "install", "--modify-path"), FaultCheckpoints.PathUpdated);
        CliHarness.Expect(0, h.RunWith(new Dictionary<string, string?> { ["WINBREW_TEST_PATH_KEY"] = hive.SubKey }, "recover"));
        Assert.Contains(h.Bin, hive.ReadPath() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        SelfInstallTests.AssertSelfInstallConsistent(h);
    }

    private static void AssertCrashed(CliRun run, string checkpoint) =>
        Assert.True(run.FaultInjected, $"checkpoint '{checkpoint}' never fired:\n{run}");
}
