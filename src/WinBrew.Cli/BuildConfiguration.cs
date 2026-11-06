using System.Globalization;
using System.Reflection;
using WinBrew.Application.Ports;

namespace WinBrew.Cli;

/// <summary>
/// The self-update feed compiled into the build (A14):
/// <c>-p:WinBrewReleaseFeedUrl=https://… -p:WinBrewReleaseRoot=&lt;base64 root.json envelope&gt;</c>
/// become assembly metadata. Official builds set both once release
/// infrastructure exists; until then <c>self update</c> exits 3 ("self-update
/// not configured for this build"). TEST-HOOKS builds with WINBREW_TEST_MODE=1
/// may instead name a fixture feed (WINBREW_TEST_RELEASE_FEED) and its
/// initial root file (WINBREW_TEST_RELEASE_ROOT) — compiled out of release
/// builds like every other test-only behavior.
/// </summary>
internal sealed class CliReleaseFeed : IReleaseFeed
{
    public CliReleaseFeed(bool testModeEnvironment)
    {
        Configuration = FromTestHooks(testModeEnvironment) ?? FromAssembly();
    }

    public ReleaseFeedConfiguration? Configuration { get; }

    private static ReleaseFeedConfiguration? FromAssembly()
    {
        var metadata = typeof(CliReleaseFeed).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        if (!metadata.TryGetValue("WinBrewReleaseFeedUrl", out var url) || string.IsNullOrWhiteSpace(url) ||
            !metadata.TryGetValue("WinBrewReleaseRoot", out var root) || string.IsNullOrWhiteSpace(root) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new ReleaseFeedConfiguration(uri, Convert.FromBase64String(root));
    }

    private static ReleaseFeedConfiguration? FromTestHooks(bool testModeEnvironment)
    {
#if WINBREW_TEST_HOOKS
        var url = Environment.GetEnvironmentVariable("WINBREW_TEST_RELEASE_FEED");
        var root = Environment.GetEnvironmentVariable("WINBREW_TEST_RELEASE_ROOT");
        if (testModeEnvironment && Uri.TryCreate(url, UriKind.Absolute, out var uri) && root is not null && File.Exists(root))
        {
            return new ReleaseFeedConfiguration(uri, File.ReadAllBytes(root));
        }
#else
        _ = testModeEnvironment;
#endif
        return null;
    }
}

/// <summary>Clock construction: the system clock, or (TEST-HOOKS + WINBREW_TEST_MODE=1 only) a shifted one.</summary>
internal static class CliClock
{
    /// <summary>
    /// WINBREW_TEST_NOW=&lt;ISO-8601&gt; shifts the clock so expiry, offline age and
    /// freeze-attack tests run deterministically against real child processes.
    /// Compiled out of release builds.
    /// </summary>
    public static TimeProvider Create(bool testModeEnvironment)
    {
#if WINBREW_TEST_HOOKS
        var now = Environment.GetEnvironmentVariable("WINBREW_TEST_NOW");
        if (testModeEnvironment && DateTimeOffset.TryParse(now, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var shifted))
        {
            return new ShiftedTimeProvider(shifted - DateTimeOffset.UtcNow);
        }
#else
        _ = testModeEnvironment;
#endif
        return TimeProvider.System;
    }

#if WINBREW_TEST_HOOKS
    private sealed class ShiftedTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + offset;
    }
#endif
}
