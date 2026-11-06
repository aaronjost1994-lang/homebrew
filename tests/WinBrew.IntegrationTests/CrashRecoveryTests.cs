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

        /// <summary>Phase C: app 1.0.0 + lib 1.5.0 → app 2.0.0 + lib 2.0.0 in ONE operation (new template too).</summary>
        MultiPackageUpgrade,
    }

    /// <summary>Every registered checkpoint → the scenarios that must fire it. Asserted to cover the registry exactly.</summary>
    public static readonly IReadOnlyDictionary<string, Scenario[]> Coverage = new Dictionary<string, Scenario[]>(StringComparer.Ordinal)
    {
        [FaultCheckpoints.DownloadStarted] = [Scenario.Install, Scenario.Upgrade, Scenario.MultiPackageUpgrade],
        [FaultCheckpoints.DownloadCompleted] = [Scenario.Install, Scenario.Upgrade, Scenario.MultiPackageUpgrade],
        [FaultCheckpoints.ArtifactVerified] = [Scenario.Install, Scenario.Upgrade, Scenario.MultiPackageUpgrade],
        [FaultCheckpoints.ArtifactCached] = [Scenario.Install, Scenario.Upgrade, Scenario.MultiPackageUpgrade],
        [FaultCheckpoints.ExtractCompleted] = [Scenario.Install, Scenario.Upgrade, Scenario.MultiPackageUpgrade],
        [FaultCheckpoints.ValidateCompleted] = [Scenario.Install, Scenario.Upgrade, Scenario.MultiPackageUpgrade],
        [FaultCheckpoints.PackagePlaced] = [Scenario.Install, Scenario.Upgrade, Scenario.MultiPackageUpgrade],
        [FaultCheckpoints.ShimSidecarReplaced] = [Scenario.Install, Scenario.Upgrade, Scenario.SelfInstall, Scenario.MultiPackageUpgrade],
        [FaultCheckpoints.ShimBinaryReplaced] = [Scenario.Install, Scenario.Upgrade, Scenario.SelfInstall, Scenario.MultiPackageUpgrade],
        [FaultCheckpoints.PackageActivated] = [Scenario.Install, Scenario.Upgrade, Scenario.MultiPackageUpgrade],
        [FaultCheckpoints.StateCommitted] = [Scenario.Install, Scenario.Upgrade, Scenario.Uninstall, Scenario.SelfInstall, Scenario.MultiPackageUpgrade],
        [FaultCheckpoints.OperationCompleted] = [Scenario.Install, Scenario.Upgrade, Scenario.Uninstall, Scenario.SelfInstall, Scenario.MultiPackageUpgrade],
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

            case Scenario.MultiPackageUpgrade:
                AssertMultiUpgradeRecovers(checkpoint);
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

    /// <summary>
    /// Phase C acceptance: a failed upgrade at every checkpoint leaves the
    /// PREVIOUS versions usable — the whole plan is all-or-nothing: either both
    /// packages are on their old versions with their old shims, or both moved.
    /// </summary>
    private static void AssertMultiUpgradeRecovers(string checkpoint)
    {
        using var h = new CliHarness(DependencyCliTests.AppAndLib).Ready();
        CliHarness.Expect(0, h.Run("install", "app@1.0.0", "--yes"));
        h.SetTemplate("TEMPLATE-2");
        AssertCrashed(h.RunFault(checkpoint, "upgrade", "--yes"), checkpoint);
        CliHarness.Expect(0, h.Run("recover"));

        Assert.Empty(h.ListWarnings());
        var app = h.Installed("app")!;
        var lib = h.Installed("lib")!;
        Assert.Contains((app.Version, lib.Version), new[] { ("1.0.0", "1.5.0"), ("2.0.0", "2.0.0") });
        foreach (var (name, version) in new[] { ("app", app.Version), ("libtool", lib.Version) })
        {
            var sidecar = WinBrew.Core.Shims.ShimSidecar.TryParse(File.ReadAllBytes(Path.Combine(h.Bin, name + ".shim.json")))!;
            Assert.Equal(version, sidecar.Version);
            Assert.True(File.Exists(sidecar.Target), $"{name} → {sidecar.Target} must be usable");
            Assert.True(File.Exists(Path.Combine(h.Bin, name + ".exe")));
        }

        Assert.Empty(h.OldFiles());
        Assert.Empty(h.StagingLeftovers());
    }

    private static void AssertCrashed(CliRun run, string checkpoint) =>
        Assert.True(run.FaultInjected, $"checkpoint '{checkpoint}' never fired:\n{run}");
}
