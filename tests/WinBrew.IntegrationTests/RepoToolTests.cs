using System.Security.Cryptography;
using System.Text.Json;
using WinBrew.RepoTool;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>
/// deploy/server's winbrew-repo publishes what the REAL client accepts: signed
/// root/timestamp/index, manifests and artifacts, dependency resolution,
/// upgrade, and a re-sign (new index version, same keys, byte-identical
/// artifacts) that `wbrew update` takes.
/// </summary>
public sealed class RepoToolTests
{
    private static void Package(string root, string id, string version, string[]? dependsOn = null)
    {
        var dir = Path.Combine(root, id, version);
        Directory.CreateDirectory(Path.Combine(dir, "files", "bin"));
        File.WriteAllBytes(Path.Combine(dir, "files", "bin", id + ".exe"), PeBytes.Create());
        File.WriteAllText(Path.Combine(dir, "files", "README.txt"), $"{id} {version}");
        var spec = new Dictionary<string, object>
        {
            ["displayName"] = id,
            ["description"] = $"{id} version {version}",
            ["homepage"] = "https://example.org/",
            ["license"] = "MIT",
            ["executables"] = new[] { new { name = id, target = $"bin/{id}.exe" } },
            ["dependencies"] = (dependsOn ?? []).Select(d => new { id = d, range = "[1.0.0,2.0.0)" }).ToArray(),
        };
        File.WriteAllText(Path.Combine(dir, "package.json"), JsonSerializer.Serialize(spec));
    }

    [TestHooksFact]
    public void PublishedRepository_IsAccepted_ByTheClient_AcrossInstallUpgradeAndResign()
    {
        using var h = new CliHarness(_ => { });
        var specs = Path.Combine(h.Work, "specs");
        Package(specs, "lib", "1.0.0");
        Package(specs, "tool", "1.0.0", ["lib"]);
        Package(specs, "tool", "1.10.0", ["lib"]);
        Package(specs, "tool", "1.9.0", ["lib"]);
        var options = new BuildOptions(specs, Path.Combine(h.Work, "keys"), h.Repository.Directory, h.Server.BaseUrl, null);

        var first = RepositoryBuilder.Build(options, DateTimeOffset.UtcNow);
        var fingerprint = Assert.Single(first.RootFingerprints);

        h.AllowLocalhost();
        CliHarness.Expect(0, h.Run("repo", "add", CliHarness.RepoName, h.Server.BaseUrl.ToString(), "--trust-root", fingerprint));
        h.SetTemplate("TEMPLATE-1");

        // Newest version's text is the catalog entry; 1.10.0 > 1.9.0.
        var search = CliHarness.Expect(0, h.Run("search", "tool", "--json")).Json().GetProperty("data").GetProperty("results");
        Assert.Contains("1.10.0", search.EnumerateArray().Single().ToString(), StringComparison.Ordinal);

        CliHarness.Expect(0, h.Run("install", "tool@1.9.0", "--yes"));
        var list = CliHarness.Expect(0, h.Run("list", "--json")).Json().GetProperty("data").GetProperty("packages");
        Assert.Equal(2, list.GetArrayLength()); // tool + its dependency lib

        // Re-sign later: same keys and root, newer index, identical artifacts.
        var artifact = Path.Combine(h.Repository.Directory, "artifacts", "tool-1.10.0-x64.zip");
        var before = SHA256.HashData(File.ReadAllBytes(artifact));
        var second = RepositoryBuilder.Build(options, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.True(second.IndexVersion > first.IndexVersion);
        Assert.Equal(first.RootFingerprints, second.RootFingerprints);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(artifact)));

        CliHarness.Expect(0, h.Run("update"));
        CliHarness.Expect(0, h.Run("upgrade", "--yes"));
        Assert.Contains("1.10.0", CliHarness.Expect(0, h.Run("list")).StdOut, StringComparison.Ordinal);
    }
}
