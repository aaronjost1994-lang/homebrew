using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using WinBrew.TestFixtures;
using Xunit;

namespace WinBrew.EndToEndTests;

/// <summary>Reads scripts/publish-files.txt and the release outputs named by environment variables.</summary>
internal static class Release
{
    public const string ZipVariable = "WINBREW_RELEASE_ZIP";
    public const string DirectoryVariable = "WINBREW_RELEASE_DIR";

    public static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinBrew.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("WinBrew.sln not found above the test directory.");
        }
    }

    /// <summary>The non-comment lines of scripts/publish-files.txt: the exact release-zip set.</summary>
    public static IReadOnlyList<string> ZipSet() =>
        File.ReadAllLines(Path.Combine(RepositoryRoot, "scripts", "publish-files.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Order(StringComparer.Ordinal)
            .ToList();

    public static string Zip => Environment.GetEnvironmentVariable(ZipVariable)!;

    /// <summary>Extracts the release zip into a fresh directory (a clean "download" of the release).</summary>
    public static string Extract()
    {
        var dir = Directory.CreateTempSubdirectory("wbrew-e2e-").FullName;
        ZipFile.ExtractToDirectory(Zip, dir);
        return dir;
    }

    public static (int ExitCode, string StdOut, string StdErr, TimeSpan Elapsed) Run(string exe, IReadOnlyDictionary<string, string?> env, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (k, v) in env)
        {
            if (v is null)
            {
                psi.Environment.Remove(k);
            }
            else
            {
                psi.Environment[k] = v;
            }
        }

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        watch.Stop();
        return (process.ExitCode, stdout.Result, stderr.Result, watch.Elapsed);
    }

    public static double Median(List<double> values)
    {
        values.Sort();
        return values.Count % 2 == 1 ? values[values.Count / 2] : (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2;
    }
}

/// <summary>A18: the release zip and SHA256SUMS.txt are checked against scripts/publish-files.txt (any OS).</summary>
public sealed class ReleaseZipTests
{
    [Fact]
    public void PublishFilesManifest_ListsAtLeastTheA14Set()
    {
        var set = Release.ZipSet();
        foreach (var required in new[] { "wbrew.exe", "e_sqlite3.dll", "wbrew-shim.exe" })
        {
            Assert.Contains(required, set);
        }
    }

    [ArtifactFact(Release.ZipVariable, "a wbrew-<ver>-win-<arch>.zip")]
    public void ReleaseZip_ContainsExactlyThePublishFilesSet()
    {
        using var zip = ZipFile.OpenRead(Release.Zip);
        var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(Release.ZipSet(), entries);
    }

    [ArtifactFact(Release.DirectoryVariable, "the release output directory (zips, loose files, SHA256SUMS.txt)")]
    public void Sha256Sums_CoverEveryShippedFile_AndMatch()
    {
        var dir = Environment.GetEnvironmentVariable(Release.DirectoryVariable)!;
        var sums = File.ReadAllLines(Path.Combine(dir, "SHA256SUMS.txt"))
            .Where(l => l.Length > 0)
            .Select(l => (Hash: l[..64], Name: l[66..].Trim()))
            .ToDictionary(x => x.Name, x => x.Hash, StringComparer.Ordinal);

        foreach (var (name, hash) in sums)
        {
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, name)))));
        }

        var zips = Directory.GetFiles(dir, "wbrew-*-win-*.zip").Select(Path.GetFileName).ToList();
        Assert.NotEmpty(zips);
        foreach (var zip in zips)
        {
            Assert.Contains(zip!, sums.Keys);
            var arch = Path.GetFileNameWithoutExtension(zip!).Split('-')[^1];
            foreach (var file in Release.ZipSet())
            {
                Assert.Contains($"win-{arch}/{file}", sums.Keys); // every shipped file, per architecture
            }
        }
    }
}

/// <summary>
/// A19 #1 / A17 on a clean Windows runner with the PUBLISHED RELEASE binary:
/// it runs from the extracted zip without installing anything, never creates
/// an extraction directory under %TEMP%, self-installs, and refuses
/// http://localhost even when every test switch is set.
/// </summary>
public sealed class ReleaseBinaryTests
{
    private static Dictionary<string, string?> FreshProfile(string root, string temp) => new()
    {
        ["WINBREW_ROOT"] = root,
        ["TEMP"] = temp,
        ["TMP"] = temp,
        ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = null,
    };

    [WindowsArtifactFact(Release.ZipVariable, "the release zip for this runner's architecture")]
    public void FromTheExtractedZip_VersionRuns_AndNothingIsExtractedUnderTemp()
    {
        var app = Release.Extract();
        var root = Directory.CreateTempSubdirectory("wbrew-root-").FullName;
        var temp = Directory.CreateTempSubdirectory("wbrew-temp-").FullName;
        var first = Release.Run(Path.Combine(app, "wbrew.exe"), FreshProfile(root, temp), "--version");
        Assert.Equal(0, first.ExitCode);
        Assert.Empty(Directory.GetFileSystemEntries(temp)); // no %TEMP%\.net\ (A4/A17)
        Console.WriteLine($"[A17] --version first run (fresh profile): {first.Elapsed.TotalMilliseconds:0} ms");
    }

    [WindowsArtifactFact(Release.ZipVariable, "the release zip for this runner's architecture")]
    public void SelfInstall_CreatesTheShim_AndWarmStartIsMeasured()
    {
        var app = Release.Extract();
        var root = Directory.CreateTempSubdirectory("wbrew-root-").FullName;
        var temp = Directory.CreateTempSubdirectory("wbrew-temp-").FullName;
        var env = FreshProfile(root, temp);
        Assert.Equal(0, Release.Run(Path.Combine(app, "wbrew.exe"), env, "self", "install", "--non-interactive").ExitCode);
        var shim = Path.Combine(root, "bin", "wbrew.exe");
        Assert.True(File.Exists(shim));

        Assert.Equal(0, Release.Run(shim, env, "--version").ExitCode); // warm-up
        var times = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var run = Release.Run(shim, env, "--version");
            Assert.Equal(0, run.ExitCode);
            times.Add(run.Elapsed.TotalMilliseconds);
        }

        Assert.Empty(Directory.GetFileSystemEntries(temp));
        var median = Release.Median(times);
        Console.WriteLine($"[A17] --version warm start through the shim: median {median:0} ms of 10");
        Assert.True(median < 300, $"warm start median {median:0} ms (A17 target < 300 ms)");
        var shimSize = new FileInfo(Path.Combine(app, "wbrew-shim.exe")).Length;
        Console.WriteLine($"[A17] wbrew-shim.exe size: {shimSize} bytes; wbrew.exe: {new FileInfo(Path.Combine(app, "wbrew.exe")).Length} bytes; zip: {new FileInfo(Release.Zip).Length} bytes");
        Assert.True(shimSize < 3 * 1024 * 1024);
    }

    [WindowsArtifactFact(Release.ZipVariable, "the release zip for this runner's architecture")]
    public void ReleaseBinary_RefusesHttpLocalhost_EvenWithEveryTestSwitchSet()
    {
        var app = Release.Extract();
        var root = Directory.CreateTempSubdirectory("wbrew-root-").FullName;
        var env = FreshProfile(root, Directory.CreateTempSubdirectory("wbrew-temp-").FullName);
        env["WINBREW_TEST_MODE"] = "1";
        var exe = Path.Combine(app, "wbrew.exe");
        Assert.Equal(2, Release.Run(exe, env, "config", "set", "testing.allowInsecureLocalhost", "true").ExitCode); // release builds reject testing.* (D10)
        Assert.Equal(5, Release.Run(exe, env, "repo", "add", "local", "http://127.0.0.1:9/", "--trust-root", new string('0', 64)).ExitCode);
    }
}
