using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Shims;

namespace WinBrew.Application.Lifecycle;

/// <summary>The ports every lifecycle use case and recovery share (one DI registration).</summary>
public sealed record LifecycleServices(
    IStorageLayout Layout,
    IOperationJournal Journal,
    IFaultInjector Faults,
    IPackageFileSystem Files,
    IShimStore Shims,
    IInstalledStore Installed,
    ICatalogStore Catalog,
    IArtifactCache Cache,
    IArchiveExtractor Extractor,
    IHttpFetcher Http,
    IUserPathEditor UserPath,
    IConfigStore Config,
    ISelfLocation Self,
    IEnvironmentProbe Probe,
    RuntimeFlags Flags,
    TimeProvider Time);

/// <summary>A6 paths used by the lifecycle.</summary>
public static class LifecyclePaths
{
    public static string PackagesRoot(IStorageLayout layout) => layout.Directories["packages"];

    /// <summary>packages\&lt;id&gt;\ (all versions of one package).</summary>
    public static string PackageRoot(IStorageLayout layout, string id) => Path.Combine(PackagesRoot(layout), id);

    /// <summary>packages\&lt;id&gt;\&lt;version&gt;\&lt;arch&gt;\ (A6).</summary>
    public static string PackageDirectory(IStorageLayout layout, string id, string version, string arch) =>
        Path.Combine(PackagesRoot(layout), id, version, arch);

    /// <summary>staging\&lt;operation-id&gt;\ (A6: same volume as packages\).</summary>
    public static string StagingDirectory(IStorageLayout layout, string opId) => Path.Combine(layout.Directories["staging"], opId);

    /// <summary>cache\metadata\&lt;repo&gt;\manifests\&lt;id&gt;\&lt;version&gt;.json — verified manifests (A6).</summary>
    public static string CachedManifest(IStorageLayout layout, string repo, string id, string version) =>
        Path.Combine(layout.Directories["cache.metadata"], repo, "manifests", id, version + ".json");

    public static string AppDirectory(IStorageLayout layout, string version) => Path.Combine(layout.Directories["app"], version);

    public const string ShimTemplateFileName = "wbrew-shim.exe";

    /// <summary>
    /// The active shim template app\&lt;active-ver&gt;\wbrew-shim.exe (D4):
    /// the running wbrew's own directory when it runs from app\, else the
    /// directory the wbrew self-shim points at, else (an uninstalled
    /// development build) the running executable's directory.
    /// </summary>
    public static async Task<string> FindShimTemplateAsync(LifecycleServices s, CancellationToken ct)
    {
        var appRoot = s.Layout.Directories["app"];
        var selfDir = Path.GetFullPath(s.Self.ExecutableDirectory);

        if (Core.Archive.ArchiveEntryRules.IsContained(Path.GetFullPath(appRoot), selfDir, ignoreCase: OperatingSystem.IsWindows()))
        {
            var candidate = Path.Combine(selfDir, ShimTemplateFileName);
            if (s.Files.FileExists(candidate))
            {
                return candidate;
            }
        }

        var sidecarBytes = await s.Shims.ReadSidecarAsync(ShimSidecar.SelfShimName, ct);
        if (sidecarBytes is not null && ShimSidecar.TryParse(sidecarBytes) is { } sidecar)
        {
            var dir = Path.GetDirectoryName(sidecar.Target);
            if (dir is not null)
            {
                var candidate = Path.Combine(dir, ShimTemplateFileName);
                if (s.Files.FileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        var local = Path.Combine(selfDir, ShimTemplateFileName);
        if (s.Files.FileExists(local))
        {
            return local;
        }

        throw new WinBrewException(
            WinBrewErrorCode.GeneralFailure,
            $"The shim template ({ShimTemplateFileName}) was not found next to wbrew or under app\\. " +
            "Run 'wbrew self install' from the extracted release zip first (A14, D4).");
    }

    /// <summary>Host architecture for selection (A10): native machine, falling back to OSArchitecture.</summary>
    public static System.Runtime.InteropServices.Architecture HostArchitecture(IEnvironmentProbe probe) =>
        probe.NativeMachine ?? System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;

    public static string Now(TimeProvider time) =>
        time.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
