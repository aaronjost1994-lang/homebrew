using WinBrew.Core.Manifests;

namespace WinBrew.UnitTests;

public class ManifestValidatorTests
{
    /// <summary>A fully valid portable manifest baseline (A9).</summary>
    private static Manifest ValidManifest() => new()
    {
        SchemaVersion = "1",
        Id = "hellocmd",
        Version = "1.2.0",
        DisplayName = "Hello CMD",
        Description = "A tiny fixture package used by the WinBrew test suite.",
        Homepage = "https://example.test/hellocmd",
        License = "MIT",
        Scope = ["user"],
        Artifacts =
        [
            new ManifestArtifact(
                Architecture: "x64",
                Url: "https://repo.test/artifacts/hellocmd-1.2.0-x64.zip",
                Sha256: new string('a', 64),
                Size: 1024,
                Format: "zip",
                InstallType: "portable"),
        ],
        Executables = [new ManifestExecutable("hello", "bin/hello.exe")],
    };

    private static List<string> Errors(Manifest m, bool testHooksBuild = false, bool allowV11 = false) =>
        ManifestValidator.Validate(m, testHooksBuild, allowV11).Select(e => e.Field).ToList();

    [Fact]
    public void ValidManifest_Passes()
    {
        var errors = ManifestValidator.Validate(ValidManifest());
        Assert.Empty(errors);
    }

    [Fact]
    public void WrongSchemaVersion_Fails() =>
        Assert.Contains("schemaVersion", Errors(ValidManifest() with { SchemaVersion = "2" }));

    [Fact]
    public void UnnormalizedVersion_Rejected() // A9: version must already be normalized (D3)
    {
        Assert.Contains("version", Errors(ValidManifest() with { Version = "v1.2" }));
        Assert.Contains("version", Errors(ValidManifest() with { Version = "1.2" }));
    }

    [Fact]
    public void HttpsHomepage_Required()
    {
        Assert.Contains("homepage", Errors(ValidManifest() with { Homepage = "http://example.test/x" }));
        Assert.Contains("homepage", Errors(ValidManifest() with { Homepage = "ftp://example.test/x" }));
        Assert.DoesNotContain("homepage", Errors(ValidManifest()));
    }

    [Theory]
    [InlineData("MIT")]
    [InlineData("MIT OR Apache-2.0")]
    [InlineData("(MIT OR GPL-3.0-only) WITH Classpath-exception-2.0")]
    [InlineData("LicenseRef-Proprietary")]
    public void Spdx_StructurallyValid_Accepted(string license) =>
        Assert.DoesNotContain("license", Errors(ValidManifest() with { License = license }));

    [Theory]
    [InlineData("MIT AND")]
    [InlineData("OR MIT")]
    [InlineData("(MIT")]
    [InlineData("MIT OR OR Apache-2.0")]
    [InlineData("MIT && Apache-2.0")]
    public void Spdx_StructurallyInvalid_Rejected(string license) =>
        Assert.Contains("license", Errors(ValidManifest() with { License = license }));

    [Fact]
    public void MachineScope_RejectedInV10() // D1/A9: v1.0 = portable user scope only
    {
        Assert.Contains("scope", Errors(ValidManifest() with { Scope = ["user", "machine"] }));
        Assert.DoesNotContain("scope", Errors(ValidManifest() with { Scope = ["user", "machine"] }, allowV11: true));
    }

    [Fact]
    public void DuplicateArchitectures_Rejected()
    {
        var m = ValidManifest();
        m = m with
        {
            Artifacts =
            [
                m.Artifacts[0],
                m.Artifacts[0] with { },
            ],
        };
        Assert.Contains("artifacts[1].architecture", Errors(m));
    }

    [Fact]
    public void BadSha256_Rejected() =>
        Assert.Contains("artifacts[0].sha256",
            Errors(ValidManifest() with { Artifacts = [ValidManifest().Artifacts[0] with { Sha256 = new string('A', 64) }] }));

    [Fact]
    public void MismatchedFormatInstallType_Rejected() // zip pairs with portable (A9)
    {
        var artifact = ValidManifest().Artifacts[0] with { InstallType = "msi" };
        Assert.Contains("artifacts[0]", Errors(ValidManifest() with { Artifacts = [artifact] }));
    }

    [Fact]
    public void MsiArtifact_RejectedInV10_AllowedWithV11Flag()
    {
        var artifact = ValidManifest().Artifacts[0] with { Format = "msi", InstallType = "msi" };
        Assert.Contains("artifacts[0].installType", Errors(ValidManifest() with { Artifacts = [artifact] }));
        Assert.DoesNotContain("artifacts[0].installType",
            Errors(ValidManifest() with { Artifacts = [artifact] }, allowV11: true));
    }

    [Fact]
    public void InstallerDeclaration_RejectedInV10_WithTrustWarning()
    {
        var errors = ManifestValidator.Validate(ValidManifest() with
        {
            Installer = System.Text.Json.JsonSerializer.SerializeToElement(
                new { type = "msi" }),
        });
        Assert.Contains(errors, e => e.Field == "installer");
    }

    [Fact]
    public void Executables_OnlyValidOnPortablePackages()
    {
        var artifact = ValidManifest().Artifacts[0] with { Format = "msi", InstallType = "msi" };
        var errors = Errors(ValidManifest() with
        {
            Artifacts = [artifact],
        }, allowV11: true);
        Assert.Contains("executables", errors);
    }

    [Fact]
    public void ExecutableTarget_MustBeRelativeExeInsidePackage() // A9 rules
    {
        Assert.DoesNotContain("executables[0].target", Errors(ValidManifest()));

        // not an .exe (A9; shims for non-.exe entry points are a non-goal, A3)
        Assert.Contains("executables[0].target", Errors(ValidManifest() with
        {
            Executables = [new ManifestExecutable("hello", "bin/hello")],
        }));

        // '..' escape rejected
        Assert.Contains("executables[0].target", Errors(ValidManifest() with
        {
            Executables = [new ManifestExecutable("hello", "../escape.exe")],
        }));

        // absolute path rejected
        Assert.Contains("executables[0].target", Errors(ValidManifest() with
        {
            Executables = [new ManifestExecutable("hello", "/abs/hello.exe")],
        }));

        // backslash separators rejected (A13 ambiguity rule)
        Assert.Contains("executables[0].target", Errors(ValidManifest() with
        {
            Executables = [new ManifestExecutable("hello", @"bin\hello.exe")],
        }));

        // drive letter rejected
        Assert.Contains("executables[0].target", Errors(ValidManifest() with
        {
            Executables = [new ManifestExecutable("hello", "C:/tools/hello.exe")],
        }));
    }

    [Fact]
    public void ShimName_WithExeSuffix_Rejected() // <name>.exe doubling rule
    {
        Assert.Contains("executables[0].name", Errors(ValidManifest() with
        {
            Executables = [new ManifestExecutable("hello.exe", "bin/hello.exe")],
        }));
    }

    [Fact]
    public void DuplicateShimNames_Rejected()
    {
        var errors = Errors(ValidManifest() with
        {
            Executables =
            [
                new ManifestExecutable("hello", "bin/hello.exe"),
                new ManifestExecutable("HELLO", "bin2/hello.exe"),
            ],
        });
        Assert.Contains("executables[1].name", errors);
    }

    [Fact]
    public void Dependencies_RangeAndIdRules()
    {
        var good = ValidManifest() with
        {
            Dependencies = [new ManifestDependency("libfoo", "[1.0.0,2.0.0)")],
        };
        Assert.Empty(Errors(good));

        // bare range rejected (D3)
        Assert.Contains("dependencies[0].range", Errors(ValidManifest() with
        {
            Dependencies = [new ManifestDependency("libfoo", "1.2.3")],
        }));

        // invalid id rejected (A9)
        Assert.Contains("dependencies[0].id", Errors(ValidManifest() with
        {
            Dependencies = [new ManifestDependency("Bad_ID", "[1.0.0,)")],
        }));

        // self-dependency rejected
        Assert.Contains("dependencies[0].id", Errors(ValidManifest() with
        {
            Dependencies = [new ManifestDependency("hellocmd", "[1.0.0,)")],
        }));

        // duplicate dependency id rejected
        Assert.Contains("dependencies[1].id", Errors(ValidManifest() with
        {
            Dependencies =
            [
                new ManifestDependency("libfoo", "[1.0.0,2.0.0)"),
                new ManifestDependency("libfoo", "[2.0.0,3.0.0)"),
            ],
        }));

        // repo-qualified dependency accepted (A9 cross-repo)
        Assert.DoesNotContain("dependencies[0].id", Errors(ValidManifest() with
        {
            Dependencies = [new ManifestDependency("other/libbar", "[1.0.0,)")],
        }));
    }

    [Fact]
    public void LoopbackHttpUrl_OnlyInTestHooksBuilds() // A13
    {
        var artifact = ValidManifest().Artifacts[0] with { Url = "http://127.0.0.1:8080/fixture.zip" };

        Assert.Contains("artifacts[0].url", Errors(ValidManifest() with { Artifacts = [artifact] }, testHooksBuild: false));
        Assert.DoesNotContain("artifacts[0].url", Errors(ValidManifest() with { Artifacts = [artifact] }, testHooksBuild: true));

        // non-loopback http rejected even in test-hooks builds
        var bad = ValidManifest().Artifacts[0] with { Url = "http://repo.test/x.zip" };
        Assert.Contains("artifacts[0].url", Errors(ValidManifest() with { Artifacts = [bad] }, testHooksBuild: true));
    }
}
