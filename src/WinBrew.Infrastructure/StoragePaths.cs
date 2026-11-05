using WinBrew.Application.Ports;
using WinBrew.Core;

namespace WinBrew.Infrastructure;

/// <summary>
/// WinBrew storage layout (A6): %LOCALAPPDATA%\WinBrew, overridden by
/// WINBREW_ROOT (used by tests). The whole layout must stay on one volume so
/// renames are atomic — a configuration that would put staging\, packages\,
/// cache\ or bin\ on a different volume from the rest of the layout is
/// refused.
/// </summary>
public sealed class StoragePaths : IStorageLayout
{
    private readonly IEnvironmentProbe _probe;

    public const string RootEnvironmentVariable = "WINBREW_ROOT";

    public StoragePaths(IEnvironmentProbe probe)
    {
        _probe = probe;
        Root = ResolveRoot();
        Directories = BuildDirectories(Root);
    }

    public string Root { get; }

    public IReadOnlyDictionary<string, string> Directories { get; }

    public static string ResolveRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new WinBrewException(
                WinBrewErrorCode.GeneralFailure,
                "Cannot resolve the WinBrew root: %LOCALAPPDATA% is unavailable and " +
                $"{RootEnvironmentVariable} is not set.");
        }

        return Path.Combine(localAppData, "WinBrew");
    }

    private static Dictionary<string, string> BuildDirectories(string root) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app"] = Path.Combine(root, "app"),
            ["packages"] = Path.Combine(root, "packages"),
            ["bin"] = Path.Combine(root, "bin"),
            ["cache"] = Path.Combine(root, "cache"),
            ["cache.artifacts"] = Path.Combine(root, "cache", "artifacts"),
            ["cache.metadata"] = Path.Combine(root, "cache", "metadata"),
            ["repositories"] = Path.Combine(root, "repositories"),
            ["staging"] = Path.Combine(root, "staging"),
            ["staging.elevated"] = Path.Combine(root, "staging", "elevated"),
            ["logs"] = Path.Combine(root, "logs"),
            ["state"] = Path.Combine(root, "state"),
            ["backups"] = Path.Combine(root, "state", "backups"),
        };

    /// <summary>state\winbrew.db (A6).</summary>
    public string DatabasePath => Path.Combine(Directories["state"], "winbrew.db");

    /// <summary>state\winbrew.lock (A6/D5).</summary>
    public string LockFilePath => Path.Combine(Directories["state"], "winbrew.lock");

    /// <summary>config.json (D10, A6).</summary>
    public string ConfigFilePath => Path.Combine(Root, "config.json");

    /// <summary>Directories that must share the volume with state\ (A6).</summary>
    private static readonly string[] VolumeCritical = ["staging", "packages", "cache", "bin"];

    public Task<StorageLayoutStatus> EnsureAsync(CancellationToken ct)
    {
        var status = Inspect();
        if (status.VolumeConflicts.Count > 0)
        {
            throw new WinBrewException(
                WinBrewErrorCode.ConflictOrRefused,
                "Refusing: staging\\, packages\\, cache\\ and bin\\ must be on the same volume as " +
                "state\\ so renames stay atomic (A6). Conflicts: " +
                string.Join(", ", status.VolumeConflicts) + ".",
                new Dictionary<string, object?> { ["volumeConflicts"] = status.VolumeConflicts });
        }

        Directory.CreateDirectory(Root);
        foreach (var dir in Directories.Values)
        {
            Directory.CreateDirectory(dir);
        }

        return Task.FromResult(new StorageLayoutStatus(
            RootExists: true,
            AllDirectoriesPresent: true,
            Writable: IsWritable(Root),
            VolumeConflicts: [],
            Problem: null));
    }

    public Task<StorageLayoutStatus> InspectAsync(CancellationToken ct) =>
        Task.FromResult(Inspect());

    private StorageLayoutStatus Inspect()
    {
        var conflicts = new List<string>();
        var stateDir = Directories["state"];
        var rootExists = Directory.Exists(Root);

        if (rootExists && Directory.Exists(stateDir))
        {
            foreach (var name in VolumeCritical)
            {
                var dir = Directories[name];
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                if (!_probe.AreSameVolume(dir, stateDir))
                {
                    conflicts.Add(name);
                }
            }
        }

        var missing = Directories
            .Where(kv => Directory.Exists(Root) && !Directory.Exists(kv.Value))
            .Select(kv => kv.Key)
            .ToList();

        var problem = (string?)null;
        if (rootExists && missing.Count > 0)
        {
            problem = "Missing directories: " + string.Join(", ", missing);
        }
        else if (rootExists && !IsWritable(Root))
        {
            problem = $"Root '{Root}' is not writable.";
        }

        return new StorageLayoutStatus(
            RootExists: rootExists,
            AllDirectoriesPresent: rootExists && missing.Count == 0,
            Writable: !rootExists || IsWritable(Root),
            VolumeConflicts: conflicts,
            Problem: problem);
    }

    private static bool IsWritable(string path)
    {
        try
        {
            var probe = Path.Combine(path, ".wbrew-write-probe");
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.WriteByte(0x57);
            }

            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
