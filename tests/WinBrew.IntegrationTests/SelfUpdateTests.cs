using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinBrew.Core.Shims;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>
/// A signed self-update feed (A14) served under &lt;server&gt;/feed/: the same
/// envelope, roles and key model as D6. The INITIAL root (the one a build
/// compiles in) is written to a file and handed to the test-hooks CLI via
/// WINBREW_TEST_RELEASE_ROOT; the feed may then rotate past it.
/// </summary>
public sealed class ReleaseFeed
{
    public const string NewVersion = "99.0.0";
    public const string NewTemplate = "TEMPLATE-FROM-99";

    public ReleaseFeed(CliHarness h)
    {
        Harness = h;
        Directory = Path.Combine(h.Repository.Directory, "feed");
        System.IO.Directory.CreateDirectory(Directory);
        InitialRootPath = Path.Combine(h.Work, "initial-release-root.json");
        File.WriteAllBytes(InitialRootPath, Signer.CurrentRoot);
    }

    public CliHarness Harness { get; }

    public RepositorySigner Signer { get; } = new();

    public string Directory { get; }

    public string InitialRootPath { get; }

    public Uri Url => new(Harness.Server.BaseUrl, "feed/");

    public int Version { get; set; } = 1;

    public byte[] Zip { get; private set; } = [];

    /// <summary>Publishes releases.json (as index.json) with one x64 zip for <see cref="NewVersion"/>.</summary>
    public ReleaseFeed Publish(byte[]? zipOverride = null, string? declaredSha = null)
    {
        Zip = zipOverride ?? ZipBytes.Create(
            ("wbrew.exe", "WBREW-99"), ("e_sqlite3.dll", "SQLITE-99"), ("wbrew-shim.exe", NewTemplate));
        var zipPath = Path.Combine(Directory, $"wbrew-{NewVersion}-win-x64.zip");
        File.WriteAllBytes(zipPath, Zip);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["version"] = Version,
            ["expires"] = RepositorySigner.Iso(DateTimeOffset.UtcNow.AddDays(20)),
            ["releases"] = new JsonObject
            {
                [NewVersion] = new JsonObject
                {
                    ["artifacts"] = new JsonArray(new JsonObject
                    {
                        ["architecture"] = "x64",
                        ["url"] = new Uri(Url, Path.GetFileName(zipPath)).ToString(),
                        ["sha256"] = declaredSha ?? Convert.ToHexStringLower(SHA256.HashData(Zip)),
                        ["size"] = Zip.LongLength,
                    }),
                },
            },
        });
        var index = Signer.SignIndex(payload, RepositorySigner.ReleasesType);
        File.WriteAllBytes(Path.Combine(Directory, "index.json"), index);
        File.WriteAllBytes(Path.Combine(Directory, "timestamp.json"), Signer.SignTimestamp(Version, DateTimeOffset.UtcNow.AddDays(6), index));
        Signer.WriteRoots(Directory);
        return this;
    }

    public Dictionary<string, string?> Env(Dictionary<string, string?>? extra = null)
    {
        var env = new Dictionary<string, string?>
        {
            ["WINBREW_TEST_RELEASE_FEED"] = Url.ToString(),
            ["WINBREW_TEST_RELEASE_ROOT"] = InitialRootPath,
        };
        foreach (var (k, v) in extra ?? [])
        {
            env[k] = v;
        }

        return env;
    }

    public CliRun Run(params string[] args) => Harness.RunWith(Env(), args);

    public CliRun RunFault(string checkpoint, params string[] args) =>
        Harness.RunWith(Env(new() { ["WINBREW_FAULT_AT"] = checkpoint }), args);

    public string ActiveTarget()
    {
        var sidecar = ShimSidecar.TryParse(File.ReadAllBytes(Path.Combine(Harness.Bin, "wbrew.shim.json")))!;
        return sidecar.Target;
    }
}

/// <summary>A14: self update against a signed fixture feed; self uninstall.</summary>
public sealed class SelfUpdateTests
{
    [Fact]
    public void SelfUpdate_WithoutAConfiguredFeed_Exits3_WithTheDocumentedMessage()
    {
        using var h = new CliHarness();
        var run = CliHarness.Expect(3, h.Run("self", "update"));
        Assert.Contains("self-update not configured for this build", run.StdErr, StringComparison.Ordinal);
    }

    [TestHooksFact]
    public void SelfUpdate_FollowsTheFeedRootRotation_AndSwitchesEveryShim()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(0, h.Run("install", "hello@2.0.0", "--yes"));
        var feed = new ReleaseFeed(h);
        feed.Signer.RotateRoot(); // compiled-in root v1 → feed root v2 (cross-signed)
        feed.Publish();

        var dry = CliHarness.Expect(0, feed.Run("self", "update", "--dry-run", "--json")).Json().GetProperty("data");
        Assert.Equal(ReleaseFeed.NewVersion, dry.GetProperty("availableVersion").GetString());
        Assert.False(Directory.Exists(Path.Combine(h.Root, "app", ReleaseFeed.NewVersion)));

        CliHarness.Expect(10, feed.Run("self", "update")); // non-interactive without --yes (A8)
        var data = CliHarness.Expect(0, feed.Run("self", "update", "--yes", "--json")).Json().GetProperty("data");
        Assert.True(data.GetProperty("updated").GetBoolean());
        Assert.Equal(2, data.GetProperty("feedRootVersion").GetInt32());

        var app = Path.Combine(h.Root, "app", ReleaseFeed.NewVersion);
        Assert.Equal(Path.Combine(app, "wbrew.exe"), feed.ActiveTarget());
        foreach (var file in new[] { "wbrew.exe", "e_sqlite3.dll", "wbrew-shim.exe" })
        {
            Assert.True(File.Exists(Path.Combine(app, file)), file);
        }

        foreach (var shim in new[] { "wbrew", "hello", "hi" })
        {
            Assert.Equal(ReleaseFeed.NewTemplate, File.ReadAllText(Path.Combine(h.Bin, shim + ".exe")));
        }

        Assert.True(Directory.Exists(Path.Combine(h.Root, "app", "0.0.0-test")), "the previous version stays (A14)");
        Assert.Empty(h.StagingLeftovers());
        Assert.Empty(h.ListWarnings());

        // The feed root v2 is now the accepted one: the next check starts there.
        var again = CliHarness.Expect(0, feed.Run("self", "update", "--yes", "--json")).Json().GetProperty("data");
        Assert.False(again.GetProperty("updated").GetBoolean());
    }

    [TestHooksFact]
    public void SelfUpdate_RotationWithoutCrossSigning_IsRejected_AndNothingChanges()
    {
        using var h = new CliHarness().Ready();
        var feed = new ReleaseFeed(h);
        feed.Signer.RotateRoot(crossSign: false);
        feed.Publish();
        var run = CliHarness.Expect(5, feed.Run("self", "update", "--yes"));
        Assert.Contains("signed by root 1", run.StdErr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(h.Root, "app", ReleaseFeed.NewVersion)));
    }

    [TestHooksFact]
    public void SelfUpdate_ZipDigestMismatch_Exits5_AndTheRunningVersionStaysActive()
    {
        using var h = new CliHarness().Ready();
        var feed = new ReleaseFeed(h).Publish(declaredSha: new string('a', 64));
        var before = feed.ActiveTarget();
        CliHarness.Expect(5, feed.Run("self", "update", "--yes"));
        Assert.Equal(before, feed.ActiveTarget());
        Assert.False(Directory.Exists(Path.Combine(h.Root, "app", ReleaseFeed.NewVersion)));
        Assert.Empty(h.StagingLeftovers());
        Assert.Empty(h.ListWarnings());
    }

    [TestHooksFact]
    public void SelfUninstall_RemovesShimsPackagesAndState_ButKeepDataKeepsThem()
    {
        using (var keep = new CliHarness().Ready())
        {
            CliHarness.Expect(0, keep.Run("install", "hello@1.0.0", "--yes"));
            CliHarness.Expect(10, keep.Run("self", "uninstall", "--keep-data"));
            CliHarness.Expect(0, keep.Run("self", "uninstall", "--keep-data", "--yes"));
            Assert.Empty(Directory.GetFiles(keep.Bin, "*.exe"));
            Assert.True(Directory.Exists(Path.Combine(keep.Packages, "hello")));
            Assert.True(File.Exists(Path.Combine(keep.Root, "state", "winbrew.db")));
        }

        using var h = new CliHarness().Ready();
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        var data = CliHarness.Expect(0, h.Run("self", "uninstall", "--yes", "--json")).Json().GetProperty("data");
        Assert.True(data.GetProperty("dataRemoved").GetBoolean());
        Assert.Empty(Directory.GetFiles(h.Bin, "*.exe"));
        Assert.False(Directory.Exists(h.Packages));
        Assert.False(File.Exists(Path.Combine(h.Root, "state", "winbrew.db")));
        Assert.False(File.Exists(Path.Combine(h.Root, "config.json")));
        Assert.Contains(data.GetProperty("removeManually").EnumerateArray(), p => p.GetString() == h.Root);
    }
}
