using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using WinBrew.Core;
using WinBrew.TestFixtures;
using WinBrew.Windows.Installers;

namespace WinBrew.IntegrationTests;

/// <summary>
/// Phase E acceptance that needs a DISPOSABLE Windows runner (A16): the helper's
/// directory rules against real NTFS objects, and real MSI/EXE installs.
/// Skipped — with the reason — everywhere else. WINBREW_DISPOSABLE_RUNNER=1
/// enables the directory tests (they create and delete %ProgramData% entries);
/// WINBREW_INSTALLER_FIXTURES points at scripts/build-installer-fixtures.ps1 output.
/// </summary>
public sealed class InstallerWindowsTests
{
    private const string Runner = "WINBREW_DISPOSABLE_RUNNER";
    private const string Fixtures = "WINBREW_INSTALLER_FIXTURES";

    [SupportedOSPlatform("windows")]
    private static string ScratchUnderProgramData() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinBrewTest-" + Guid.NewGuid().ToString("N")[..8]);

    [SupportedOSPlatform("windows")]
    private static string Sddl(string path) =>
        new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.All);

    [SupportedOSPlatform("windows")]
    private static void AssertRefusedAndUntouched(string path, string because)
    {
        var before = Sddl(path);
        var ex = Assert.Throws<WinBrewException>(() => ProtectedDirectories.EnsureTrustedOrCreate(path));
        Assert.Contains("never adopts or repairs", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, Sddl(path)); // no ACL changed outside directories the helper created
        _ = because;
    }

    [WindowsArtifactFact(Runner, "a disposable runner (creates %ProgramData% entries)")]
    [SupportedOSPlatform("windows")]
    public void PreCreatedPlainDirectory_OwnedByTheUser_IsRefused()
    {
        var path = ScratchUnderProgramData();
        var security = new DirectorySecurity();
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        new DirectoryInfo(path).Create(security);
        try
        {
            AssertRefusedAndUntouched(path, "owner is a standard user");
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [WindowsArtifactFact(Runner, "a disposable runner (creates %ProgramData% entries)")]
    [SupportedOSPlatform("windows")]
    public void PreCreatedJunctionOrLink_IsRefused_WhereverItAppears()
    {
        var target = Directory.CreateTempSubdirectory("wbrew-target").FullName;
        var link = ScratchUnderProgramData();
        Directory.CreateSymbolicLink(link, target);
        try
        {
            Assert.Throws<WinBrewException>(() => ProtectedDirectories.EnsureTrustedOrCreate(link));
            Assert.Throws<WinBrewException>(() => ProtectedDirectories.EnsureTrustedOrCreate(Path.Combine(link, "elevated"))); // as a parent component
            Assert.False(Directory.Exists(Path.Combine(target, "elevated")), "nothing was created through the link");
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target, recursive: true);
        }
    }

    [WindowsArtifactFact(Runner, "a disposable runner (creates %ProgramData% entries)")]
    [SupportedOSPlatform("windows")]
    public void PreCreatedDirectoryContainingAHardLink_IsRefused_AndTheLinkTargetsAclIsUnchanged()
    {
        var path = ScratchUnderProgramData();
        var security = new DirectorySecurity();
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        new DirectoryInfo(path).Create(security);
        var outside = Path.Combine(Path.GetTempPath(), "wbrew-hardlink-target-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(outside, "victim");
        var victimBefore = new FileInfo(outside).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        Assert.True(CreateHardLink(Path.Combine(path, "link.txt"), outside, IntPtr.Zero));
        try
        {
            AssertRefusedAndUntouched(path, "pre-created, with a hard link inside");
            Assert.Equal(victimBefore, new FileInfo(outside).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
            File.Delete(outside);
        }
    }

    [WindowsArtifactFact(Runner, "a disposable runner running elevated")]
    [SupportedOSPlatform("windows")]
    public void AdministratorsOwnedDirectory_GrantingUsersModify_IsRefused_TheDaclCheck()
    {
        var path = ScratchUnderProgramData();
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        try
        {
            AssertRefusedAndUntouched(path, "Users:Modify");
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [WindowsArtifactFact(Runner, "a disposable runner running elevated")]
    [SupportedOSPlatform("windows")]
    public void CreatedByTheHelper_GetsTheProtectedDescriptor_AndAnExistingOneIsRecheckedNotAdopted()
    {
        var path = ScratchUnderProgramData();
        try
        {
            Assert.True(ProtectedDirectories.EnsureTrustedOrCreate(path));            // created with its final descriptor
            var security = new DirectoryInfo(path).GetAccessControl();
            Assert.Equal("S-1-5-32-544", security.GetOwner(typeof(SecurityIdentifier))!.Value);
            Assert.True(security.AreAccessRulesProtected);
            Assert.False(ProtectedDirectories.EnsureTrustedOrCreate(path));           // ERROR_ALREADY_EXISTS path: re-checked, trusted, not re-ACLed
            var op = ProtectedDirectories.CreateOperationDirectory(path);
            Assert.StartsWith(path, op, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    // ---- real installers (fixtures built in CI) --------------------------------------------------

    [WindowsArtifactFact(Fixtures, "the CI-built fixture MSI and EXE")]
    [SupportedOSPlatform("windows")]
    public void FixtureMsi_PerUser_InstallsListsAndUninstalls()
    {
        using var h = new CliHarness(r => AddFixture(r, "winbrew-fixture.msi", "fixture-msi", "msi", InstallerCliTests.MsiDeclaration())).Ready();
        CliHarness.Expect(0, h.Run("install", "fixture-msi", "--yes", "--allow-installer"));
        var row = JsonNode.Parse(CliHarness.Expect(0, h.Run("list", "--json")).StdOut)!["data"]!["installers"]![0]!;
        Assert.Equal("user", row["scope"]!.GetValue<string>());
        Assert.Null(row["drift"]);
        CliHarness.Expect(0, h.Run("uninstall", "fixture-msi", "--yes", "--allow-installer"));
    }

    [WindowsArtifactFact(Fixtures, "the CI-built fixture MSI (per-machine, elevated runner)")]
    [SupportedOSPlatform("windows")]
    public void FixtureMsi_PerMachine_GoesThroughTheElevatedHelper()
    {
        using var h = new CliHarness(r => AddFixture(r, "winbrew-fixture.msi", "fixture-msi", "msi", InstallerCliTests.MsiDeclaration())).Ready();
        CliHarness.Expect(0, h.Run("install", "fixture-msi", "--yes", "--allow-installer", "--scope", "machine"));
        CliHarness.Expect(0, h.Run("uninstall", "fixture-msi", "--yes", "--allow-installer"));
    }

    [WindowsArtifactFact(Fixtures, "the CI-built fixture EXE")]
    [SupportedOSPlatform("windows")]
    public void FixtureExe_PerUser_InstallsAndUninstalls_ByItsDetectionRules()
    {
        var exe = new JsonObject
        {
            ["type"] = "exe",
            ["exe"] = new JsonObject
            {
                ["silentInstallArgs"] = new JsonArray("/S", "/install"),
                ["silentUninstall"] = new JsonObject
                {
                    ["path"] = "{LocalAppData}/WinBrewFixtureExe/winbrew-fixture-setup.exe",
                    ["args"] = new JsonArray("/S", "/uninstall"),
                },
                ["detection"] = new JsonObject { ["uninstallKey"] = "WinBrewFixtureExe" },
            },
        };
        using var h = new CliHarness(r => AddFixture(r, "winbrew-fixture-setup.exe", "fixture-exe", "exe", exe)).Ready();
        CliHarness.Expect(0, h.Run("install", "fixture-exe", "--yes", "--allow-installer"));
        CliHarness.Expect(0, h.Run("uninstall", "fixture-exe", "--yes", "--allow-installer"));
    }

    private static void AddFixture(FixtureRepository r, string file, string id, string type, JsonObject installer)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Environment.GetEnvironmentVariable(Fixtures)!, file));
        r.Add(new FixturePackage(id, "1.0.0", [], [], ArchiveOverride: bytes, InstallType: type, Installer: installer, Scope: ["user", "machine"]));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
