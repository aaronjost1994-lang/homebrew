using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>
/// A17 dev-host measurements through the real CLI process (framework-dependent
/// test host). The targets are defined for the published binary on the CI
/// runners (ReleaseBinaryTests); these numbers are reported, never substituted.
/// </summary>
public sealed class MeasurementTests
{
    [TestHooksFact]
    public void List_With200InstalledPackages_MedianOf10Runs()
    {
        using var h = new CliHarness().Ready();
        CliHarness.Expect(0, h.Run("recover")); // ensures the schema exists
        using (var c = new SqliteConnection($"Data Source={Path.Combine(h.Root, "state", "winbrew.db")}"))
        {
            c.Open();
            using var tx = c.BeginTransaction();
            for (var i = 0; i < 200; i++)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO installed_packages(repo, package_id, version, arch, install_type, explicit, installed_at, updated_at)
                    VALUES ('local', $id, '1.0.0', 'x64', 'portable', 1, '2026-11-06T00:00:00Z', '2026-11-06T00:00:00Z');
                    INSERT INTO shims(name, owner_repo, owner_pkg, owner_version, target_path, subsystem, template_sha256, created_at, updated_at)
                    VALUES ($id, 'local', $id, '1.0.0', 'C:\x.exe', 'console', 'x', '2026-11-06T00:00:00Z', '2026-11-06T00:00:00Z');
                    """;
                cmd.Parameters.AddWithValue("$id", $"pkg-{i:D3}");
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        SqliteConnection.ClearAllPools();
        var times = new List<double>();
        for (var run = 0; run < 10; run++)
        {
            var watch = Stopwatch.StartNew();
            var result = CliHarness.Expect(0, h.Run("list", "--json"));
            watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal(200, result.Json().GetProperty("data").GetProperty("packages").GetArrayLength());
        }

        times.Sort();
        var median = (times[4] + times[5]) / 2;
        Console.WriteLine($"[A17] list with 200 packages (dev host, test host process): median {median:0} ms (min {times[0]:0}, max {times[^1]:0}).");
        Assert.True(median < 2000, $"median {median:0} ms — a gross regression (the A17 target is measured on CI)");
    }
}
