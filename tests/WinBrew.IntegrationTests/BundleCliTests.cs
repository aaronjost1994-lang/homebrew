using System.Text.Json;
using System.Text.Json.Nodes;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>
/// A15 end to end: bundle export + locked restore reproduces exact versions
/// and digests; a missing or different entry fails explicitly and changes
/// nothing; an offline locked restore works from the verified cache; offline
/// use of expired metadata warns with its age.
/// </summary>
public sealed class BundleCliTests
{
    private static (string Bundle, string Lock) Paths(CliHarness h) =>
        (Path.Combine(h.Work, "dev.json"), Path.Combine(h.Work, "dev.lock.json"));

    /// <summary>app 1.0.0 + lib 1.5.0 installed and exported, then both removed (artifacts stay in the cache).</summary>
    private static CliHarness ExportedThenRemoved()
    {
        var h = new CliHarness(DependencyCliTests.AppAndLib).Ready();
        CliHarness.Expect(0, h.Run("install", "app@1.0.0", "--yes"));
        CliHarness.Expect(0, h.Run("pin", "app"));
        var (bundle, _) = Paths(h);
        CliHarness.Expect(0, h.Run("bundle", "export", "--file", bundle));
        CliHarness.Expect(0, h.Run("unpin", "app"));
        CliHarness.Expect(0, h.Run("uninstall", "app", "--yes"));
        CliHarness.Expect(0, h.Run("autoremove", "--yes"));
        Assert.Null(h.Installed("lib"));
        return h;
    }

    [TestHooksFact]
    public void Export_WritesBundleAndLockfile_WithRepositoryIdentityAndExactDigests()
    {
        using var h = ExportedThenRemoved();
        var (bundlePath, lockPath) = Paths(h);
        var bundle = JsonNode.Parse(File.ReadAllText(bundlePath))!;
        Assert.Equal("1", bundle["schemaVersion"]!.GetValue<string>());
        var package = Assert.Single(bundle["packages"]!.AsArray())!;
        Assert.Equal("local/app", package["id"]!.GetValue<string>());
        Assert.True(package["pinned"]!.GetValue<bool>());

        var lockfile = JsonNode.Parse(File.ReadAllText(lockPath))!;
        var repo = Assert.Single(lockfile["repositories"]!.AsArray())!;
        Assert.Equal(h.Repository.Signer.RootFingerprints, repo["rootKeyFingerprints"]!.AsArray().Select(f => f!.GetValue<string>()));
        var entries = lockfile["packages"]!.AsArray().ToDictionary(p => p!["id"]!.GetValue<string>(), p => p!);
        Assert.Equal("1.0.0", entries["app"]["version"]!.GetValue<string>());
        Assert.Equal("1.5.0", entries["lib"]["version"]!.GetValue<string>());
        Assert.True(entries["app"]["explicit"]!.GetValue<bool>());
        Assert.False(entries["lib"]["explicit"]!.GetValue<bool>());
        Assert.Equal("x64", entries["lib"]["architecture"]!.GetValue<string>());
        Assert.Equal(Sha(h.Repository.ManifestPath("lib", "1.5.0")), entries["lib"]["manifestSha256"]!.GetValue<string>());
        Assert.Equal(Sha(Path.Combine(h.Repository.Directory, "artifacts", "lib-1.5.0-x64.zip")), entries["lib"]["artifactSha256"]!.GetValue<string>());
    }

    [TestHooksFact]
    public void LockedRestore_ReproducesExactVersions_EvenWhenNewerOnesExist_AndRestoresThePin()
    {
        using var h = ExportedThenRemoved();
        var (bundle, _) = Paths(h);

        var json = CliHarness.Expect(0, h.Run("bundle", "install", "--file", bundle, "--locked", "--yes", "--json")).Json();
        Assert.Equal(["lib", "app"], json.GetProperty("data").GetProperty("install").GetProperty("plan").GetProperty("items")
            .EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        Assert.Equal("1.0.0", h.Installed("app")!.Version);
        Assert.Equal("1.5.0", h.Installed("lib")!.Version);
        Assert.Contains("local/app", json.GetProperty("data").GetProperty("pinned").EnumerateArray().Select(e => e.GetString()));
        CliHarness.Expect(6, h.Run("upgrade", "app", "--yes")); // pinned again (D2 rule 1)

        // Without --locked the bundle re-resolves: app (no range) goes to the newest.
        CliHarness.Expect(0, h.Run("unpin", "app"));
        CliHarness.Expect(0, h.Run("uninstall", "app", "--yes"));
        CliHarness.Expect(0, h.Run("autoremove", "--yes"));
        var unpinned = Path.Combine(h.Work, "plain.json");
        File.WriteAllText(unpinned, File.ReadAllText(bundle).Replace("\"pinned\": true", "\"pinned\": false", StringComparison.Ordinal));
        CliHarness.Expect(0, h.Run("bundle", "install", "--file", unpinned, "--yes"));
        Assert.Equal("2.0.0", h.Installed("app")!.Version);
        Assert.True(File.Exists(Path.Combine(h.Work, "plain.lock.json")), "a resolved bundle install writes its lockfile");
    }

    [TestHooksTheory]
    [InlineData("missing-requested", 3)]
    [InlineData("missing-dependency", 3)]
    [InlineData("artifact-digest", 5)]
    [InlineData("manifest-digest", 5)]
    [InlineData("root-fingerprint", 5)]
    public void LockedRestore_MissingOrDifferentEntry_FailsExplicitly_AndChangesNothing(string defect, int exit)
    {
        using var h = ExportedThenRemoved();
        var (bundle, lockPath) = Paths(h);
        var lockfile = JsonNode.Parse(File.ReadAllText(lockPath))!;
        var packages = lockfile["packages"]!.AsArray();
        JsonNode Entry(string id) => packages.Single(p => p!["id"]!.GetValue<string>() == id)!;
        switch (defect)
        {
            case "missing-requested": packages.Remove(Entry("app")); break;
            case "missing-dependency": packages.Remove(Entry("lib")); break;
            case "artifact-digest": Entry("lib")["artifactSha256"] = new string('b', 64); break;
            case "manifest-digest": Entry("lib")["manifestSha256"] = new string('c', 64); break;
            case "root-fingerprint": lockfile["repositories"]![0]!["rootKeyFingerprints"] = new JsonArray(new string('d', 64)); break;
        }

        File.WriteAllText(lockPath, lockfile.ToJsonString());
        var before = DependencyCliTests.Snapshot(h.Root);
        var run = CliHarness.Expect(exit, h.Run("bundle", "install", "--file", bundle, "--locked", "--yes"));
        Assert.Contains("A15", run.StdErr, StringComparison.Ordinal);
        Assert.Null(h.Installed("app"));
        Assert.Null(h.Installed("lib"));
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), DependencyCliTests.Snapshot(h.Root).Keys.Order(StringComparer.Ordinal));
    }

    [TestHooksFact]
    public void OfflineLockedRestore_FromTheVerifiedCache_MakesNoRequest_AndWarnsAboutExpiredMetadataWithItsAge()
    {
        using var h = ExportedThenRemoved();
        var (bundle, _) = Paths(h);
        var requests = h.Server.Requests.Count;
        var later = CliHarness.At(DateTimeOffset.UtcNow.AddDays(10));
        var json = CliHarness.Expect(0, h.RunWith(later, "bundle", "install", "--file", bundle, "--locked", "--offline", "--yes", "--json")).Json();
        Assert.Equal(requests, h.Server.Requests.Count);
        Assert.Contains(json.GetProperty("warnings").EnumerateArray(), w =>
            w.GetString()!.Contains("metadata that expired 4 days ago", StringComparison.Ordinal));
        Assert.Equal("1.0.0", h.Installed("app")!.Version);
    }

    [TestHooksFact]
    public void BundleInstall_NamingAnUnconfiguredRepository_Exits3_AndNeverAddsTrust()
    {
        using var h = new CliHarness().Ready();
        var bundle = Path.Combine(h.Work, "other.json");
        File.WriteAllText(bundle, JsonSerializer.Serialize(new
        {
            schemaVersion = "1",
            repositories = new[] { new { name = "other", url = "https://example.org/repo" } },
            packages = new[] { new { id = "other/thing" } },
        }));
        var run = CliHarness.Expect(3, h.Run("bundle", "install", "--file", bundle, "--yes"));
        Assert.Contains("trust is never added implicitly", run.StdErr, StringComparison.Ordinal);
        CliHarness.Expect(3, h.Run("bundle", "install", "--file", Path.Combine(h.Work, "nope.json"), "--yes"));
    }

    private static string Sha(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
}
