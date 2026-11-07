using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinBrew.RepoTool;

internal sealed record BuildResult(int Packages, long IndexVersion, IReadOnlyList<string> RootFingerprints);

/// <summary>
/// Turns a package folder tree into a signed D6 repository:
///
///   packages/&lt;id&gt;/&lt;version&gt;/package.json   metadata (see deploy/README.md)
///   packages/&lt;id&gt;/&lt;version&gt;/files/...      the files to ship (zipped as-is)
///
/// Output layout (what the client reads): root.json, root/1.json,
/// timestamp.json, index.json, manifests/&lt;id&gt;/&lt;version&gt;.json and
/// artifacts/&lt;id&gt;-&lt;version&gt;-&lt;arch&gt;.zip.
///
/// ZIPs are deterministic (sorted entries, fixed timestamps), so re-running
/// the build never changes the digest of an already published version.
/// index/timestamp versions are Unix seconds, so every run moves them forward
/// (clients reject rollbacks).
/// </summary>
internal static class RepositoryBuilder
{
    // D6 maximum lifetimes are 365 / 7 / 30 days (root / timestamp / index).
    private static readonly TimeSpan RootLifetime = TimeSpan.FromDays(360);
    private static readonly TimeSpan TimestampLifetime = TimeSpan.FromDays(6);
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromDays(29);
    private static readonly DateTimeOffset ZipEntryTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static BuildResult Build(BuildOptions options, DateTimeOffset now)
    {
        var keys = RepositoryKeys.LoadOrCreate(options.Keys, now + RootLifetime);
        Directory.CreateDirectory(options.Out);

        var packages = new JsonObject();
        var count = 0;
        foreach (var idDir in Directory.GetDirectories(options.Packages).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(idDir);
            // Oldest first, so the newest version's displayName/description end up in the index entry.
            foreach (var versionDir in Directory.GetDirectories(idDir).OrderBy(d => Path.GetFileName(d), VersionOrder.Instance))
            {
                AddVersion(options, id, Path.GetFileName(versionDir), versionDir, packages);
                count++;
            }
        }

        if (count == 0)
        {
            throw new InvalidDataException($"no packages found under {options.Packages} (expected <id>/<version>/package.json)");
        }

        var version = now.ToUnixTimeSeconds();
        var index = new JsonObject
        {
            ["version"] = version,
            ["expires"] = Iso(now + IndexLifetime),
            ["packages"] = packages,
        };
        var indexEnvelope = Envelope.Sign(JsonSerializer.SerializeToUtf8Bytes(index, Indented), Envelope.IndexType, [keys.Index]);

        var timestamp = new JsonObject
        {
            ["version"] = version,
            ["expires"] = Iso(now + TimestampLifetime),
            ["meta"] = new JsonObject
            {
                ["index.json"] = new JsonObject
                {
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(indexEnvelope)),
                    ["length"] = indexEnvelope.LongLength,
                },
            },
        };
        var timestampEnvelope = Envelope.Sign(JsonSerializer.SerializeToUtf8Bytes(timestamp), Envelope.TimestampType, [keys.Timestamp]);

        // Order matters for clients refreshing mid-publish: artifacts and
        // manifests are already in place; the index comes before the
        // timestamp that binds it; each file is replaced atomically.
        WriteAtomic(Path.Combine(options.Out, "index.json"), indexEnvelope);
        WriteAtomic(Path.Combine(options.Out, "timestamp.json"), timestampEnvelope);
        Directory.CreateDirectory(Path.Combine(options.Out, "root"));
        WriteAtomic(Path.Combine(options.Out, "root", "1.json"), keys.RootEnvelope);
        WriteAtomic(Path.Combine(options.Out, "root.json"), keys.RootEnvelope);

        return new BuildResult(count, version, [keys.Root.Id]);
    }

    private static void AddVersion(BuildOptions options, string id, string version, string versionDir, JsonObject packages)
    {
        var specPath = Path.Combine(versionDir, "package.json");
        if (!File.Exists(specPath))
        {
            throw new InvalidDataException($"{specPath} is missing");
        }

        JsonObject spec;
        try
        {
            spec = JsonNode.Parse(File.ReadAllBytes(specPath)) as JsonObject
                ?? throw new InvalidDataException($"{specPath} must contain a JSON object");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{specPath}: {ex.Message}", ex);
        }

        string Text(string name) => spec[name]?.GetValue<string>() is { Length: > 0 } v
            ? v : throw new InvalidDataException($"{specPath}: \"{name}\" is required");

        var architecture = spec["architecture"]?.GetValue<string>() ?? "x64";
        var filesDir = Path.Combine(versionDir, "files");
        if (!Directory.Exists(filesDir))
        {
            throw new InvalidDataException($"{filesDir} is missing (the files to ship)");
        }

        var zip = DeterministicZip(filesDir);
        var artifactName = $"{id}-{version}-{architecture}.zip";
        var artifactPath = Path.Combine(options.Out, "artifacts", artifactName);
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        WriteAtomic(artifactPath, zip);

        var dependencies = spec["dependencies"]?.DeepClone() as JsonArray ?? [];
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1",
            ["id"] = id,
            ["version"] = version,
            ["displayName"] = Text("displayName"),
            ["description"] = Text("description"),
            ["homepage"] = Text("homepage"),
            ["license"] = Text("license"),
            ["scope"] = new JsonArray("user"),
            ["writesToInstallDir"] = spec["writesToInstallDir"]?.GetValue<bool>() ?? false,
            ["artifacts"] = new JsonArray(new JsonObject
            {
                ["architecture"] = architecture,
                ["url"] = new Uri(options.BaseUrl, "artifacts/" + artifactName).ToString(),
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(zip)),
                ["size"] = zip.LongLength,
                ["format"] = "zip",
                ["installType"] = "portable",
            }),
            ["dependencies"] = dependencies,
            ["executables"] = spec["executables"]?.DeepClone() as JsonArray
                ?? throw new InvalidDataException($"{specPath}: \"executables\" is required"),
        };

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Indented);
        var manifestPath = Path.Combine(options.Out, "manifests", id, version + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        WriteAtomic(manifestPath, manifestBytes);

        if (packages[id] is not JsonObject entry)
        {
            entry = new JsonObject { ["versions"] = new JsonObject() };
            packages[id] = entry;
        }

        entry["displayName"] = Text("displayName");
        entry["description"] = Text("description");

        ((JsonObject)entry["versions"]!)[version] = new JsonObject
        {
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            ["size"] = manifestBytes.LongLength,
            ["architectures"] = new JsonArray(architecture),
            ["dependencies"] = dependencies.DeepClone(),
        };
    }

    /// <summary>Sorted entries, '/' separators, fixed timestamps: the same files always give the same bytes.</summary>
    internal static byte[] DeterministicZip(string directory)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Select(f => (Full: f, Name: Path.GetRelativePath(directory, f).Replace('\\', '/')))
                .OrderBy(f => f.Name, StringComparer.Ordinal);
            foreach (var (full, name) in files)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = ZipEntryTime;
                using var target = entry.Open();
                using var source = File.OpenRead(full);
                source.CopyTo(target);
            }
        }

        return buffer.ToArray();
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
        {
            return;
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    internal static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

/// <summary>Orders "1.10.0" after "1.9.0": numeric dot-separated parts, then any pre-release suffix (which sorts first).</summary>
internal sealed class VersionOrder : IComparer<string>
{
    public static readonly VersionOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        var (xCore, xPre) = Split(x ?? string.Empty);
        var (yCore, yPre) = Split(y ?? string.Empty);
        var xs = xCore.Split('.');
        var ys = yCore.Split('.');
        for (var i = 0; i < Math.Max(xs.Length, ys.Length); i++)
        {
            var a = i < xs.Length && long.TryParse(xs[i], NumberStyles.None, CultureInfo.InvariantCulture, out var av) ? av : 0;
            var b = i < ys.Length && long.TryParse(ys[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bv) ? bv : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        return (xPre, yPre) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(xPre, yPre),
        };
    }

    private static (string Core, string? Pre) Split(string version)
    {
        var dash = version.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? (version, null) : (version[..dash], version[(dash + 1)..]);
    }
}
