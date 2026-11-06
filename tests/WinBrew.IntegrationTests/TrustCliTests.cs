using System.Security.Cryptography;
using System.Text.Json;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>
/// D6 end to end through the real CLI: repo add bootstrap and consent, repo
/// list/remove, update with root rotation, the negative metadata matrix
/// (every rejection exits 5 and changes nothing), freshness rules (A8/A15)
/// and search.
/// </summary>
public sealed class TrustCliTests
{
    // ---- bootstrap / consent ---------------------------------------------------------------

    [TestHooksFact]
    public void RepoAdd_NonInteractive_WithoutTrustRoot_Exits10_AndAddsNothing()
    {
        using var h = new CliHarness();
        h.AllowLocalhost();
        var run = CliHarness.Expect(10, h.Run("repo", "add", "local", h.Server.BaseUrl.ToString(), "--yes", "--json"));
        var details = run.Json().GetProperty("error").GetProperty("details");
        Assert.Equal(h.Repository.Signer.RootFingerprints, details.GetProperty("rootKeyFingerprints").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(Repositories(h));
    }

    [TestHooksFact]
    public void RepoAdd_FingerprintMismatch_OrBelowRootThreshold_Exits5()
    {
        using var h = new CliHarness(r => r.AddHello());
        h.Repository.Signer = new RepositorySigner(rootKeys: 3, rootThreshold: 2);
        h.Repository.Publish();
        h.AllowLocalhost();

        var stranger = SigningKey.Create().Id;
        var mismatch = CliHarness.Expect(5, h.Run("repo", "add", "local", h.Server.BaseUrl.ToString(), "--trust-root", stranger));
        Assert.Contains("does not match any root key", mismatch.StdErr, StringComparison.Ordinal);

        var one = h.Repository.Signer.RootFingerprints[0];
        var below = CliHarness.Expect(5, h.Run("repo", "add", "local", h.Server.BaseUrl.ToString(), "--trust-root", one));
        Assert.Contains("threshold", below.StdErr, StringComparison.Ordinal);
        Assert.Empty(Repositories(h));

        CliHarness.Expect(0, h.AddRepo("local"));
        Assert.Single(Repositories(h));
    }

    [TestHooksFact]
    public void RepoList_ShowsTrustState_RepoRemove_RefusesWhileInstalled_ThenRemoves()
    {
        using var h = new CliHarness().Ready();
        var repo = Assert.Single(Repositories(h));
        Assert.Equal("local", repo.GetProperty("name").GetString());
        Assert.Equal(h.Repository.Signer.RootFingerprints, repo.GetProperty("rootKeyFingerprints").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, repo.GetProperty("rootVersion").GetInt32());
        Assert.False(repo.GetProperty("expired").GetBoolean());
        Assert.Equal(1, repo.GetProperty("packages").GetInt32());

        CliHarness.Expect(6, h.AddRepo("local")); // already configured
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        CliHarness.Expect(6, h.Run("repo", "remove", "local"));
        CliHarness.Expect(0, h.Run("uninstall", "hello", "--yes"));
        CliHarness.Expect(0, h.Run("repo", "remove", "local"));
        Assert.Empty(Repositories(h));
        CliHarness.Expect(3, h.Run("repo", "remove", "local"));
        CliHarness.Expect(3, h.Run("search", "hello"));
    }

    // ---- update and rotation -----------------------------------------------------------------

    [TestHooksFact]
    public void Update_WalksRootRotations_NToNPlusOne_AndAcceptsTheNewKeys()
    {
        using var h = new CliHarness().Ready();
        h.Repository.Signer.RotateRoot();                                   // v2, cross-signed by v1
        h.Repository.Signer.RotateRoot(newKeys: 2, threshold: 2);           // v3, 2-of-2, cross-signed by v2
        h.Repository.IndexVersion = 2;
        h.Repository.Publish();

        var row = Assert.Single(CliHarness.Expect(0, h.Run("update", "--json")).Json().GetProperty("data").GetProperty("repositories").EnumerateArray());
        Assert.Equal([2, 3], row.GetProperty("rotatedRoots").EnumerateArray().Select(e => e.GetInt32()));
        var repo = Assert.Single(Repositories(h));
        Assert.Equal(3, repo.GetProperty("rootVersion").GetInt32());
        Assert.Equal(h.Repository.Signer.RootFingerprints, repo.GetProperty("rootKeyFingerprints").EnumerateArray().Select(e => e.GetString()));
    }

    [TestHooksFact]
    public void Update_RotationWithoutCrossSigning_IsRejected()
    {
        using var h = new CliHarness().Ready();
        h.Repository.Signer.RotateRoot(crossSign: false);
        h.Repository.IndexVersion = 2;
        h.Repository.Publish();
        var run = CliHarness.Expect(5, h.Run("update"));
        Assert.Contains("signed by root 1", run.StdErr, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(Repositories(h)).GetProperty("rootVersion").GetInt32());
    }

    public enum Attack
    {
        TamperedPayload,
        WrongKey,
        NoSignatures,
        DuplicateKeyIds,
        WrongSignatureLength,
        IndexLongerThanDeclared,
        IndexDigestMismatch,
        ReplayedOldTimestamp,
        FreezeAttack,
        RolledBackIndex,
        WrongPayloadType,
    }

    /// <summary>Every attack exits 5 and leaves the accepted catalog and marks untouched.</summary>
    [TestHooksTheory]
    [InlineData(Attack.TamperedPayload)]
    [InlineData(Attack.WrongKey)]
    [InlineData(Attack.NoSignatures)]
    [InlineData(Attack.DuplicateKeyIds)]
    [InlineData(Attack.WrongSignatureLength)]
    [InlineData(Attack.IndexLongerThanDeclared)]
    [InlineData(Attack.IndexDigestMismatch)]
    [InlineData(Attack.ReplayedOldTimestamp)]
    [InlineData(Attack.FreezeAttack)]
    [InlineData(Attack.RolledBackIndex)]
    [InlineData(Attack.WrongPayloadType)]
    public void Update_RejectsAttack_Exit5_AndChangesNothing(Attack attack)
    {
        using var h = new CliHarness().Ready();
        var r = h.Repository;
        var signer = r.Signer;
        IReadOnlyDictionary<string, string?>? env = null;

        // Accept index v2 first so that replay/rollback have something to go below.
        r.IndexVersion = 2;
        var v2Timestamp = Publish(r);
        CliHarness.Expect(0, h.Run("update"));
        var before = Snapshot(h);

        r.Add(new FixturePackage("evil", "1.0.0", [("bin/evil.exe", PeBytes.Create())], [("evil", "bin/evil.exe")]));
        r.IndexVersion = 3;
        r.Publish();
        var payload = r.IndexPayload;
        switch (attack)
        {
            case Attack.TamperedPayload:
                var signed = signer.IndexKey.Sign(payload);
                var tampered = payload.ToArray();
                tampered[^3] ^= 0x01;
                r.WriteSignedMetadata(RepositorySigner.RawEnvelope(tampered, RepositorySigner.IndexType, [new(signer.IndexKey.Id, signed)]));
                break;
            case Attack.WrongKey:
                r.WriteSignedMetadata(RepositorySigner.Envelope(payload, RepositorySigner.IndexType, [SigningKey.Create()]));
                break;
            case Attack.NoSignatures:
                r.WriteSignedMetadata(RepositorySigner.RawEnvelope(payload, RepositorySigner.IndexType, []));
                break;
            case Attack.DuplicateKeyIds:
                var sig = signer.IndexKey.Sign(payload);
                r.WriteSignedMetadata(RepositorySigner.RawEnvelope(payload, RepositorySigner.IndexType,
                    [new(signer.IndexKey.Id, sig), new(signer.IndexKey.Id, sig)]));
                break;
            case Attack.WrongSignatureLength:
                var der = signer.IndexKey.Key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                r.WriteSignedMetadata(RepositorySigner.RawEnvelope(payload, RepositorySigner.IndexType, [new(signer.IndexKey.Id, der)]));
                break;
            case Attack.IndexLongerThanDeclared:
                var declared = File.ReadAllBytes(r.IndexPath);
                File.WriteAllBytes(r.IndexPath, [.. declared, .. "    "u8.ToArray()]); // timestamp still declares the shorter length
                break;
            case Attack.IndexDigestMismatch:
                var original = File.ReadAllBytes(r.IndexPath);
                var swapped = RepositorySigner.Envelope(payload, RepositorySigner.IndexType, [signer.IndexKey]); // fresh signature bytes, same length
                Assert.Equal(original.Length, swapped.Length);
                File.WriteAllBytes(r.IndexPath, swapped);
                break;
            case Attack.ReplayedOldTimestamp:
                File.WriteAllBytes(r.TimestampPath, OlderTimestamp(signer, r));
                break;
            case Attack.FreezeAttack:
                // The server keeps serving the accepted (validly signed) timestamp v2 after it expired.
                File.WriteAllBytes(r.TimestampPath, v2Timestamp);
                env = CliHarness.At(DateTimeOffset.UtcNow.AddDays(8));
                break;
            case Attack.RolledBackIndex:
                r.IndexVersion = 1;
                r.TimestampVersion = 4;
                r.Publish();
                break;
            case Attack.WrongPayloadType:
                r.WriteSignedMetadata(RepositorySigner.Envelope(payload, RepositorySigner.TimestampType, [signer.IndexKey]));
                break;
        }

        CliHarness.Expect(5, h.RunWith(env, "update"));
        Assert.Equal(before, Snapshot(h));
        CliHarness.Expect(3, h.Run("info", "evil", "--offline"));
    }

    [TestHooksFact]
    public void OversizedMetadata_AboveTrustMaxMetadataBytes_IsRejected()
    {
        using var h = new CliHarness().Ready();
        h.Repository.IndexVersion = 2;
        h.Repository.Publish();
        // Large enough for timestamp.json, smaller than the index.json it declares.
        var limit = (new FileInfo(h.Repository.TimestampPath).Length + new FileInfo(h.Repository.IndexPath).Length) / 2;
        Assert.True(new FileInfo(h.Repository.IndexPath).Length > limit);
        CliHarness.Expect(0, h.Run("config", "set", "trust.maxMetadataBytes", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var run = CliHarness.Expect(5, h.Run("update"));
        Assert.Contains("trust.maxMetadataBytes", run.StdErr, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(Repositories(h)).GetProperty("indexVersion").GetInt32());
    }

    [TestHooksFact]
    public void ManifestDigestMismatch_Exits5_AndInstallsNothing()
    {
        using var h = new CliHarness().Ready();
        var manifest = h.Repository.ManifestPath("hello", "1.0.0");
        var text = File.ReadAllText(manifest).Replace("hello fixture", "hellO fixture", StringComparison.Ordinal);
        File.WriteAllText(manifest, text); // same size, different digest
        var run = CliHarness.Expect(5, h.Run("install", "hello@1.0.0", "--yes"));
        Assert.Contains("Manifest digest mismatch", run.StdErr, StringComparison.Ordinal);
        h.AssertConsistent([null]);
    }

    [TestHooksFact]
    public void RepoAdd_ExpiredTimestamp_IsRefused_AndNeverValidMetadataIsNotUsedOffline()
    {
        using var h = new CliHarness();
        h.Repository.TimestampExpires = DateTimeOffset.UtcNow.AddMinutes(-30);
        h.Repository.Publish();
        h.AllowLocalhost();
        var run = CliHarness.Expect(5, h.AddRepo("local"));
        Assert.Contains("timestamp.json expired", run.StdErr, StringComparison.Ordinal);
        Assert.Empty(Repositories(h));
        CliHarness.Expect(3, h.Run("install", "hello@1.0.0", "--yes", "--offline"));
        CliHarness.Expect(3, h.Run("search", "hello"));
    }

    // ---- freshness (A8 / A15) -------------------------------------------------------------------

    [TestHooksFact]
    public void ExpiredMetadata_ReadOnlyWarns_DryRunRefuses_OfflineWarnsWithAge_OnlineRefreshes()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(0, h.Run("install", "hello@1.0.0", "--yes"));
        CliHarness.Expect(0, h.Run("uninstall", "hello", "--yes"));
        var later = CliHarness.At(DateTimeOffset.UtcNow.AddDays(9));

        var search = CliHarness.Expect(0, h.RunWith(later, "search", "hello", "--json")).Json();
        Assert.Contains(search.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("expired 3 days ago", StringComparison.Ordinal));
        CliHarness.Expect(5, h.RunWith(later, "install", "hello@1.0.0", "--dry-run"));

        var requests = h.Server.Requests.Count;
        var offline = CliHarness.Expect(0, h.RunWith(later, "install", "hello@1.0.0", "--yes", "--offline", "--json")).Json();
        Assert.Contains(offline.GetProperty("warnings").EnumerateArray(), w =>
            w.GetString()!.Contains("--offline: using repository 'local' metadata that expired 3 days ago", StringComparison.Ordinal));
        Assert.Equal(requests, h.Server.Requests.Count);
        CliHarness.Expect(0, h.Run("uninstall", "hello", "--yes"));

        // Online: the publisher refreshed its metadata; install refreshes automatically and says so.
        var shifted = DateTimeOffset.UtcNow.AddDays(9);
        h.Repository.IndexVersion = 2;
        h.Repository.TimestampExpires = shifted.AddDays(5);
        h.Repository.Expires = shifted.AddDays(20);
        h.Repository.Publish();
        var online = CliHarness.Expect(0, h.RunWith(later, "install", "hello@1.0.0", "--yes", "--json")).Json();
        Assert.Contains(online.GetProperty("warnings").EnumerateArray(), w =>
            w.GetString()!.StartsWith("Repository metadata refreshed: 'local' timestamp expired 3 days ago", StringComparison.Ordinal));
        Assert.Equal(2, Assert.Single(Repositories(h)).GetProperty("indexVersion").GetInt32());
    }

    // ---- search + info ---------------------------------------------------------------------------

    [TestHooksFact]
    public void Search_IsCaseInsensitivePrefixOrSubstring_OrderedById_WithLiteralMetacharacters()
    {
        using var h = new CliHarness(r =>
        {
            r.AddHello();
            r.Add(new FixturePackage("zeta", "1.0.0", [("bin/z.exe", PeBytes.Create())], [("zeta", "bin/z.exe")]));
            r.Add(new FixturePackage("alpha", "1.0.0", [("bin/a.exe", PeBytes.Create())], [("alpha", "bin/a.exe")]));
        });
        h.Ready();

        Assert.Equal(["local/alpha", "local/hello", "local/zeta"], SearchIds(h, "FIXTURE"));
        Assert.Equal(["local/hello"], SearchIds(h, "hel"));
        Assert.Equal(["local/hello"], SearchIds(h, "LL"));   // short query, substring
        Assert.Equal(["local/zeta"], SearchIds(h, "eta fix")); // spans id and description
        Assert.Empty(SearchIds(h, "*"));
        Assert.Empty(SearchIds(h, "h?llo"));
        CliHarness.Expect(2, h.Run("search", "  "));
    }

    [TestHooksFact]
    public void Info_ShowsTheWritesToInstallDirWarning_FromTheSignedManifest_WithoutInstalling()
    {
        using var h = new CliHarness(r => r.Add(new FixturePackage("stateful", "1.0.0", [("bin/s.exe", PeBytes.Create())],
            [("stateful", "bin/s.exe")], WritesToInstallDir: true)));
        h.Ready();
        var info = CliHarness.Expect(0, h.Run("info", "stateful", "--json")).Json();
        Assert.True(info.GetProperty("data").GetProperty("writesToInstallDir").GetBoolean());
        Assert.Contains(info.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("install directory", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(h.Root, "cache", "metadata", "local", "stateful")), "info never writes the cache");
    }

    /// <summary>
    /// A17: search on a GENERATED 10,000-package catalog, median of 10 runs. The
    /// target (&lt; 1 s) is defined for the CI runner; this records the dev-host value.
    /// </summary>
    [TestHooksFact]
    public void Search_On10000PackageCatalog_MedianOf10Runs_IsUnderOneSecond()
    {
        using var h = new CliHarness(r => r.AddHello());
        var index = new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = 1,
            ["expires"] = RepositorySigner.Iso(DateTimeOffset.UtcNow.AddDays(20)),
            ["packages"] = new System.Text.Json.Nodes.JsonObject(),
        };
        var packages = (System.Text.Json.Nodes.JsonObject)index["packages"]!;
        for (var i = 0; i < 10_000; i++)
        {
            packages[$"fixture-{i:D5}"] = new System.Text.Json.Nodes.JsonObject
            {
                ["displayName"] = $"Fixture {i}",
                ["description"] = $"Generated fixture package number {i}",
                ["versions"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["1.0.0"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["sha256"] = new string('0', 64), ["size"] = 1,
                        ["architectures"] = new System.Text.Json.Nodes.JsonArray("x64"),
                        ["dependencies"] = new System.Text.Json.Nodes.JsonArray(),
                    },
                },
            };
        }

        h.Repository.WriteSignedMetadata(h.Repository.Signer.SignIndex(JsonSerializer.SerializeToUtf8Bytes(index)));
        h.Ready();
        Assert.Equal(10_000, Assert.Single(Repositories(h)).GetProperty("packages").GetInt32());

        var times = new List<double>();
        for (var run = 0; run < 10; run++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = CliHarness.Expect(0, h.Run("search", "number 99", "--json"));
            watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal(111, result.Json().GetProperty("data").GetProperty("results").GetArrayLength()); // 99, 990–999, 9900–9999
        }

        times.Sort();
        var median = (times[4] + times[5]) / 2;
        Console.WriteLine($"[A17] search on 10,000 packages: median {median:0} ms over 10 runs (min {times[0]:0}, max {times[^1]:0}).");
        Assert.True(median < 1000, $"median {median:0} ms");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static byte[] Publish(FixtureRepository r)
    {
        r.Publish();
        return File.ReadAllBytes(r.TimestampPath);
    }

    /// <summary>A validly signed timestamp v1 (lower than the accepted v2) binding the current index.</summary>
    private static byte[] OlderTimestamp(RepositorySigner signer, FixtureRepository r) =>
        signer.SignTimestamp(1, DateTimeOffset.UtcNow.AddDays(6), File.ReadAllBytes(r.IndexPath));

    private static List<JsonElement> Repositories(CliHarness h) =>
        CliHarness.Expect(0, h.Run("repo", "list", "--json")).Json().GetProperty("data").GetProperty("repositories").EnumerateArray().ToList();

    private static string Snapshot(CliHarness h)
    {
        var repo = Assert.Single(Repositories(h));
        var search = CliHarness.Expect(0, h.Run("search", "fixture", "--json")).Json().GetProperty("data").GetProperty("results");
        return $"{repo.GetProperty("rootVersion")}/{repo.GetProperty("timestampVersion")}/{repo.GetProperty("indexVersion")}/{repo.GetProperty("packages")}|{search}";
    }

    private static List<string> SearchIds(CliHarness h, string query) =>
        CliHarness.Expect(0, h.Run("search", query, "--json")).Json().GetProperty("data").GetProperty("results")
            .EnumerateArray().Select(e => e.GetProperty("package").GetString()!).ToList();
}
