using System.Security.Cryptography;
using System.Text.Json.Nodes;
using WinBrew.Core.Shims;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>
/// Phase C through the real CLI: dependency graphs (D2), explicit/dependency
/// tracking and edges (A10, D8), conflicts that change nothing, upgrade,
/// pins, architecture re-selection, autoremove and cleanup.
/// </summary>
public sealed class DependencyCliTests
{
    /// <summary>app 1.0.0 → lib [1.0.0,2.0.0); app 2.0.0 → lib [2.0.0,3.0.0); lib 1.0.0 / 1.5.0 / 2.0.0.</summary>
    internal static void AppAndLib(FixtureRepository r)
    {
        // Each version's archive carries a VERSION file, so every artifact has its own digest
        // (identical archives would be one content-addressed cache entry, A11).
        static (string, byte[])[] Files(string exe, string version) => [(exe, PeBytes.Create()), ("VERSION", System.Text.Encoding.UTF8.GetBytes(version))];
        r.Add(new FixturePackage("lib", "1.0.0", Files("lib.exe", "1.0.0"), [("libtool", "lib.exe")]));
        r.Add(new FixturePackage("lib", "1.5.0", Files("lib.exe", "1.5.0"), [("libtool", "lib.exe")]));
        r.Add(new FixturePackage("lib", "2.0.0", Files("lib.exe", "2.0.0"), [("libtool", "lib.exe")]));
        r.Add(new FixturePackage("app", "1.0.0", Files("app.exe", "1.0.0"), [("app", "app.exe")], Dependencies: [("lib", "[1.0.0,2.0.0)")]));
        r.Add(new FixturePackage("app", "2.0.0", Files("app.exe", "2.0.0"), [("app", "app.exe")], Dependencies: [("lib", "[2.0.0,3.0.0)")]));
    }

    private static Dictionary<string, (string Version, bool Explicit)> Installed(CliHarness h)
    {
        var list = CliHarness.Expect(0, h.Run("list", "--json")).Json().GetProperty("data").GetProperty("packages");
        return list.EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!,
            p => (p.GetProperty("version").GetString()!, p.GetProperty("explicit").GetBoolean()));
    }

    /// <summary>Content hashes of every file under the root, except the lock file and SQLite's WAL/SHM side files.</summary>
    internal static Dictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("winbrew.lock", StringComparison.Ordinal) && !f.EndsWith("winbrew.lock.guard", StringComparison.Ordinal) &&
                        !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(root, f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));

    [TestHooksFact]
    public void Install_ResolvesTheGraph_DependenciesFirst_TracksExplicitAndEdges()
    {
        using var h = new CliHarness(AppAndLib).Ready();
        var json = CliHarness.Expect(0, h.Run("install", "app@1.0.0", "--yes", "--json")).Json();
        var items = json.GetProperty("data").GetProperty("plan").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["lib", "app"], items.Select(i => i.GetProperty("id").GetString()));   // dependencies first
        Assert.Equal("1.5.0", items[0].GetProperty("version").GetString());                 // highest in [1.0.0,2.0.0)
        Assert.False(items[0].GetProperty("explicit").GetBoolean());

        var installed = Installed(h);
        Assert.Equal(("1.5.0", false), installed["lib"]);
        Assert.Equal(("1.0.0", true), installed["app"]);

        // D8: the dependency cannot be removed while app needs it.
        var refused = CliHarness.Expect(6, h.Run("uninstall", "lib", "--yes"));
        Assert.Contains("required by local/app", refused.StdErr, StringComparison.Ordinal);

        // Uninstalling app leaves lib (dependency-only) until autoremove.
        CliHarness.Expect(0, h.Run("uninstall", "app", "--yes"));
        Assert.True(Installed(h).ContainsKey("lib"));
        var dry = CliHarness.Expect(0, h.Run("autoremove", "--dry-run", "--json")).Json().GetProperty("data").GetProperty("removed");
        Assert.Equal("lib", Assert.Single(dry.EnumerateArray()).GetProperty("id").GetString());
        Assert.True(Installed(h).ContainsKey("lib")); // dry run changed nothing
        CliHarness.Expect(0, h.Run("autoremove", "--yes"));
        Assert.Empty(Installed(h));
        Assert.Empty(h.PackageShimFiles(".exe"));
    }

    [TestHooksFact]
    public void InstallingADependencyByName_MakesItExplicit_AndAutoremoveKeepsIt()
    {
        using var h = new CliHarness(AppAndLib).Ready();
        CliHarness.Expect(0, h.Run("install", "app@1.0.0", "--yes"));
        CliHarness.Expect(0, h.Run("install", "lib", "--yes")); // already 1.5.0: promotion only
        Assert.True(Installed(h)["lib"].Explicit);
        CliHarness.Expect(0, h.Run("uninstall", "app", "--yes"));
        CliHarness.Expect(0, h.Run("autoremove", "--yes"));
        Assert.True(Installed(h).ContainsKey("lib"));
    }

    [TestHooksFact]
    public void Conflict_FailsWithAnExplanation_AndChangesNothing()
    {
        // Only app 1.0.0 exists (needs lib 1.x); other needs lib 2.x — no plan satisfies both.
        // (With app 2.0.0 in the catalog the resolver would rightly upgrade app instead.)
        using var h = new CliHarness(r =>
        {
            r.Add(new FixturePackage("lib", "1.5.0", [("lib.exe", PeBytes.Create())], [("libtool", "lib.exe")]));
            r.Add(new FixturePackage("lib", "2.0.0", [("lib.exe", PeBytes.Create())], [("libtool", "lib.exe")]));
            r.Add(new FixturePackage("app", "1.0.0", [("app.exe", PeBytes.Create())], [("app", "app.exe")], Dependencies: [("lib", "[1.0.0,2.0.0)")]));
            r.Add(new FixturePackage("other", "1.0.0", [("o.exe", PeBytes.Create())], [("other", "o.exe")], Dependencies: [("lib", "[2.0.0,3.0.0)")]));
        }).Ready();
        CliHarness.Expect(0, h.Run("install", "app@1.0.0", "--yes"));
        var before = Snapshot(h.Root);
        var requests = h.Server.Requests.Count;

        var run = CliHarness.Expect(6, h.Run("install", "other", "--yes", "--json"));
        var conflict = run.Json().GetProperty("error").GetProperty("details").GetProperty("conflict");
        var explanation = string.Join("\n", conflict.GetProperty("explanation").EnumerateArray().Select(l => l.GetString()));
        Assert.Contains("local/other 1.0.0 depends on local/lib [2.0.0,3.0.0)", explanation, StringComparison.Ordinal);
        Assert.Contains("local/app 1.0.0 depends on local/lib [1.0.0,2.0.0)", explanation, StringComparison.Ordinal);
        Assert.Contains(conflict.GetProperty("causes").EnumerateArray(), c => c.GetProperty("kind").GetString() == "dependency");

        Assert.Equal(before, Snapshot(h.Root));          // zero filesystem/state changes (snapshot comparison)
        Assert.Equal(requests, h.Server.Requests.Count); // not even a manifest fetch
    }

    [TestHooksFact]
    public void Install_MayUpgradeAnInstalledPackage_WhenThatIsTheOnlyWay_AndShowsItInThePlan()
    {
        using var h = new CliHarness(r =>
        {
            AppAndLib(r);
            r.Add(new FixturePackage("other", "1.0.0", [("o.exe", PeBytes.Create())], [("other", "o.exe")], Dependencies: [("lib", "[2.0.0,3.0.0)")]));
        }).Ready();
        CliHarness.Expect(0, h.Run("install", "app@1.0.0", "--yes"));
        var json = CliHarness.Expect(0, h.Run("install", "other", "--yes", "--json")).Json();
        Assert.Equal("upgrade", CliHarness.PlanItem(json, "app").GetProperty("action").GetString());
        Assert.Equal("2.0.0", Installed(h)["lib"].Version);
    }

    [TestHooksFact]
    public void ManifestDependenciesThatDisagreeWithTheCatalog_Exit5_AndChangeNothing()
    {
        using var h = new CliHarness(AppAndLib);
        // index.json claims app 1.0.0 has NO dependencies; its (digest-matching) manifest says lib [1.0.0,2.0.0).
        h.Repository.EditIndex(index => index["packages"]!["app"]!["versions"]!["1.0.0"]!["dependencies"] = new JsonArray());
        h.Ready();
        var before = Snapshot(h.Root);

        var run = CliHarness.Expect(5, h.Run("install", "app@1.0.0", "--yes"));
        Assert.Contains("does not match its index.json entry", run.StdErr, StringComparison.Ordinal);
        Assert.Contains("dependencies", run.StdErr, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(h.Root));
    }

    [TestHooksFact]
    public void QualifiedDependencyOnAnUnconfiguredRepository_Exit3_NamesIt()
    {
        using var h = new CliHarness(r => r.Add(new FixturePackage("app", "1.0.0", [("a.exe", PeBytes.Create())], [("app", "a.exe")],
            Dependencies: [("ghost/lib", "[1.0.0,2.0.0)")]))).Ready();
        var run = CliHarness.Expect(3, h.Run("install", "app", "--yes", "--json"));
        Assert.Equal("ghost", run.Json().GetProperty("error").GetProperty("details").GetProperty("missingRepository").GetString());
    }

    [TestHooksFact]
    public void SamePlan_WithAnotherRepositoryHoldingSameNamedPackages()
    {
        using var h = new CliHarness(AppAndLib).Ready();
        var alone = CliHarness.Expect(0, h.Run("install", "local/app", "--dry-run", "--json")).Json()
            .GetProperty("data").GetProperty("plan").GetProperty("items").ToString();
        CliHarness.Expect(0, h.AddRepo("other"));
        var withOther = CliHarness.Expect(0, h.Run("install", "local/app", "--dry-run", "--json")).Json()
            .GetProperty("data").GetProperty("plan").GetProperty("items").ToString();
        Assert.Equal(alone, withOther);
    }

    [TestHooksFact]
    public void Upgrade_MovesTheWholeGraph_AndOutdatedReportsIt()
    {
        using var h = new CliHarness(AppAndLib).Ready();
        CliHarness.Expect(0, h.Run("install", "app@1.0.0", "--yes"));

        var outdated = CliHarness.Expect(0, h.Run("outdated", "--json")).Json().GetProperty("data").GetProperty("packages");
        Assert.Equal(["local/app 1.0.0 → 2.0.0", "local/lib 1.5.0 → 2.0.0"],
            outdated.EnumerateArray().Select(r => $"{r.GetProperty("package").GetString()} {r.GetProperty("installed").GetString()} → {r.GetProperty("latest").GetString()}"));

        var before = Snapshot(h.Root);
        CliHarness.Expect(0, h.Run("upgrade", "--dry-run"));
        Assert.Equal(before, Snapshot(h.Root));

        var json = CliHarness.Expect(0, h.Run("upgrade", "--yes", "--json")).Json();
        Assert.Equal("upgrade", CliHarness.PlanItem(json, "app").GetProperty("action").GetString());
        Assert.Equal("2.0.0", CliHarness.PlanItem(json, "lib").GetProperty("version").GetString());
        var installed = Installed(h);
        Assert.Equal("2.0.0", installed["app"].Version);
        Assert.Equal("2.0.0", installed["lib"].Version);
        Assert.False(installed["lib"].Explicit); // still a dependency
        Assert.Empty(CliHarness.Expect(0, h.Run("outdated", "--json")).Json().GetProperty("data").GetProperty("packages").EnumerateArray());
    }

    [TestHooksFact]
    public void Pins_HoldAPackage_AndBlockConflictingInstalls()
    {
        using var h = new CliHarness(AppAndLib).Ready();
        CliHarness.Expect(0, h.Run("install", "app@1.0.0", "--yes"));
        CliHarness.Expect(0, h.Run("pin", "lib"));

        CliHarness.Expect(0, h.Run("upgrade", "--yes"));      // app 2.0.0 needs lib 2.x: stays at 1.0.0
        Assert.Equal("1.5.0", Installed(h)["lib"].Version);
        Assert.Equal("1.0.0", Installed(h)["app"].Version);

        Assert.Contains("pinned", CliHarness.Expect(6, h.Run("upgrade", "lib", "--yes")).StdErr, StringComparison.Ordinal);
        var conflict = CliHarness.Expect(6, h.Run("install", "app@2.0.0", "--yes"));
        Assert.Contains("pinned to 1.5.0", conflict.StdErr, StringComparison.Ordinal);
        Assert.True(CliHarness.Expect(0, h.Run("outdated", "--json")).Json().GetProperty("data").GetProperty("packages")
            .EnumerateArray().Single(r => r.GetProperty("package").GetString() == "local/lib").GetProperty("pinned").GetBoolean());

        CliHarness.Expect(0, h.Run("unpin", "lib"));
        CliHarness.Expect(0, h.Run("upgrade", "--yes"));
        Assert.Equal("2.0.0", Installed(h)["app"].Version);
        CliHarness.Expect(3, h.Run("pin", "nothing-installed"));
    }

    [TestHooksFact]
    public void Upgrade_ReSelectsTheArchitecture_AndRecordsIt()
    {
        using var h = new CliHarness(r =>
        {
            r.Add(new FixturePackage("tool", "1.0.0", [("t.exe", PeBytes.Create(PeBytes.I386))], [("tool", "t.exe")], Architecture: "x86"));
            r.Add(new FixturePackage("tool", "2.0.0", [("t.exe", PeBytes.Create())], [("tool", "t.exe")], Architecture: "x64"));
        }).Ready();
        CliHarness.Expect(0, h.Run("install", "tool@1.0.0", "--yes"));
        var json = CliHarness.Expect(0, h.Run("upgrade", "--yes", "--json")).Json();
        var item = CliHarness.PlanItem(json, "tool");
        Assert.Equal("x86 → x64", item.GetProperty("architectureTransition").GetString());
        Assert.Contains(json.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("x86 → x64", StringComparison.Ordinal));
        var list = CliHarness.Expect(0, h.Run("list", "--json")).Json().GetProperty("data").GetProperty("packages");
        Assert.Equal("x64", list.EnumerateArray().Single().GetProperty("arch").GetString());
        Assert.True(Directory.Exists(Path.Combine(h.Packages, "tool", "2.0.0", "x64")));
    }

    [TestHooksFact]
    public void CycleInTheCatalog_IsRefused_WithItsPath()
    {
        using var h = new CliHarness(r =>
        {
            r.Add(new FixturePackage("a", "1.0.0", [("a.exe", PeBytes.Create())], [], Dependencies: [("b", "[1.0.0,2.0.0)")]));
            r.Add(new FixturePackage("b", "1.0.0", [("b.exe", PeBytes.Create())], [], Dependencies: [("a", "[1.0.0,2.0.0)")]));
        }).Ready();
        var run = CliHarness.Expect(6, h.Run("install", "a", "--yes"));
        Assert.Contains("local/a → local/b → local/a", run.StdErr, StringComparison.Ordinal);
        Assert.Empty(Installed(h));
    }

    [TestHooksFact]
    public void Cleanup_NeverRemovesActivePinnedReferencedOrRetainedArtifacts()
    {
        using var h = new CliHarness(r =>
        {
            r.AddHello();
            r.Add(new FixturePackage("temp", "1.0.0", [("t.exe", PeBytes.Create())], [("temp", "t.exe")]));
        }).Ready();
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        CliHarness.Expect(0, h.Run("install", "hello@2.0.0", "--yes")); // 1.0.0 retained (retainVersions=1)
        CliHarness.Expect(0, h.Run("pin", "hello"));
        CliHarness.Expect(0, h.Run("install", "temp", "--yes"));
        CliHarness.Expect(0, h.Run("uninstall", "temp", "--yes"));        // temp's artifact is now unreferenced
        CliHarness.Expect(0, h.Run("config", "set", "cache.maxBytes", "0"));

        var cache = Path.Combine(h.Root, "cache", "artifacts");
        Assert.Equal(3, Directory.GetFiles(cache).Length);

        var dry = CliHarness.Expect(0, h.Run("cleanup", "--dry-run", "--json")).Json().GetProperty("data");
        Assert.Single(dry.GetProperty("removed").EnumerateArray());
        Assert.Equal(3, Directory.GetFiles(cache).Length); // dry run removes nothing

        var result = CliHarness.Expect(0, h.Run("cleanup", "--json")).Json();
        Assert.Single(result.GetProperty("data").GetProperty("removed").EnumerateArray());
        Assert.Equal(2, result.GetProperty("data").GetProperty("protected").GetInt32());
        Assert.Equal(2, Directory.GetFiles(cache).Length); // hello 2.0.0 (active, pinned) + 1.0.0 (retained)

        // If a protected artifact cannot be identified, cleanup removes nothing at all.
        File.Delete(Path.Combine(h.Root, "cache", "metadata", "local", "manifests", "hello", "1.0.0.json"));
        CliHarness.Expect(0, h.Run("install", "temp", "--yes"));
        CliHarness.Expect(0, h.Run("uninstall", "temp", "--yes"));
        var refused = CliHarness.Expect(0, h.Run("cleanup", "--json")).Json();
        Assert.Empty(refused.GetProperty("data").GetProperty("removed").EnumerateArray());
        Assert.Contains(refused.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("removed nothing", StringComparison.Ordinal));
    }

    [TestHooksFact]
    public void Shims_OfEveryPackageInThePlan_AreActivated()
    {
        using var h = new CliHarness(AppAndLib).Ready();
        CliHarness.Expect(0, h.Run("install", "app@1.0.0", "--yes"));
        Assert.Equal(["app", "libtool"], h.PackageShimFiles(".exe"));
        Assert.Equal("1.5.0", ShimSidecar.TryParse(File.ReadAllBytes(Path.Combine(h.Bin, "libtool.shim.json")))!.Version);
    }
}
