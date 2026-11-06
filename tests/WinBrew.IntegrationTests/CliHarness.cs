using System.Diagnostics;
using System.Text.Json;
using WinBrew.Core.Shims;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>Result of one wbrew child process.</summary>
public sealed record CliRun(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>True when the test-hooks seam terminated the process (A11).</summary>
    public bool FaultInjected => StdErr.Contains("[fault-injection]", StringComparison.Ordinal);

    public JsonElement Json()
    {
        using var doc = JsonDocument.Parse(StdOut);
        return doc.RootElement.Clone();
    }

    public override string ToString() => $"exit={ExitCode}\nstdout:\n{StdOut}\nstderr:\n{StdErr}";
}

/// <summary>
/// One isolated WinBrew root (WINBREW_ROOT) plus a fixture repository served
/// over the test-hooks localhost exception (A13: test-hooks build +
/// testing.allowInsecureLocalhost + WINBREW_TEST_MODE=1). Runs the CLI as a
/// REAL child process (wbrew-testhost — the src/WinBrew.Cli sources built
/// framework-dependent), so FailFast crash tests are genuine (A16).
/// </summary>
public sealed class CliHarness : IDisposable
{
    public const string RepoName = "local";

    public CliHarness(Action<FixtureRepository>? build = null)
    {
        Work = Path.Combine(Path.GetTempPath(), "wbrew-it-" + Guid.NewGuid().ToString("N")[..10]);
        Root = Path.Combine(Work, "root");
        Directory.CreateDirectory(Root);
        Server = new LocalHttpServer(Path.Combine(Work, "repo"));
        Repository = new FixtureRepository(Path.Combine(Work, "repo"), Server.BaseUrl);
        (build ?? (r => r.AddHello()))(Repository);
        Repository.Publish();
    }

    public string Work { get; }

    public string Root { get; }

    public LocalHttpServer Server { get; }

    public FixtureRepository Repository { get; }

    public string Bin => Path.Combine(Root, "bin");

    public string Packages => Path.Combine(Root, "packages");

    public static string HostDll
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "wbrew-testhost.dll");
            Assert.True(File.Exists(path), $"wbrew-testhost.dll not found at {path}");
            return path;
        }
    }

    public CliRun Run(params string[] args) => RunWith(null, args);

    public CliRun RunWith(IReadOnlyDictionary<string, string?>? env, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = Work, // core dumps from FailFast land here, never in the repo
        };
        psi.ArgumentList.Add(HostDll);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["WINBREW_ROOT"] = Root;
        psi.Environment["WINBREW_TEST_MODE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment.Remove("WINBREW_FAULT_AT");
        psi.Environment.Remove("WINBREW_NONINTERACTIVE");
        if (env is not null)
        {
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
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("cannot start wbrew-testhost");
        process.StandardInput.Close(); // stdin is not a terminal → non-interactive (A8)
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("wbrew-testhost did not finish in 2 minutes");
        }

        return new CliRun(process.ExitCode, stdout.Result, stderr.Result);
    }

    public CliRun RunFault(string checkpoint, params string[] args) =>
        RunWith(new Dictionary<string, string?> { ["WINBREW_FAULT_AT"] = checkpoint }, args);

    /// <summary>Config + localhost exception + <c>repo add</c> (signed, D6) + a shim template (via the self-shim, like a real install).</summary>
    public CliHarness Ready(string template = "TEMPLATE-1")
    {
        AllowLocalhost();
        Expect(0, AddRepo(RepoName));
        SetTemplate(template);
        return this;
    }

    public void AllowLocalhost() => Expect(0, Run("config", "set", "testing.allowInsecureLocalhost", "true"));

    /// <summary><c>wbrew repo add &lt;name&gt; &lt;fixture url&gt; --trust-root …</c> with the fixture's current root fingerprints.</summary>
    public CliRun AddRepo(string name, IReadOnlyDictionary<string, string?>? env = null) =>
        RunWith(env, ["repo", "add", name, Server.BaseUrl.ToString(), .. TrustRootArgs()]);

    public string[] TrustRootArgs() => Repository.Signer.RootFingerprints.SelectMany(f => new[] { "--trust-root", f }).ToArray();

    /// <summary>Environment that shifts the CLI's clock (test-hooks builds, WINBREW_TEST_NOW).</summary>
    public static Dictionary<string, string?> At(DateTimeOffset now) =>
        new() { ["WINBREW_TEST_NOW"] = now.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>
    /// Points the self-shim sidecar at app\0.0.0-test\ and writes the shim
    /// template there — the D4 template lookup then uses it, isolated per root.
    /// </summary>
    public void SetTemplate(string content)
    {
        var app = Path.Combine(Root, "app", "0.0.0-test");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "wbrew-shim.exe"), content);
        Directory.CreateDirectory(Bin);
        var sidecar = new ShimSidecar(Path.Combine(app, "wbrew.exe"), "winbrew", "0.0.0-test", ShimSidecar.Console);
        File.WriteAllBytes(Path.Combine(Bin, "wbrew.shim.json"), sidecar.ToUtf8Bytes());
    }

    /// <summary>The plan item for <paramref name="id"/> in an install/upgrade JSON result ("data.plan.items").</summary>
    public static JsonElement PlanItem(JsonElement json, string id) =>
        json.GetProperty("data").GetProperty("plan").GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetString() == id);

    public static CliRun Expect(int code, CliRun run)
    {
        Assert.True(run.ExitCode == code, $"expected exit {code}, got:\n{run}");
        return run;
    }

    // ---- state inspection ----------------------------------------------------------------

    public sealed record InstalledView(string Version, IReadOnlyList<string> Shims);

    public InstalledView? Installed(string id = "hello")
    {
        var list = Expect(0, Run("list", "--json")).Json();
        foreach (var p in list.GetProperty("data").GetProperty("packages").EnumerateArray())
        {
            if (p.GetProperty("id").GetString() == id)
            {
                return new InstalledView(p.GetProperty("version").GetString()!,
                    p.GetProperty("shims").EnumerateArray().Select(s => s.GetString()!).ToList());
            }
        }

        return null;
    }

    public IReadOnlyList<string> ListWarnings() =>
        Expect(0, Run("list", "--json")).Json().GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();

    public IReadOnlyList<string> PackageShimFiles(string suffix) =>
        Directory.Exists(Bin)
            ? Directory.GetFiles(Bin, "*" + suffix).Select(Path.GetFileName).Select(n => n![..^suffix.Length])
                .Where(n => n != ShimSidecar.SelfShimName).OrderBy(n => n, StringComparer.Ordinal).ToList()
            : [];

    public IReadOnlyList<string> OldFiles() =>
        Directory.Exists(Bin) ? Directory.GetFiles(Bin).Where(f => f.Contains(".old.", StringComparison.Ordinal)).ToList() : [];

    public IReadOnlyList<string> StagingLeftovers() =>
        Directory.Exists(Path.Combine(Root, "staging"))
            ? Directory.GetDirectories(Path.Combine(Root, "staging")).Select(d => Path.GetFileName(d)).Where(n => n != "elevated").ToList()
            : [];

    /// <summary>
    /// The consistency invariant after recovery (A11): the database, bin\
    /// (sidecars and binaries), packages\ and staging\ agree; no .old file
    /// and no staging directory is left; no operation is pending.
    /// </summary>
    public void AssertConsistent(params string?[] allowedVersions)
    {
        Assert.Empty(ListWarnings());
        var installed = Installed();
        Assert.Contains(installed?.Version, allowedVersions);

        var expectedShims = installed?.Shims.OrderBy(s => s, StringComparer.Ordinal).ToList() ?? [];
        Assert.Equal(expectedShims, PackageShimFiles(ShimSidecar.SidecarSuffix));
        Assert.Equal(expectedShims, PackageShimFiles(".exe"));
        foreach (var name in expectedShims)
        {
            var sidecar = ShimSidecar.TryParse(File.ReadAllBytes(Path.Combine(Bin, name + ShimSidecar.SidecarSuffix)));
            Assert.NotNull(sidecar);
            Assert.Equal(installed!.Version, sidecar.Version);
            Assert.True(File.Exists(sidecar.Target), $"sidecar {name} targets a missing file {sidecar.Target}");
        }

        Assert.Empty(OldFiles());
        Assert.Empty(StagingLeftovers());
        if (installed is null)
        {
            Assert.False(Directory.Exists(Path.Combine(Packages, "hello")), "packages\\hello left behind");
        }
        else
        {
            Assert.True(Directory.Exists(Path.Combine(Packages, "hello", installed.Version, "x64")));
        }
    }

    public void Dispose()
    {
        Server.Dispose();
        try
        {
            Directory.Delete(Work, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
