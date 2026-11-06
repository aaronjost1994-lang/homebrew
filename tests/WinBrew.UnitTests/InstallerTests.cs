using System.Security.Cryptography;
using System.Text.RegularExpressions;
using WinBrew.Application.Installers;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Installers;
using WinBrew.Core.Manifests;
using WinBrew.Infrastructure.Files;
using WinBrew.TestFixtures;

namespace WinBrew.UnitTests;

/// <summary>A12 rules that need no Windows: msiexec exit codes and arguments, the 1618 retry, the helper request.</summary>
public sealed class MsiRulesTests
{
    [Theory]
    [InlineData(0, InstallerOutcome.Success, 0)]
    [InlineData(3010, InstallerOutcome.RebootRequired, 9)]
    [InlineData(1641, InstallerOutcome.RebootStarted, 9)]
    [InlineData(1602, InstallerOutcome.Cancelled, 130)]
    [InlineData(1618, InstallerOutcome.Busy, 13)]
    [InlineData(1603, InstallerOutcome.Failed, 1)]
    [InlineData(1619, InstallerOutcome.Failed, 1)]
    [InlineData(-1, InstallerOutcome.Failed, 1)]
    public void EveryDocumentedMsiexecExitCode_MapsToItsD9Code(int msiexec, InstallerOutcome outcome, int exit)
    {
        Assert.Equal(outcome, MsiRules.Map(msiexec));
        Assert.Equal(exit, (int)MsiRules.ToErrorCode(outcome));
    }

    [Fact]
    public void ExeExitCodes_1618IsJustAFailure()
    {
        Assert.Equal(InstallerOutcome.Success, MsiRules.MapExe(0));
        Assert.Equal(InstallerOutcome.RebootRequired, MsiRules.MapExe(3010));
        Assert.Equal(InstallerOutcome.Failed, MsiRules.MapExe(1618));
    }

    [Fact]
    public void InstallArguments_AreAListWithOnlyTheDeclaredProperties_InOrdinalOrder()
    {
        var msi = Path.Combine(Path.GetTempPath(), "p.msi");
        var log = Path.Combine(Path.GetTempPath(), "p.log");
        Assert.Equal(["/i", msi, "/qn", "/norestart", "/l*v", log, "A_PROP=1", "ZED=x"],
            MsiRules.InstallArguments(msi, log, new Dictionary<string, string> { ["ZED"] = "x", ["A_PROP"] = "1" }));
        Assert.Equal(["/x", "{6F1C2D3E-4A5B-4C6D-8E7F-90A1B2C3D4E5}", "/qn", "/norestart", "/l*v", log],
            MsiRules.UninstallArguments("{6F1C2D3E-4A5B-4C6D-8E7F-90A1B2C3D4E5}", log));

        Assert.Throws<WinBrewException>(() => MsiRules.InstallArguments(msi, log, new Dictionary<string, string> { ["X"] = "a b" }));
        Assert.Throws<WinBrewException>(() => MsiRules.InstallArguments(msi, log, new Dictionary<string, string> { ["X"] = "\"; calc" }));
        Assert.Throws<WinBrewException>(() => MsiRules.InstallArguments("relative.msi", log, new Dictionary<string, string>()));
        Assert.Throws<WinBrewException>(() => MsiRules.UninstallArguments("not-a-guid", log));
    }

    [Fact]
    public void PerUserPropertySet_IsAddedOnlyForUserScope()
    {
        var msi = new MsiInstaller("{6F1C2D3E-4A5B-4C6D-8E7F-90A1B2C3D4E5}", Properties: new Dictionary<string, string> { ["FEATURE"] = "1" },
            UserProperties: new Dictionary<string, string> { ["ALLUSERS"] = "2", ["MSIINSTALLPERUSER"] = "1" });
        Assert.Equal(3, MsiRules.PropertiesFor(msi, "user").Count);
        Assert.Equal(["FEATURE"], MsiRules.PropertiesFor(msi, "machine").Keys);
    }

    [Fact]
    public async Task Busy1618_IsRetriedWithBackoff_ThenReturned()
    {
        var delays = new List<TimeSpan>();
        var calls = 0;
        var code = await MsiRules.RunWithBusyRetriesAsync(_ => { calls++; return Task.FromResult(1618); },
            (d, _) => { delays.Add(d); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(1618, code);
        Assert.Equal(MsiRules.BusyRetries + 1, calls);
        Assert.Equal([5, 10, 20, 40], delays.Select(d => (int)d.TotalSeconds));

        var sequence = new Queue<int>([1618, 1618, 0]);
        Assert.Equal(0, await MsiRules.RunWithBusyRetriesAsync(_ => Task.FromResult(sequence.Dequeue()), (_, _) => Task.CompletedTask, CancellationToken.None));
    }

    // ---- elevated request (untrusted input) -----------------------------------------------------

    private static readonly string Artifact = Path.Combine(Path.GetTempPath(), "x.msi");
    private static readonly string Sha = new('a', 64);

    private static byte[] Json(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public void ElevatedRequest_RoundTrips_AndOnlyTypedOperationsAreAccepted()
    {
        var request = new ElevatedRequest("1", ElevatedProtocol.MsiInstall, Artifact, Sha, MsiProperties: new Dictionary<string, string> { ["FEATURE"] = "1" });
        Assert.Equal(request.ArtifactSha256, ElevatedProtocol.Parse(ElevatedProtocol.Serialize(request)).ArtifactSha256);

        Assert.Throws<WinBrewException>(() => ElevatedProtocol.Parse(Json("""{"schemaVersion":"1","operation":"run","commandLine":"cmd /c calc"}""")));
        Assert.Throws<WinBrewException>(() => ElevatedProtocol.Parse(Json(
            """{"schemaVersion":"1","operation":"msi-uninstall","productCode":"{6F1C2D3E-4A5B-4C6D-8E7F-90A1B2C3D4E5}","commandLine":"x"}""")));
        Assert.Throws<WinBrewException>(() => ElevatedProtocol.Parse(ElevatedProtocol.Serialize(request with { ArtifactPath = "x.msi" })));
        Assert.Throws<WinBrewException>(() => ElevatedProtocol.Parse(ElevatedProtocol.Serialize(request with { ArtifactPath = Path.ChangeExtension(Artifact, ".exe") })));
        Assert.Throws<WinBrewException>(() => ElevatedProtocol.Parse(ElevatedProtocol.Serialize(request with { ProductCode = "{6F1C2D3E-4A5B-4C6D-8E7F-90A1B2C3D4E5}" })));
        Assert.Throws<WinBrewException>(() => ElevatedProtocol.Parse(ElevatedProtocol.Serialize(request with { MsiProperties = new Dictionary<string, string> { ["x"] = "1" } })));
        Assert.Throws<WinBrewException>(() => ElevatedProtocol.Parse(new byte[ElevatedProtocol.MaxRequestBytes + 1]));
    }

    [Fact]
    public void ElevatedExeUninstall_MustNameAnExeUnderAMachineRoot()
    {
        ElevatedRequest Uninstall(string path) => new("1", ElevatedProtocol.ExeUninstall, ExeUninstall: new ExeCommand(path, ["/S"]));
        ElevatedProtocol.Parse(ElevatedProtocol.Serialize(Uninstall("{ProgramFiles}/Vendor/unins000.exe")));
        Assert.Throws<WinBrewException>(() => ElevatedProtocol.Parse(ElevatedProtocol.Serialize(Uninstall("{LocalAppData}/Vendor/unins000.exe"))));
        Assert.Throws<WinBrewException>(() => ElevatedProtocol.Parse(ElevatedProtocol.Serialize(Uninstall("{ProgramFiles}/../Windows/System32/cmd.exe"))));
        Assert.Throws<WinBrewException>(() => ElevatedProtocol.Parse(ElevatedProtocol.Serialize(Uninstall("C:/Windows/System32/cmd.exe"))));
        Assert.Matches(ElevatedProtocol.RequestFileNameRegex(), $"request-{Guid.NewGuid():N}.json");
        Assert.DoesNotMatch(ElevatedProtocol.RequestFileNameRegex(), "request-../../x.json");
    }

    public static TheoryData<ElevatedResult> Results() => new()
    {
        new ElevatedResult("1", 0, "C:\\log.txt", "{6F1C2D3E-4A5B-4C6D-8E7F-90A1B2C3D4E5}", null), // MSI success: no error
        new ElevatedResult("1", 0, "C:\\log.txt", null, null),                                   // EXE success: no product code either
        new ElevatedResult("1", null, null, null, "refused: never adopts or repairs"),         // refusal: only the error
    };

    [Theory]
    [MemberData(nameof(Results))]
    public void ElevatedResult_RoundTrips_WithNullMembersOmitted(ElevatedResult result)
    {
        // The helper omits null members on write; the CLI must still read them.
        Assert.Equal(result, ElevatedProtocol.ParseResult(ElevatedProtocol.Serialize(result)));
    }
}

/// <summary>A12: owner + DACL rules for pre-existing %ProgramData%\WinBrew directories.</summary>
public sealed class DirectoryTrustRulesTests
{
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string CreatorOwner = "S-1-3-0";
    private const string SomeUser = "S-1-5-21-1-2-3-1001";
    private const int FullControl = 0x001F01FF;
    private const int Modify = 0x001301BF;
    private const int ReadExecute = 0x001200A9;

    private static AceSnapshot Allow(string sid, int mask, bool inherited = false, bool inheritOnly = false) => new(sid, true, mask, inherited, inheritOnly);

    private static DirectorySecuritySnapshot Protected(params AceSnapshot[] extra) => new(DirectoryTrustRules.Administrators,
        [Allow(DirectoryTrustRules.Administrators, FullControl), Allow(DirectoryTrustRules.LocalSystem, FullControl), .. extra], IsReparsePoint: false);

    [Fact]
    public void AdministratorsOwned_WithReadOnlyUsers_IsTrusted() =>
        Assert.Null(DirectoryTrustRules.Evaluate(Protected(Allow(Users, ReadExecute))));

    [Fact]
    public void SystemOwner_IsTrustedToo() =>
        Assert.Null(DirectoryTrustRules.Evaluate(Protected() with { OwnerSid = DirectoryTrustRules.LocalSystem }));

    [Fact]
    public void PreCreatedByAStandardUser_IsRefused_BecauseOfTheOwner() =>
        Assert.Contains(DirectoryTrustRules.Evaluate(Protected() with { OwnerSid = SomeUser })!, p => p.Contains("owner", StringComparison.Ordinal));

    [Fact]
    public void AdministratorsOwned_GrantingUsersModify_IsRefused_TheDaclCheck() =>
        Assert.Contains(DirectoryTrustRules.Evaluate(Protected(Allow(Users, Modify)))!, p => p.StartsWith(Users, StringComparison.Ordinal));

    [Theory]
    [InlineData(DirectoryTrustRules.FileWriteData)]
    [InlineData(DirectoryTrustRules.FileAppendData)]
    [InlineData(DirectoryTrustRules.Delete)]
    [InlineData(DirectoryTrustRules.FileDeleteChild)]
    [InlineData(DirectoryTrustRules.WriteDac)]
    [InlineData(DirectoryTrustRules.WriteOwner)]
    [InlineData(DirectoryTrustRules.GenericWrite)]
    [InlineData(DirectoryTrustRules.GenericAll)]
    public void AnyWriteAppendDeleteOrOwnershipRight_ForAnotherPrincipal_IsRefused(int right) =>
        Assert.NotNull(DirectoryTrustRules.Evaluate(Protected(Allow(AuthenticatedUsers, right))));

    [Fact]
    public void InheritedAndInheritOnlyGrants_CountToo()
    {
        Assert.NotNull(DirectoryTrustRules.Evaluate(Protected(Allow(Users, Modify, inherited: true))));
        Assert.NotNull(DirectoryTrustRules.Evaluate(Protected(Allow(CreatorOwner, FullControl, inheritOnly: true))));
    }

    [Fact]
    public void ReparsePoint_OrNullDacl_IsRefused_AndDenyAcesNeverHelp()
    {
        Assert.NotNull(DirectoryTrustRules.Evaluate(Protected() with { IsReparsePoint = true }));
        Assert.NotNull(DirectoryTrustRules.Evaluate(Protected() with { DaclPresent = false }));
        Assert.Null(DirectoryTrustRules.Evaluate(Protected(new AceSnapshot(Users, false, Modify, false, false))));
        Assert.Contains("never adopts or repairs", DirectoryTrustRules.RefusalMessage("C:\\ProgramData\\WinBrew", ["x"]), StringComparison.Ordinal);
    }
}

/// <summary>v1.1 manifest rules for installer declarations (A9/A12).</summary>
public sealed class InstallerManifestTests
{
    private const string ProductCode = "{6F1C2D3E-4A5B-4C6D-8E7F-90A1B2C3D4E5}";

    public static Manifest Msi(string[]? scope = null, MsiInstaller? msi = null) => new()
    {
        Id = "fixture-msi",
        Version = "1.0.0",
        DisplayName = "Fixture MSI",
        Description = "fixture",
        Homepage = "https://example.org/",
        License = "MIT",
        Scope = scope ?? ["user", "machine"],
        Artifacts = [new ManifestArtifact("x64", "https://example.org/f.msi", new string('a', 64), 10, "msi", "msi")],
        Installer = new InstallerDeclaration("msi", Msi: msi ?? new MsiInstaller(ProductCode,
            UserProperties: new Dictionary<string, string> { ["ALLUSERS"] = "2", ["MSIINSTALLPERUSER"] = "1" })),
    };

    public static Manifest Exe(ExeInstaller? exe = null, string[]? scope = null) => Msi(scope) with
    {
        Id = "fixture-exe",
        Artifacts = [new ManifestArtifact("x64", "https://example.org/f.exe", new string('a', 64), 10, "exe", "exe")],
        Installer = new InstallerDeclaration("exe", Exe: exe ?? new ExeInstaller(["/S", "/install"],
            new ExeCommand("{LocalAppData}/WinBrewFixtureExe/winbrew-fixture-setup.exe", ["/S", "/uninstall"]),
            new ExeDetection("WinBrewFixtureExe", new ExeFileDetection("{LocalAppData}/WinBrewFixtureExe/winbrew-fixture-setup.exe", "1.0.0.0")))),
    };

    private static List<string> Errors(Manifest m) => ManifestValidator.Validate(m, allowV11: true).Select(e => e.Field).ToList();

    [Fact]
    public void ValidMsiAndExe_Pass() => Assert.Empty(Errors(Msi()).Concat(Errors(Exe())));

    [Fact]
    public void UserScopeMsi_WithoutTheAuthoredPerUserPropertySet_IsRejected() =>
        Assert.Contains("installer.msi.userProperties", Errors(Msi(msi: new MsiInstaller(ProductCode))));

    [Fact]
    public void MachineOnlyMsi_NeedsNoUserProperties() =>
        Assert.Empty(Errors(Msi(["machine"], new MsiInstaller(ProductCode))));

    [Fact]
    public void ReservedProperties_BadGuids_AndUnsafeValues_AreRejected()
    {
        Assert.Contains("installer.msi.properties", Errors(Msi(["machine"], new MsiInstaller(ProductCode, Properties: new Dictionary<string, string> { ["ALLUSERS"] = "1" }))));
        Assert.Contains("installer.msi.properties", Errors(Msi(["machine"], new MsiInstaller(ProductCode, Properties: new Dictionary<string, string> { ["X"] = "a b" }))));
        Assert.Contains("installer.msi.productCode", Errors(Msi(["machine"], new MsiInstaller(ProductCode.ToLowerInvariant()))));
    }

    [Fact]
    public void Exe_NeedsExplicitArgs_AnUninstaller_DetectionRules_AndSafePaths()
    {
        var ok = Exe().Installer!.Exe!;
        Assert.Contains("installer.exe.silentInstallArgs", Errors(Exe(ok with { SilentInstallArgs = [] })));
        Assert.Contains("installer.exe.detection", Errors(Exe(ok with { Detection = new ExeDetection() })));
        Assert.Contains("installer.exe.silentUninstall.path", Errors(Exe(ok with { SilentUninstall = new ExeCommand("{LocalAppData}/../x.exe", []) })));
        Assert.Contains("installer.exe.silentUninstall.path", Errors(Exe(ok with { SilentUninstall = new ExeCommand("C:/x/unins.exe", []) })));
        Assert.Contains("installer.exe.silentUninstall.path", Errors(Exe(ok, ["machine"]))); // a machine-only package cannot use {LocalAppData}
        Assert.Contains("installer.exe.detection.uninstallKey", Errors(Exe(ok with { Detection = new ExeDetection(@"Software\x") })));
    }

    [Fact]
    public void InstallerMustMatchTheArtifacts_AndHasNoDependencies()
    {
        Assert.Contains("installer", Errors(Msi() with { Installer = null }));
        Assert.Contains("installer", Errors(Msi() with { Installer = new InstallerDeclaration("exe") }));
        Assert.Contains("dependencies", Errors(Msi() with { Dependencies = [new ManifestDependency("lib", "[1.0.0,)")] }));
        var portable = Msi() with { Artifacts = [new ManifestArtifact("x64", "https://example.org/f.zip", new string('a', 64), 10, "zip", "portable")] };
        Assert.Contains("installer", Errors(portable));
    }
}

/// <summary>InstallerService flows with in-memory ports (the Windows adapters are exercised on Windows runners).</summary>
public sealed class InstallerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wbrew-inst-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _artifact;
    private readonly string _sha;
    private readonly FakeInstallerStore _store = new();
    private readonly FakeRunner _runner = new();
    private readonly FakeElevated _elevated = new();
    private readonly FakeMsi _msi = new();
    private readonly FakeProbe _probe = new();
    private readonly List<TimeSpan> _delays = [];

    public InstallerServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "staging"));
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        _artifact = Path.Combine(_root, "cached.bin");
        File.WriteAllText(_artifact, "installer bytes");
        _sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_artifact)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private InstallerService Service() => new(_store, _runner, _elevated, _msi, _probe, new FakeStorageLayout(_root),
        new PackageFileSystem(new NoLocks()), TimeProvider.System, (d, _) => { _delays.Add(d); return Task.CompletedTask; });

    private InstallerJob Job(Manifest manifest, bool allow = true, string scope = "user") =>
        new("local", manifest.Id, manifest.Version, "x64", manifest, _artifact, _sha, new InstallerOptions(allow, scope));

    private static async Task<WinBrewErrorCode> FailsWith(Func<Task> action) => (await Assert.ThrowsAsync<WinBrewException>(action)).Code;

    [Fact]
    public async Task WithoutAllowInstaller_Exit10_AndNothingRuns()
    {
        Assert.Equal(WinBrewErrorCode.ConsentRequired, await FailsWith(() => Service().InstallAsync(Job(InstallerManifestTests.Msi(), allow: false), default)));
        Assert.Empty(_runner.Calls);
        Assert.Empty(_elevated.Requests);
    }

    [Fact]
    public async Task ScopeNotDeclared_Exit6_AndNonWindows_Exit1()
    {
        Assert.Equal(WinBrewErrorCode.ConflictOrRefused,
            await FailsWith(() => Service().InstallAsync(Job(InstallerManifestTests.Msi(["machine"], new MsiInstaller("{6F1C2D3E-4A5B-4C6D-8E7F-90A1B2C3D4E5}"))), default)));
        _runner.Supported = false;
        Assert.Equal(WinBrewErrorCode.GeneralFailure, await FailsWith(() => Service().InstallAsync(Job(InstallerManifestTests.Msi()), default)));
    }

    [Fact]
    public async Task PerUserMsi_RunsMsiexecDirectly_FromAHeldCopy_WithThePerUserPropertySet()
    {
        _msi.Context = "user";
        await Service().InstallAsync(Job(InstallerManifestTests.Msi()), default);
        var call = Assert.Single(_runner.Calls);
        Assert.Equal("msiexec", call.Exe);
        Assert.Equal("/i", call.Args[0]);
        Assert.StartsWith(Path.Combine(_root, "staging"), call.Args[1], StringComparison.Ordinal);
        Assert.Contains("ALLUSERS=2", call.Args);
        Assert.Contains("MSIINSTALLPERUSER=1", call.Args);
        Assert.True(_runner.HeldDuringRun, "the artifact handle must be open while msiexec runs");
        Assert.Empty(_elevated.Requests);
        var record = Assert.Single(_store.Rows.Values);
        Assert.Equal(("Installed", "user", "{6F1C2D3E-4A5B-4C6D-8E7F-90A1B2C3D4E5}"), (record.State, record.Scope, record.ProductCode));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "staging")));
    }

    [Fact]
    public async Task PerMachine_GoesThroughTheElevatedHelper_WithATypedRequest()
    {
        _msi.Context = "machine";
        _elevated.Result = new ElevatedResult("1", 0, "C:\\log.txt", null, null);
        await Service().InstallAsync(Job(InstallerManifestTests.Msi(), scope: "machine"), default);
        var request = Assert.Single(_elevated.Requests);
        Assert.Equal((ElevatedProtocol.MsiInstall, _sha), (request.Operation, request.ArtifactSha256));
        Assert.EndsWith(".msi", request.ArtifactPath, StringComparison.Ordinal);
        Assert.False(File.Exists(request.ArtifactPath), "the staged copy for the helper is removed afterwards");
        Assert.DoesNotContain("ALLUSERS", request.MsiProperties!.Keys);
        Assert.Empty(_runner.Calls);
        Assert.Equal("machine", _store.Rows.Values.Single().Scope);
    }

    [Theory]
    [InlineData(1618, WinBrewErrorCode.InstallerBusy)]
    [InlineData(1603, WinBrewErrorCode.GeneralFailure)]
    [InlineData(1602, WinBrewErrorCode.Cancelled)]
    public async Task FailedInstalls_MapToTheirCodes_AndRecordNothing(int msiexec, WinBrewErrorCode expected)
    {
        _runner.ExitCode = msiexec;
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Service().InstallAsync(Job(InstallerManifestTests.Msi()), default));
        Assert.Equal(expected, ex.Code);
        Assert.Empty(_store.Rows);
        if (msiexec == 1618)
        {
            Assert.Equal(MsiRules.BusyRetries + 1, _runner.Calls.Count);
            Assert.Equal(MsiRules.BusyRetries, _delays.Count);
        }

        if (msiexec == 1603)
        {
            Assert.Contains(".log", ex.Message, StringComparison.Ordinal); // the log path is included
        }
    }

    [Fact]
    public async Task RebootRequired_RecordsTheInstall_ThenExits9()
    {
        _runner.ExitCode = 3010;
        _msi.Context = "user";
        Assert.Equal(WinBrewErrorCode.RebootRequired, await FailsWith(() => Service().InstallAsync(Job(InstallerManifestTests.Msi()), default)));
        Assert.Equal("RebootRequired", _store.Rows.Values.Single().State);
    }

    [Fact]
    public async Task UnconfirmedMsiContext_IsUnknown_Exit8_AndAContextChangeIsRecorded()
    {
        _msi.Context = null;
        Assert.Equal(WinBrewErrorCode.PartialOrRecoveryRequired, await FailsWith(() => Service().InstallAsync(Job(InstallerManifestTests.Msi()), default)));
        Assert.Equal("Unknown", _store.Rows.Values.Single().State);

        _store.Rows.Clear();
        _msi.Context = "machine"; // asked for per-user, Windows Installer put it per-machine
        var warnings = await Service().InstallAsync(Job(InstallerManifestTests.Msi()), default);
        Assert.Equal("machine", _store.Rows.Values.Single().Scope);
        Assert.Contains(warnings, w => w.Contains("actual context", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HeldHashMismatch_Exit5_AndTheInstallerNeverRuns()
    {
        _runner.HashOverride = new string('b', 64);
        Assert.Equal(WinBrewErrorCode.VerificationFailure, await FailsWith(() => Service().InstallAsync(Job(InstallerManifestTests.Msi()), default)));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Exe_DetectionAllMatch_Installed_Ambiguous_Unknown8()
    {
        _probe.Key = true;
        _probe.FileVersion = "1.0.0.0";
        await Service().InstallAsync(Job(InstallerManifestTests.Exe()), default);
        var call = Assert.Single(_runner.Calls);
        Assert.Equal(["/S", "/install"], call.Args);
        Assert.Equal("Installed", _store.Rows.Values.Single().State);

        _store.Rows.Clear();
        _probe.FileVersion = "0.9.0.0"; // key present, file version differs
        Assert.Equal(WinBrewErrorCode.PartialOrRecoveryRequired, await FailsWith(() => Service().InstallAsync(Job(InstallerManifestTests.Exe()), default)));
        Assert.Equal("Unknown", _store.Rows.Values.Single().State);
    }

    [Fact]
    public async Task Uninstall_Msi_RemovesTheRecordOnlyWhenWindowsInstallerSaysAbsent()
    {
        _msi.Context = "user";
        await Service().InstallAsync(Job(InstallerManifestTests.Msi()), default);
        var record = _store.Rows.Values.Single();

        Assert.Equal(WinBrewErrorCode.ConsentRequired, await FailsWith(() => Service().UninstallAsync(record, InstallerOptions.None, default)));

        _msi.State = MsiProductState.Installed; // still there afterwards
        Assert.Equal(WinBrewErrorCode.PartialOrRecoveryRequired, await FailsWith(() => Service().UninstallAsync(record, new InstallerOptions(true, "user"), default)));
        Assert.Equal("Unknown", _store.Rows.Values.Single().State);

        _msi.State = MsiProductState.Absent;
        await Service().UninstallAsync(_store.Rows.Values.Single(), new InstallerOptions(true, "user"), default);
        Assert.Empty(_store.Rows);
        Assert.Equal("/x", _runner.Calls[^1].Args[0]);
    }

    [Fact]
    public async Task Uninstall_Exe_WithAMissingUninstaller_IsRefused_NeverAShell()
    {
        _probe.Key = true;
        _probe.FileVersion = "1.0.0.0";
        await Service().InstallAsync(Job(InstallerManifestTests.Exe()), default);
        _probe.FileVersion = null; // the uninstaller file is gone
        Assert.Equal(WinBrewErrorCode.ConflictOrRefused,
            await FailsWith(() => Service().UninstallAsync(_store.Rows.Values.Single(), new InstallerOptions(true, "user"), default)));
    }

    [Fact]
    public async Task Reconcile_ReportsDrift_AndNeverCorrectsIt()
    {
        _msi.Context = "user";
        await Service().InstallAsync(Job(InstallerManifestTests.Msi()), default);
        var record = _store.Rows.Values.Single();
        _msi.State = MsiProductState.Installed;
        Assert.Null(Service().Reconcile(record));
        _msi.State = MsiProductState.Absent;
        Assert.Contains("removed outside WinBrew", Service().Reconcile(record), StringComparison.Ordinal);
        Assert.Equal(record, _store.Rows.Values.Single()); // unchanged
    }

    // ---- fakes -------------------------------------------------------------------------------------

    private sealed class NoLocks : IFileLockInspector
    {
        public IReadOnlyList<string> GetLockingProcesses(IReadOnlyList<string> paths) => [];
    }

    private sealed class FakeInstallerStore : IInstallerStore
    {
        public Dictionary<(string, string), InstallerPackage> Rows { get; } = [];

        public Task<InstallerPackage?> GetInstallerPackageAsync(string repo, string id, CancellationToken ct) => Task.FromResult(Rows.GetValueOrDefault((repo, id)));

        public Task<IReadOnlyList<InstallerPackage>> ListInstallerPackagesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InstallerPackage>>([.. Rows.Values]);

        public Task UpsertInstallerPackageAsync(InstallerPackage package, CancellationToken ct)
        {
            Rows[(package.Repo, package.Id)] = package;
            return Task.CompletedTask;
        }

        public Task DeleteInstallerPackageAsync(string repo, string id, CancellationToken ct)
        {
            Rows.Remove((repo, id));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunner : IInstallerRunner
    {
        private Func<bool>? _isHeld;

        public bool Supported { get; set; } = true;
        public int ExitCode { get; set; }
        public string? HashOverride { get; set; }
        public bool HeldDuringRun { get; private set; }
        public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = [];

        public bool IsSupported => Supported;

        public Task<HeldArtifact> HoldAsync(string path, CancellationToken ct)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _isHeld = () => !stream.SafeFileHandle.IsClosed;
            var sha = HashOverride ?? Convert.ToHexStringLower(SHA256.HashData(stream));
            return Task.FromResult(new HeldArtifact(path, sha, stream));
        }

        public Task<int> RunMsiexecAsync(IReadOnlyList<string> arguments, CancellationToken ct) => Run("msiexec", arguments);

        public Task<int> RunExecutableAsync(string path, IReadOnlyList<string> arguments, CancellationToken ct) => Run(path, arguments);

        private Task<int> Run(string exe, IReadOnlyList<string> args)
        {
            Calls.Add((exe, args));
            // The FileShare.Read-only handle from HoldAsync is still open while the installer runs.
            HeldDuringRun = _isHeld?.Invoke() == true;

            return Task.FromResult(ExitCode);
        }
    }

    private sealed class FakeElevated : IElevatedRunner
    {
        public ElevatedResult Result { get; set; } = new("1", 0, null, null, null);
        public List<ElevatedRequest> Requests { get; } = [];

        public Task<ElevatedResult> RunAsync(ElevatedRequest request, CancellationToken ct)
        {
            Requests.Add(ElevatedProtocol.Parse(ElevatedProtocol.Serialize(request))); // what the helper would accept
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeMsi : IMsiProducts
    {
        public string? Context { get; set; }
        public MsiProductState State { get; set; } = MsiProductState.Absent;

        public bool IsSupported => true;

        public MsiProductState QueryState(string productCode) => State;

        public string? GetInstalledContext(string productCode) => Context;
    }

    private sealed class FakeProbe : IInstallProbe
    {
        public bool Key { get; set; }
        public string? FileVersion { get; set; }

        public string? Expand(string pattern) => "/expanded/" + pattern.Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal);

        public bool UninstallKeyExists(string keyName) => Key;

        public (bool Exists, string? Version) GetFile(string path) => (FileVersion is not null, FileVersion);
    }
}

/// <summary>A12: no code path builds a shell command string (enforced by scanning the production sources).</summary>
public sealed partial class NoShellCommandStringTests
{
    [GeneratedRegex(@"\.Arguments\s*=|UseShellExecute\s*=\s*true|cmd(\.exe)?""?\s*/[ck]|powershell(\.exe)?""?\s+-|ShellExecuteExW?\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex ShellPattern();

    [Fact]
    public void ProductionSources_NeverBuildAShellCommandLine()
    {
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        Assert.True(Directory.Exists(src), src);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal) || line.TrimStart().StartsWith("///", StringComparison.Ordinal) ||
                    !ShellPattern().IsMatch(line))
                {
                    continue;
                }

                // The ONE allowed site: the elevated helper launch (runas verb, one argument via ArgumentList).
                var allowed = file.EndsWith("InstallerAdapters.cs", StringComparison.Ordinal) && line.Contains("Verb = \"runas\"", StringComparison.Ordinal);
                if (!allowed)
                {
                    offenders.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Shell command construction found:\n" + string.Join("\n", offenders));
    }
}
