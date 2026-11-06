using WinBrew.Application.Ports;
using WinBrew.Core.Archive;
using WinBrew.Core.Config;
using WinBrew.Core.Net;

namespace WinBrew.Application.Lifecycle;

/// <summary>Build/environment flags decided by the composition root (A11/A13).</summary>
/// <param name="TestHooksBuild">Compiled with -p:WinBrewTestHooks=true.</param>
/// <param name="TestModeEnvironment">WINBREW_TEST_MODE=1 is set.</param>
public sealed record RuntimeFlags(bool TestHooksBuild, bool TestModeEnvironment);

/// <summary>Effective D10 values used by the lifecycle, read once per command.</summary>
public sealed record LifecycleSettings(
    TimeSpan LockTimeout,
    TimeSpan DownloadTimeout,
    long DownloadMaxBytes,
    int DownloadRetries,
    long MaxMetadataBytes,
    int RetainVersions,
    ArchiveLimits ArchiveLimits,
    InsecureLocalhostPolicy Localhost,
    bool PathManaged,
    bool InteractiveNever,
    TimeSpan MaxClockSkew)
{
    public static async Task<LifecycleSettings> LoadAsync(IConfigStore store, RuntimeFlags flags, CancellationToken ct)
    {
        var doc = await store.LoadAsync(ct);

        T Get<T>(string key)
        {
            if (doc.Values.TryGetValue(key, out var value) && value is T typed)
            {
                return typed;
            }

            return (T)ConfigDefinitions.Keys[key].Default;
        }

        return new LifecycleSettings(
            LockTimeout: TimeSpan.FromSeconds(Get<int>("lock.timeoutSeconds")),
            DownloadTimeout: TimeSpan.FromSeconds(Get<int>("download.timeoutSeconds")),
            DownloadMaxBytes: Get<long>("download.maxBytes"),
            DownloadRetries: Get<int>("download.retries"),
            MaxMetadataBytes: Get<long>("trust.maxMetadataBytes"),
            RetainVersions: Get<int>("retainVersions"),
            ArchiveLimits: new ArchiveLimits(
                Get<int>("archive.maxEntries"),
                Get<long>("archive.maxTotalBytes"),
                Get<int>("archive.maxCompressionRatio")),
            Localhost: new InsecureLocalhostPolicy(
                flags.TestHooksBuild,
                flags.TestHooksBuild && Get<bool>("testing.allowInsecureLocalhost"),
                flags.TestModeEnvironment),
            PathManaged: Get<bool>("path.managed"),
            InteractiveNever: string.Equals(Get<string>("interactive"), "never", StringComparison.Ordinal),
            MaxClockSkew: TimeSpan.FromSeconds(Get<int>("trust.maxClockSkewSeconds")));
    }
}
