using System.Text.Json.Nodes;
using WinBrew.Application.Ports;
using WinBrew.Infrastructure.Sqlite;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>
/// v1.1 installer wiring through the real CLI (A8/A12/A14): consent, the
/// "installed on its own" rule, list/self-uninstall with installer-managed
/// packages. Running a real MSI/EXE needs a Windows runner (InstallerWindowsTests).
/// </summary>
public sealed class InstallerCliTests
{
    public const string ProductCode = "{6F1C2D3E-4A5B-4C6D-8E7F-90A1B2C3D4E5}";

    public static JsonObject MsiDeclaration() => new()
    {
        ["type"] = "msi",
        ["msi"] = new JsonObject
        {
            ["productCode"] = ProductCode,
            ["userProperties"] = new JsonObject { ["ALLUSERS"] = "2", ["MSIINSTALLPERUSER"] = "1" },
        },
    };

    internal static void MsiRepository(FixtureRepository r)
    {
        r.Add(new FixturePackage("tool-msi", "1.0.0", [], [], ArchiveOverride: "not a real msi"u8.ToArray(),
            InstallType: "msi", Installer: MsiDeclaration(), Scope: ["user", "machine"]));
        r.Add(new FixturePackage("app", "1.0.0", [("app.exe", PeBytes.Create())], [("app", "app.exe")], Dependencies: [("tool-msi", "[1.0.0,)")]));
    }

    [TestHooksFact]
    public void InstallingAnMsi_WithoutAllowInstaller_Exits10_EvenWithYes_AndRecordsNothing()
    {
        using var h = new CliHarness(MsiRepository).Ready();
        var dry = CliHarness.Expect(0, h.Run("install", "tool-msi", "--dry-run", "--json")).Json();
        Assert.Equal("install", CliHarness.PlanItem(dry, "tool-msi").GetProperty("action").GetString());

        var run = CliHarness.Expect(10, h.Run("install", "tool-msi", "--yes"));
        Assert.Contains("--allow-installer", run.StdErr, StringComparison.Ordinal);
        Assert.Empty(Installers(h));
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Root, "cache", "artifacts"))); // consent is checked before any download
    }

    [TestHooksFact]
    public void ScopeMachine_IsRefusedForAUserOnlyMsi_AndAnInstallerCannotShareAPlan()
    {
        using var h = new CliHarness(r =>
        {
            MsiRepository(r);
            var userOnly = MsiDeclaration();
            r.Add(new FixturePackage("user-msi", "1.0.0", [], [], ArchiveOverride: "x"u8.ToArray(), InstallType: "msi", Installer: userOnly));
        }).Ready();

        CliHarness.Expect(6, h.Run("install", "user-msi", "--yes", "--allow-installer", "--scope", "machine"));
        var mixed = CliHarness.Expect(6, h.Run("install", "app", "--yes", "--allow-installer"));
        Assert.Contains("must be installed on its own", mixed.StdErr, StringComparison.Ordinal);
        CliHarness.Expect(2, h.Run("install", "tool-msi", "--scope", "everywhere"));
    }

    [TestHooksFact]
    public void AllowedMsi_OnANonWindowsHost_FailsClearly_WithExit1()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // on Windows the real runner runs msiexec; InstallerWindowsTests covers that
        }

        using var h = new CliHarness(MsiRepository).Ready();
        var run = CliHarness.Expect(1, h.Run("install", "tool-msi", "--yes", "--allow-installer"));
        Assert.Contains("need Windows", run.StdErr, StringComparison.Ordinal);
        Assert.Empty(Installers(h));
    }

    [TestHooksFact]
    public async Task InstallerManagedPackages_AreListed_BlockSelfUninstall_AndNeedConsentToUninstall()
    {
        using var h = new CliHarness(MsiRepository).Ready();
        var store = new SqliteStateStore(Path.Combine(h.Root, "state", "winbrew.db"), TimeProvider.System);
        await store.UpsertInstallerPackageAsync(new InstallerPackage("local", "tool-msi", "1.0.0", "x64", "msi", "machine", ProductCode, null,
            "Installed", null, """{"type":"msi","msi":{"productCode":"{6F1C2D3E-4A5B-4C6D-8E7F-90A1B2C3D4E5}"}}""",
            "2026-11-06T00:00:00Z", "2026-11-06T00:00:00Z"), CancellationToken.None);

        var row = Assert.Single(Installers(h));
        Assert.Equal(("local/tool-msi", "msi", "machine", ProductCode), (row["package"]!.GetValue<string>(), row["type"]!.GetValue<string>(),
            row["scope"]!.GetValue<string>(), row["productCode"]!.GetValue<string>()));

        var refused = CliHarness.Expect(6, h.Run("self", "uninstall", "--yes"));
        Assert.Contains("Installer-managed packages are still installed: local/tool-msi", refused.StdErr, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(h.Root, "state", "winbrew.db")), "nothing was removed");

        CliHarness.Expect(10, h.Run("uninstall", "tool-msi", "--yes"));
        CliHarness.Expect(0, h.Run("uninstall", "tool-msi", "--dry-run"));
        Assert.Single(Installers(h));
    }

    private static List<JsonNode> Installers(CliHarness h) =>
        JsonNode.Parse(CliHarness.Expect(0, h.Run("list", "--json")).StdOut)!["data"]!["installers"]!.AsArray().Select(n => n!).ToList();
}
