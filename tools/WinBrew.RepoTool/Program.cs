using System.Globalization;

namespace WinBrew.RepoTool;

/// <summary>
/// winbrew-repo build --packages DIR --keys DIR --out DIR --base-url URL [--every HOURS]
///
/// Every option also reads an environment variable (WINBREW_REPO_PACKAGES,
/// WINBREW_REPO_KEYS, WINBREW_REPO_OUT, WINBREW_REPO_BASE_URL,
/// WINBREW_REPO_EVERY_HOURS) so the container needs no shell.
/// With --every, the repository is re-signed periodically: timestamp.json may
/// live at most 7 days and index.json at most 30 (D6), so a server that stops
/// re-signing becomes unusable for clients within a week.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is not "build")
        {
            await Console.Error.WriteLineAsync(
                "usage: winbrew-repo build --packages DIR --keys DIR --out DIR --base-url https://host[:port]/ [--every HOURS]");
            return 2;
        }

        BuildOptions options;
        try
        {
            options = BuildOptions.Parse(args.AsSpan(1));
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync("error: " + ex.Message);
            return 2;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

        while (true)
        {
            try
            {
                var result = RepositoryBuilder.Build(options, DateTimeOffset.UtcNow);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}] published {result.Packages} package version(s), index version {result.IndexVersion}, to {options.Out}"));
                Console.WriteLine("root fingerprint (give to clients: wbrew repo add <name> <url> --trust-root <fingerprint>):");
                foreach (var fingerprint in result.RootFingerprints)
                {
                    Console.WriteLine("  " + fingerprint);
                }
            }
            catch (InvalidDataException ex)
            {
                await Console.Error.WriteLineAsync("error: " + ex.Message);
                if (options.EveryHours is null)
                {
                    return 1;
                }
            }

            if (options.EveryHours is not { } hours)
            {
                return 0;
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(hours), stop.Token);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }
    }
}

internal sealed record BuildOptions(string Packages, string Keys, string Out, Uri BaseUrl, double? EveryHours)
{
    public static BuildOptions Parse(ReadOnlySpan<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException($"unexpected argument '{args[i]}'");
            }

            values[args[i][2..]] = args[++i];
        }

        string Get(string name, string env) =>
            values.TryGetValue(name, out var v) ? v
            : Environment.GetEnvironmentVariable(env) is { Length: > 0 } e ? e
            : throw new ArgumentException($"--{name} (or {env}) is required");

        var baseText = Get("base-url", "WINBREW_REPO_BASE_URL");
        if (!baseText.EndsWith('/'))
        {
            baseText += "/";
        }

        if (!Uri.TryCreate(baseText, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme != Uri.UriSchemeHttps)
        {
            // Release clients download only over HTTPS (A13). Plain http is
            // accepted for loopback so the tool can be tested locally.
            if (baseUrl is null || !(baseUrl.Scheme == Uri.UriSchemeHttp && baseUrl.IsLoopback))
            {
                throw new ArgumentException($"--base-url must be an https:// URL (got '{baseText}')");
            }
        }

        double? every = null;
        var everyText = values.TryGetValue("every", out var ev) ? ev : Environment.GetEnvironmentVariable("WINBREW_REPO_EVERY_HOURS");
        if (!string.IsNullOrWhiteSpace(everyText))
        {
            if (!double.TryParse(everyText, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || h <= 0 || h > 24 * 6)
            {
                throw new ArgumentException("--every must be between 0 and 144 hours (timestamp.json expires after 7 days)");
            }

            every = h;
        }

        return new BuildOptions(
            Path.GetFullPath(Get("packages", "WINBREW_REPO_PACKAGES")),
            Path.GetFullPath(Get("keys", "WINBREW_REPO_KEYS")),
            Path.GetFullPath(Get("out", "WINBREW_REPO_OUT")),
            baseUrl,
            every);
    }
}
