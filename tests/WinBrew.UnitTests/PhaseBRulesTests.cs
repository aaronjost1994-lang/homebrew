using System.Runtime.InteropServices;
using System.Text;
using WinBrew.Application.Packages;
using WinBrew.Core;
using WinBrew.Core.Archive;
using WinBrew.Core.Artifacts;
using WinBrew.Core.Catalog;
using WinBrew.Core.Manifests;
using WinBrew.Core.Net;
using WinBrew.Core.Pe;
using WinBrew.TestFixtures;

namespace WinBrew.UnitTests;

/// <summary>A13: URL gate, redirect policy, origin rule, redaction.</summary>
public class UrlPolicyTests
{
    private static readonly InsecureLocalhostPolicy On = new(true, true, true);

    [Theory]
    [InlineData("https://example.org/x", true)]
    [InlineData("http://example.org/x", false)]
    [InlineData("ftp://example.org/x", false)]
    [InlineData("file:///etc/passwd", false)]
    public void OnlyHttps_OutsideTheLocalhostException(string url, bool ok) =>
        Assert.Equal(ok, UrlPolicy.Check(new Uri(url), InsecureLocalhostPolicy.Release) is null);

    [Theory]
    [InlineData("http://localhost:8080/x")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://[::1]:9/x")]
    public void LocalhostException_AppliesToTheThreeLiteralHosts_OnlyWhenFullyGated(string url)
    {
        Assert.Null(UrlPolicy.Check(new Uri(url), On));
        Assert.NotNull(UrlPolicy.Check(new Uri(url), InsecureLocalhostPolicy.Release));
        Assert.NotNull(UrlPolicy.Check(new Uri(url), new InsecureLocalhostPolicy(true, true, false)));
    }

    [Fact]
    public void LookalikeHosts_AreNotLoopback() =>
        Assert.NotNull(UrlPolicy.Check(new Uri("http://localhost.evil.example/x"), On));

    [Fact]
    public void Redirect_ToAnotherOrigin_StripsCredentials()
    {
        var d = UrlPolicy.DecideRedirect(new Uri("https://a.example/x"), new Uri("https://b.example/y"), 0, On);
        Assert.True(d.Follow);
        Assert.True(d.StripCredentials);
    }

    [Fact]
    public void Redirect_SameOrigin_KeepsCredentials_RelativeLocationResolves()
    {
        var d = UrlPolicy.DecideRedirect(new Uri("https://a.example/dir/x"), new Uri("y", UriKind.Relative), 0, On);
        Assert.True(d.Follow);
        Assert.False(d.StripCredentials);
        Assert.Equal(new Uri("https://a.example/dir/y"), d.Target);
    }

    [Theory]
    [InlineData("https://a.example:443/x", "https://a.example:8443/x")] // port is part of the origin
    [InlineData("https://a.example/x", "https://A.EXAMPLE.org/x")]
    public void OriginChanges_AreDetected(string from, string to) =>
        Assert.False(UrlPolicy.SameOrigin(new Uri(from), new Uri(to)));

    [Fact]
    public void Redirect_HttpsToLoopbackHttp_IsADowngrade_AndRefused()
    {
        var d = UrlPolicy.DecideRedirect(new Uri("https://a.example/x"), new Uri("http://127.0.0.1/y"), 0, On);
        Assert.False(d.Follow);
    }

    [Fact]
    public void Redirect_Sixth_IsRefused() =>
        Assert.False(UrlPolicy.DecideRedirect(new Uri("https://a.example/x"), new Uri("https://a.example/y"), 5, On).Follow);

    [Fact]
    public void Redirect_WithoutLocation_IsRefused() =>
        Assert.False(UrlPolicy.DecideRedirect(new Uri("https://a.example/x"), null, 0, On).Follow);

    [Theory]
    [InlineData("https://u:p@h.example/a?token=SECRET", "https://h.example/a?<redacted>")]
    [InlineData("https://h.example/a/b", "https://h.example/a/b")]
    [InlineData("https://h.example/a#frag", "https://h.example/a")]
    public void Redaction(string url, string expected) => Assert.Equal(expected, UrlPolicy.Redact(new Uri(url)));

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("cookie", true)]
    [InlineData("Set-Cookie", true)]
    [InlineData("Proxy-Authorization", true)]
    [InlineData("Accept", false)]
    public void SensitiveHeaders(string name, bool sensitive) => Assert.Equal(sensitive, UrlPolicy.IsSensitiveHeader(name));

    [Fact]
    public void CredentialOrigin_IsLowercaseSchemeHostPort() =>
        Assert.Equal("https://cdn.example.org:443", UrlPolicy.Origin(new Uri("HTTPS://CDN.Example.org/x")));
}

/// <summary>A13 pure entry-name rules (the extractor tests run the same corpus against real ZIPs).</summary>
public class ArchiveEntryRulesTests
{
    [Theory]
    [InlineData("bin/tool.exe", "bin/tool.exe", false)]
    [InlineData("bin\\tool.exe", "bin/tool.exe", false)]   // normalized
    [InlineData("docs/", "docs", true)]
    [InlineData("a.b.c/d e.txt", "a.b.c/d e.txt", false)]
    [InlineData("CONSOLE.txt", "CONSOLE.txt", false)]       // only exact device stems are reserved
    [InlineData("com10.txt", "com10.txt", false)]
    public void ValidNames_Normalize(string name, string normalized, bool isDirectory)
    {
        var check = ArchiveEntryRules.CheckName(name);
        Assert.True(check.Ok, check.Problem);
        Assert.Equal(normalized, check.NormalizedPath);
        Assert.Equal(isDirectory, check.IsDirectory);
    }

    [Theory]
    [InlineData("", ArchiveDefense.EmptyName)]
    [InlineData("/", ArchiveDefense.AbsolutePath)]
    [InlineData("CON .txt", ArchiveDefense.ReservedDeviceName)]   // Windows trims the space before the extension
    [InlineData("aux", ArchiveDefense.ReservedDeviceName)]
    [InlineData("x/..", ArchiveDefense.DotDotSegment)]
    public void InvalidNames(string name, ArchiveDefense defense) =>
        Assert.Equal(defense, ArchiveEntryRules.CheckName(name).Defense);

    [Theory]
    [InlineData(unchecked((int)0xA1FF0000), true)]   // lrwxrwxrwx
    [InlineData(unchecked((int)0x81A40000), false)]  // -rw-r--r--
    [InlineData(0x20, false)]                         // DOS archive bit only
    public void SymlinkModeBits(int attributes, bool symlink) =>
        Assert.Equal(symlink, ArchiveEntryRules.HasUnixSymlinkMode(attributes));

    [Fact]
    public void Containment_NeedsTheTrailingSeparator()
    {
        var root = Path.Combine(Path.GetTempPath(), "stage");
        Assert.True(ArchiveEntryRules.IsContained(root, Path.Combine(root, "a"), ignoreCase: false));
        Assert.False(ArchiveEntryRules.IsContained(root, root + "x" + Path.DirectorySeparatorChar + "a", ignoreCase: false));
        Assert.False(ArchiveEntryRules.IsContained(root, root, ignoreCase: false));
    }
}

/// <summary>A10: PE parsing, machine acceptance and the artifact selection order.</summary>
public class PeAndArchitectureTests
{
    [Theory]
    [InlineData(PeBytes.Amd64, (ushort)3, PeSubsystemKind.Console)]
    [InlineData(PeBytes.I386, (ushort)2, PeSubsystemKind.Gui)]
    [InlineData(PeBytes.Arm64, (ushort)10, PeSubsystemKind.Other)]
    public void PeHeaders_Parse(ushort machine, ushort subsystem, PeSubsystemKind kind)
    {
        var info = PeImage.TryParse(PeBytes.Create(machine, subsystem));
        Assert.NotNull(info);
        Assert.Equal(machine, info.Machine);
        Assert.Equal(kind, info.SubsystemKind);
    }

    [Fact]
    public void NonPe_And_Truncated_AreNull()
    {
        Assert.Null(PeImage.TryParse("not a pe"u8));
        Assert.Null(PeImage.TryParse(PeBytes.Create().AsSpan(0, 100)));
        var badSignature = PeBytes.Create();
        badSignature[0x40] = (byte)'X';
        Assert.Null(PeImage.TryParse(badSignature));
        var hugeOffset = PeBytes.Create();
        BitConverter.TryWriteBytes(hugeOffset.AsSpan(0x3C), 0x7FFFFFF0);
        Assert.Null(PeImage.TryParse(hugeOffset));
    }

    [Theory]
    [InlineData(Architecture.X64, PeBytes.Amd64, true)]
    [InlineData(Architecture.X64, PeBytes.I386, true)]
    [InlineData(Architecture.X64, PeBytes.Arm64, false)]
    [InlineData(Architecture.X64, PeBytes.Arm32, false)]
    [InlineData(Architecture.Arm64, PeBytes.Arm64, true)]
    [InlineData(Architecture.Arm64, PeBytes.Amd64, true)]
    [InlineData(Architecture.Arm64, PeBytes.I386, true)]
    [InlineData(Architecture.Arm64, PeBytes.Arm32, false)]
    [InlineData(Architecture.Arm64, (ushort)0x1234, false)]
    public void MachineAcceptance(Architecture host, ushort machine, bool accepted) =>
        Assert.Equal(accepted, HostArchitectureRules.IsAcceptedMachine(host, machine));

    [Theory]
    [InlineData(Architecture.X64, new[] { "x64", "x86" }, "x64", false)]
    [InlineData(Architecture.X64, new[] { "x86" }, "x86", true)]
    [InlineData(Architecture.X64, new[] { "arm64" }, null, false)]
    [InlineData(Architecture.Arm64, new[] { "arm64", "x64", "x86" }, "arm64", false)]
    [InlineData(Architecture.Arm64, new[] { "x64", "x86" }, "x64", true)]
    [InlineData(Architecture.Arm64, new[] { "x86" }, "x86", true)]
    public void SelectionOrder(Architecture host, string[] offered, string? expected, bool fallback)
    {
        var selection = HostArchitectureRules.Select(host, offered);
        Assert.Equal(expected, selection?.Architecture);
        if (selection is not null)
        {
            Assert.Equal(fallback, selection.IsFallback);
            Assert.Equal(expected == "x86", selection.IsX86Fallback);
        }
    }
}

/// <summary>D6: strict index.json parsing.</summary>
public class RepositoryIndexParserTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Index(string versionKey = "1.0.0", string archs = "[\"x64\"]", string deps = "[]", string extra = "", int version = 1) => $$"""
        { "version": {{version}}, "expires": "2030-01-01T00:00:00Z", {{extra}}
          "packages": { "tool": { "displayName": "Tool", "description": "d",
            "versions": { "{{versionKey}}": { "sha256": "{{Sha}}", "size": 10, "architectures": {{archs}}, "dependencies": {{deps}} } } } } }
        """;

    [Fact]
    public void ValidIndex_Parses()
    {
        var index = RepositoryIndexParser.Parse(Encoding.UTF8.GetBytes(Index()));
        Assert.Equal(1, index.Version);
        Assert.Equal(["x64"], index.Packages["tool"].Versions["1.0.0"].Architectures);
    }

    [Theory]
    [InlineData("1.0")]          // not normalized (D3)
    [InlineData("01.0.0")]
    [InlineData("banana")]
    public void UnnormalizedVersionKeys_AreRejected(string key) => AssertInvalid(Index(versionKey: key));

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"x64\",\"x64\"]")]
    [InlineData("[\"mips\"]")]
    public void BadArchitectures_AreRejected(string archs) => AssertInvalid(Index(archs: archs));

    [Theory]
    [InlineData("[{\"id\":\"lib\",\"range\":\"1.0.0\"}]")]    // bare version (D3)
    [InlineData("[{\"id\":\"lib\",\"range\":\"[1.*,2.0)\"}]")] // floating
    [InlineData("[{\"id\":\"Bad Id\",\"range\":\"[1.0,2.0)\"}]")]
    public void BadDependencies_AreRejected(string deps) => AssertInvalid(Index(deps: deps));

    [Fact]
    public void UnknownMembers_AreRejected() => AssertInvalid(Index(extra: "\"latestVersion\": \"1.0.0\","));

    [Fact]
    public void VersionZero_IsRejected() => AssertInvalid(Index(version: 0));

    [Fact]
    public void MissingRequiredMembers_AreRejected() =>
        AssertInvalid("""{ "version": 1, "packages": {} }""");

    private static void AssertInvalid(string json)
    {
        var ex = Assert.Throws<WinBrewException>(() => RepositoryIndexParser.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(WinBrewErrorCode.VerificationFailure, ex.Code);
    }
}

/// <summary>A9 strict manifest parsing.</summary>
public class ManifestParserTests
{
    private const string Valid = """
        { "schemaVersion": "1", "id": "tool", "version": "1.0.0", "displayName": "Tool", "description": "d",
          "homepage": "https://example.org/", "license": "MIT", "scope": ["user"],
          "artifacts": [ { "architecture": "x64", "url": "https://example.org/t.zip",
            "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "size": 1,
            "format": "zip", "installType": "portable" } ],
          "executables": [ { "name": "tool", "target": "bin/tool.exe" } ] }
        """;

    [Fact]
    public void ValidManifest_Parses() =>
        Assert.Equal("tool", ManifestParser.Parse(Encoding.UTF8.GetBytes(Valid), testHooksBuild: false).Id);

    [Fact]
    public void UnknownMember_IsRejected() =>
        Assert.Equal(WinBrewErrorCode.VerificationFailure, Assert.Throws<WinBrewException>(() =>
            ManifestParser.Parse(Encoding.UTF8.GetBytes(Valid.Replace("\"license\"", "\"postInstall\": \"rm -rf\", \"license\"", StringComparison.Ordinal)), false)).Code);

    [Fact]
    public void ValidatorFindings_AreVerificationFailures() =>
        Assert.Equal(WinBrewErrorCode.VerificationFailure, Assert.Throws<WinBrewException>(() =>
            ManifestParser.Parse(Encoding.UTF8.GetBytes(Valid.Replace("https://example.org/t.zip", "http://example.org/t.zip", StringComparison.Ordinal)), false)).Code);
}

/// <summary>A8 package request syntax.</summary>
public class PackageRequestTests
{
    [Theory]
    [InlineData("tool", null, null, "tool", null)]
    [InlineData("main/tool", null, "main", "tool", null)]
    [InlineData("tool@1.0", null, null, "tool", "1.0.0")]       // normalized (D3)
    [InlineData("tool", "2.1.0-beta.1", null, "tool", "2.1.0-beta.1")]
    public void Parse(string package, string? option, string? repo, string id, string? version)
    {
        var r = PackageLookup.Parse(package, option);
        Assert.Equal(repo, r.Repo);
        Assert.Equal(id, r.Id);
        Assert.Equal(version, r.Version);
    }

    [Theory]
    [InlineData("tool@1.0.0", "2.0.0")]  // both forms (A8)
    [InlineData("Tool", null)]           // ids are normalized lowercase (A9)
    [InlineData("a/b/c", null)]
    [InlineData("tool@", null)]
    [InlineData("tool@x.y", null)]
    public void Invalid_IsAUsageError(string package, string? option) =>
        Assert.Equal(WinBrewErrorCode.UsageError, Assert.Throws<WinBrewException>(() => PackageLookup.Parse(package, option)).Code);
}
