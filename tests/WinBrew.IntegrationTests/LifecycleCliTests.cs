using System.Security.Cryptography;
using System.Text.Json.Nodes;
using WinBrew.Core.Shims;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>
/// The Phase B portable lifecycle end to end through the real CLI (test-hooks
/// build, isolated WINBREW_ROOT, local fixture repository): install, list,
/// info, upgrade, uninstall, consent, dry-run, catalog-only planning,
/// collisions, PE validation, and the download/metadata rejection paths.
/// </summary>
public sealed class LifecycleCliTests
{
    [TestHooksFact]
    public void Install_List_Info_Uninstall_RoundTrip()
    {
        using var h = new CliHarness().Ready();

        var install = CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes", "--json")).Json();
        Assert.True(install.GetProperty("ok").GetBoolean());
        Assert.Equal("install", CliHarness.PlanItem(install, "hello").GetProperty("action").GetString());

        var sidecar = ShimSidecar.TryParse(File.ReadAllBytes(Path.Combine(h.Bin, "hello.shim.json")))!;
        Assert.Equal(Path.Combine(h.Packages, "hello", "1.0.0", "x64", "bin", "hello.exe"), sidecar.Target);
        Assert.Equal("local/hello", sidecar.Package);
        Assert.Equal(ShimSidecar.Console, sidecar.Subsystem);
        Assert.Equal("TEMPLATE-1", File.ReadAllText(Path.Combine(h.Bin, "hello.exe"))); // a copy of the template (D4)
        h.AssertConsistent("1.0.0");

        var info = CliHarness.Expect(0, h.Run("info", "hello", "--json")).Json().GetProperty("data");
        Assert.Equal("2.0.0", info.GetProperty("latest").GetString()); // computed locally, no latestVersion field (D6)
        Assert.Equal("1.0.0", info.GetProperty("installed").GetProperty("version").GetString());

        CliHarness.Expect(0, h.Run("uninstall", "hello", "--yes"));
        h.AssertConsistent([null]);
        Assert.Equal("Nothing to recover.", CliHarness.Expect(0, h.Run("recover")).StdOut.Trim());
    }

    [TestHooksFact]
    public void Install_NonInteractiveWithoutYes_Exits10_AndChangesNothing()
    {
        using var h = new CliHarness().Ready();
        var run = CliHarness.Expect(10, h.Run("install", "hello", "--non-interactive"));
        Assert.Contains("ConsentRequired", run.StdErr, StringComparison.Ordinal);
        h.AssertConsistent([null]);
        Assert.False(Directory.Exists(Path.Combine(h.Root, "cache", "artifacts")) &&
                     Directory.EnumerateFiles(Path.Combine(h.Root, "cache", "artifacts")).Any());
    }

    [TestHooksFact]
    public void DryRun_PrintsThePlan_AndTouchesNothing_NotEvenTheNetwork()
    {
        using var h = new CliHarness().Ready();
        var before = h.Server.Requests.Count;

        var plan = CliHarness.Expect(0, h.Run("install", "hello", "--dry-run", "--json")).Json();
        Assert.True(plan.GetProperty("data").GetProperty("dryRun").GetBoolean());
        Assert.Equal("2.0.0", CliHarness.PlanItem(plan, "hello").GetProperty("version").GetString());

        Assert.Equal(before, h.Server.Requests.Count); // no manifest, no artifact (A8)
        Assert.False(Directory.Exists(Path.Combine(h.Packages, "hello")));
        Assert.Empty(h.PackageShimFiles(".exe"));
    }

    [TestHooksFact]
    public void Install_ReadsTheCatalogOnly_NeverAManifestThatIsNotIndexed()
    {
        // A manifest for 3.0.0 exists on the server but is NOT in index.json.
        using var h = new CliHarness(r => r.AddHello().Add(new FixturePackage("hello", "3.0.0",
            [("bin/hello.exe", PeBytes.Create())], [("hello", "bin/hello.exe")])));
        var index = JsonNode.Parse(File.ReadAllText(h.Repository.IndexPath))!;
        ((JsonObject)index["packages"]!["hello"]!["versions"]!).Remove("3.0.0");
        File.WriteAllText(h.Repository.IndexPath, index.ToJsonString());
        h.Ready();

        var run = CliHarness.Expect(3, h.Run("install", "hello@3.0.0", "--yes"));
        Assert.Contains("not in the catalog", run.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("/manifests/hello/3.0.0.json", h.Server.RequestedPaths);

        // The unqualified "latest" also comes from the catalog: 2.0.0, not 3.0.0.
        var plan = CliHarness.Expect(0, h.Run("install", "hello", "--dry-run", "--json")).Json();
        Assert.Equal("2.0.0", CliHarness.PlanItem(plan, "hello").GetProperty("version").GetString());
    }

    [TestHooksFact]
    public void Install_ManifestThatDiffersFromItsIndexEntry_IsRejected_Exit5()
    {
        using var h = new CliHarness();
        // Index claims two architectures; the (digest-matching) manifest offers only x64.
        var index = JsonNode.Parse(File.ReadAllText(h.Repository.IndexPath))!;
        index["packages"]!["hello"]!["versions"]!["1.0.0"]!["architectures"] = new JsonArray("x64", "arm64");
        File.WriteAllText(h.Repository.IndexPath, index.ToJsonString());
        h.Ready();

        var run = CliHarness.Expect(5, h.Run("install", "hello@1.0.0", "--yes"));
        Assert.Contains("does not match its index.json entry", run.StdErr, StringComparison.Ordinal);
        h.AssertConsistent([null]);
    }

    [TestHooksFact]
    public void Upgrade_WithAnUnchangedTemplate_UpdatesOnlyTheSidecar()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        var binary = Path.Combine(h.Bin, "hello.exe");
        var before = File.GetLastWriteTimeUtc(binary);
        Thread.Sleep(50);

        var plan = CliHarness.PlanItem(CliHarness.Expect(0, h.Run("install", "hello@2.0.0", "--yes", "--json")).Json(), "hello");
        Assert.Equal("upgrade", plan.GetProperty("action").GetString());
        Assert.Equal("1.0.0", plan.GetProperty("previousVersion").GetString());

        Assert.Equal(before, File.GetLastWriteTimeUtc(binary)); // D4: a running shim never blocks an upgrade
        Assert.Equal("2.0.0", ShimSidecar.TryParse(File.ReadAllBytes(Path.Combine(h.Bin, "hello.shim.json")))!.Version);
        Assert.Equal(ShimSidecar.Gui, ShimSidecar.TryParse(File.ReadAllBytes(Path.Combine(h.Bin, "hi.shim.json")))!.Subsystem);
        h.AssertConsistent("2.0.0");
        Assert.True(Directory.Exists(Path.Combine(h.Packages, "hello", "1.0.0", "x64")), "retainVersions=1 keeps the previous version");
    }

    [TestHooksFact]
    public void Upgrade_WithANewTemplate_ReplacesTheBinary_ThroughAJournaledOldFile()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        h.SetTemplate("TEMPLATE-2");
        CliHarness.Expect(0, h.Run("install", "hello@2.0.0", "--yes"));

        Assert.Equal("TEMPLATE-2", File.ReadAllText(Path.Combine(h.Bin, "hello.exe")));
        Assert.Empty(h.OldFiles()); // the journaled .old.<guid> was cleaned up (not locked here)
        h.AssertConsistent("2.0.0");
    }

    [TestHooksFact]
    public void RetainVersionsZero_PrunesThePreviousVersion()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(0, h.Run("config", "set", "retainVersions", "0"));
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        CliHarness.Expect(0, h.Run("install", "hello@2.0.0", "--yes"));
        Assert.False(Directory.Exists(Path.Combine(h.Packages, "hello", "1.0.0")));
        h.AssertConsistent("2.0.0");
    }

    [TestHooksFact]
    public void Downgrade_ToARetainedVersion_Works_AndRemovesStaleShims()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(0, h.Run("install", "hello@2.0.0", "--yes"));
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        Assert.Equal(["hello"], h.PackageShimFiles(".exe")); // 'hi' only exists in 2.0.0
        h.AssertConsistent("1.0.0");
    }

    [TestHooksFact]
    public void UnmanagedFileInBin_IsNeverOverwritten_Exit6()
    {
        using var h = new CliHarness().Ready();
        File.WriteAllText(Path.Combine(h.Bin, "hello.exe"), "MINE");
        var run = CliHarness.Expect(6, h.Run("install", "hello@1.0.0", "--yes"));
        Assert.Contains("not managed by WinBrew", run.StdErr, StringComparison.Ordinal);
        Assert.Equal("MINE", File.ReadAllText(Path.Combine(h.Bin, "hello.exe")));
        Assert.Empty(h.StagingLeftovers());
        Assert.False(Directory.Exists(Path.Combine(h.Packages, "hello")));
    }

    [TestHooksFact]
    public void ShimOwnedByAnotherPackage_IsACollision_Exit6()
    {
        using var h = new CliHarness(r => r.AddHello().Add(new FixturePackage("hello-fork", "1.0.0",
            [("hello.exe", PeBytes.Create())], [("hello", "hello.exe")]))).Ready();
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        var run = CliHarness.Expect(6, h.Run("install", "hello-fork", "--yes"));
        Assert.Contains("already provided by local/hello", run.StdErr, StringComparison.Ordinal);
        h.AssertConsistent("1.0.0");
    }

    [TestHooksTheory]
    [InlineData(PeBytes.Arm32, "ARM32")]
    [InlineData((ushort)0x1234, "unknown")]
    public void Executable_WithARejectedMachineType_IsRefused_Exit5(ushort machine, string expected)
    {
        using var h = new CliHarness(r => r.Add(new FixturePackage("tool", "1.0.0",
            [("t.exe", PeBytes.Create(machine))], [("tool", "t.exe")]))).Ready();
        var run = CliHarness.Expect(5, h.Run("install", "tool", "--yes"));
        Assert.Contains(expected, run.StdErr, StringComparison.Ordinal);
        Assert.Empty(h.StagingLeftovers());
    }

    [TestHooksFact]
    public void Executable_X86_IsAcceptedOnX64() // A10 table: I386 (incl. AnyCPU .NET Framework images)
    {
        using var h = new CliHarness(r => r.Add(new FixturePackage("old", "1.0.0",
            [("o.exe", PeBytes.Create(PeBytes.I386))], [("old", "o.exe")]))).Ready();
        CliHarness.Expect(0, h.Run("install", "old", "--yes"));
    }

    [TestHooksFact]
    public void X86OnlyArtifact_IsAnExplicitFallback_WithAWarning()
    {
        using var h = new CliHarness(r => r.Add(new FixturePackage("legacy", "1.0.0",
            [("l.exe", PeBytes.Create(PeBytes.I386))], [("legacy", "l.exe")], Architecture: "x86"))).Ready();
        var json = CliHarness.Expect(0, h.Run("install", "legacy", "--yes", "--json")).Json();
        Assert.True(CliHarness.PlanItem(json, "legacy").GetProperty("architectureFallback").GetBoolean());
        Assert.Contains(json.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("fallback", StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(h.Packages, "legacy", "1.0.0", "x86")));
    }

    [TestHooksFact]
    public void AmbiguousId_AcrossRepositories_IsAUsageError_ListingCandidates()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(0, h.Run("test-ingest", "other", h.Server.BaseUrl.ToString()));
        var run = CliHarness.Expect(2, h.Run("install", "hello", "--yes", "--json"));
        var candidates = run.Json().GetProperty("error").GetProperty("details").GetProperty("candidates").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Equal(["local/hello", "other/hello"], candidates);
        CliHarness.Expect(0, h.Run("install", "other/hello@1.0.0", "--yes"));
    }

    [TestHooksFact]
    public void BothAtVersionAndVersionOption_IsAUsageError()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(2, h.Run("install", "hello@1.0.0", "--version", "2.0.0"));
    }

    [TestHooksFact]
    public void WritesToInstallDir_IsWarnedByInfo()
    {
        using var h = new CliHarness(r => r.Add(new FixturePackage("stateful", "1.0.0",
            [("s.exe", PeBytes.Create())], [("stateful", "s.exe")], WritesToInstallDir: true))).Ready();
        CliHarness.Expect(0, h.Run("install", "stateful", "--yes"));
        var info = CliHarness.Expect(0, h.Run("info", "stateful", "--json")).Json();
        Assert.True(info.GetProperty("data").GetProperty("writesToInstallDir").GetBoolean());
        Assert.Contains(info.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("install directory", StringComparison.Ordinal));
    }

    // ---- download and metadata rejection paths (A11 step 3–4, D6) ---------------------------

    [TestHooksFact]
    public void CorruptArtifact_IsRejected_StagingAndCacheStayClean()
    {
        using var h = new CliHarness().Ready();
        var artifact = h.Repository.ArtifactPath(new FixturePackage("hello", "1.0.0", [], []));
        var bytes = File.ReadAllBytes(artifact);
        bytes[^30] ^= 0xFF; // same size, different hash
        File.WriteAllBytes(artifact, bytes);

        var run = CliHarness.Expect(5, h.Run("install", "hello@1.0.0", "--yes"));
        Assert.Contains("Checksum mismatch", run.StdErr, StringComparison.Ordinal);
        h.AssertConsistent([null]);
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Root, "cache", "artifacts")));
    }

    [TestHooksFact]
    public void TamperedManifest_IsRejected_Exit5()
    {
        using var h = new CliHarness().Ready();
        var manifest = h.Repository.ManifestPath("hello", "1.0.0");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("hello fixture", "HELLO fixture", StringComparison.Ordinal));
        var run = CliHarness.Expect(5, h.Run("install", "hello@1.0.0", "--yes"));
        Assert.Contains("digest mismatch", run.StdErr, StringComparison.Ordinal);
        h.AssertConsistent([null]);
    }

    [TestHooksFact]
    public void MissingArtifact_IsANetworkFailure_Exit4()
    {
        using var h = new CliHarness().Ready();
        File.Delete(h.Repository.ArtifactPath(new FixturePackage("hello", "1.0.0", [], [])));
        CliHarness.Expect(4, h.Run("install", "hello@1.0.0", "--yes"));
        h.AssertConsistent([null]);
    }

    [TestHooksFact]
    public void InterruptedDownload_IsRetried_ThenRejected_StagingClean()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(0, h.Run("config", "set", "download.retries", "1"));
        var artifact = new FixturePackage("hello", "1.0.0", [], []);
        var body = File.ReadAllBytes(h.Repository.ArtifactPath(artifact));
        var path = "/artifacts/" + Path.GetFileName(h.Repository.ArtifactPath(artifact));
        h.Server.Handle(path, ctx => LocalHttpServer.DropAfterAsync(ctx, body, body.Length / 2));

        CliHarness.Expect(4, h.Run("install", "hello@1.0.0", "--yes"));
        Assert.Equal(2, h.Server.RequestedPaths.Count(p => p == path)); // 1 try + 1 retry
        h.AssertConsistent([null]);

        h.Server.Reset(path); // the server recovers: the next install succeeds
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
    }

    [TestHooksFact]
    public void CorruptCacheEntry_IsDiscarded_AndRedownloaded()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        CliHarness.Expect(0, h.Run("uninstall", "hello", "--yes"));
        var entry = Assert.Single(Directory.GetFiles(Path.Combine(h.Root, "cache", "artifacts")));
        var expected = Path.GetFileName(entry);
        File.WriteAllText(entry, "rotten");

        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(entry))));
    }

    [TestHooksFact]
    public void Offline_UsesOnlyTheVerifiedCache()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(4, h.Run("install", "hello@1.0.0", "--yes", "--offline"));
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        CliHarness.Expect(0, h.Run("uninstall", "hello", "--yes"));
        var requests = h.Server.Requests.Count;
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes", "--offline"));
        Assert.Equal(requests, h.Server.Requests.Count);
    }

    [TestHooksFact]
    public void IndexRollback_IsRejected_AndExpiredIndex_IsRejected()
    {
        using var h = new CliHarness();
        h.Repository.IndexVersion = 5;
        h.Repository.Publish();
        h.Ready();

        h.Repository.IndexVersion = 4;
        h.Repository.Publish();
        var rollback = CliHarness.Expect(5, h.Run("test-ingest", CliHarness.RepoName, h.Server.BaseUrl.ToString()));
        Assert.Contains("rollback", rollback.StdErr, StringComparison.Ordinal);

        h.Repository.IndexVersion = 6;
        h.Repository.Expires = DateTimeOffset.UtcNow.AddHours(-1);
        h.Repository.Publish();
        var expired = CliHarness.Expect(5, h.Run("test-ingest", CliHarness.RepoName, h.Server.BaseUrl.ToString()));
        Assert.Contains("expired", expired.StdErr, StringComparison.Ordinal);
    }

    [TestHooksFact]
    public void HttpWithoutTestMode_IsRefused_EvenInATestHooksBuild()
    {
        using var h = new CliHarness().Ready();
        var run = CliHarness.Expect(5, h.RunWith(new Dictionary<string, string?> { ["WINBREW_TEST_MODE"] = null }, "install", "hello@1.0.0", "--yes"));
        Assert.Contains("localhost exception requires", run.StdErr, StringComparison.Ordinal);
    }
}
