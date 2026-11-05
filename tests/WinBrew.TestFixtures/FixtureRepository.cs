using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinBrew.TestFixtures;

/// <summary>Minimal PE images built in memory (DOS header, PE signature, COFF, PE32+ optional header).</summary>
public static class PeBytes
{
    public const ushort Amd64 = 0x8664;
    public const ushort I386 = 0x014C;
    public const ushort Arm64 = 0xAA64;
    public const ushort Arm32 = 0x01C4;

    public static byte[] Create(ushort machine = Amd64, ushort subsystem = 3)
    {
        var b = new byte[0x40 + 4 + 20 + 0xF0];
        b[0] = (byte)'M';
        b[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x3C), 0x40);
        b[0x40] = (byte)'P';
        b[0x41] = (byte)'E';
        const int coff = 0x44;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(coff), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(coff + 16), 0xF0);
        const int optional = coff + 20;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(optional), 0x20B);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(optional + 68), subsystem);
        return b;
    }
}

/// <summary>Builds ZIP archives in memory.</summary>
public static class ZipBytes
{
    public static byte[] Create(IEnumerable<(string Name, byte[] Data)> entries, Action<ZipArchiveEntry>? tweak = null,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name, level);
                tweak?.Invoke(entry);
                if (!name.EndsWith('/'))
                {
                    using var stream = entry.Open();
                    stream.Write(data);
                }
            }
        }

        return buffer.ToArray();
    }

    public static byte[] Create(params (string Name, string Text)[] entries) =>
        Create(entries.Select(e => (e.Name, System.Text.Encoding.UTF8.GetBytes(e.Text))));
}

/// <summary>One package version in a fixture repository.</summary>
public sealed record FixturePackage(
    string Id,
    string Version,
    IReadOnlyList<(string Name, byte[] Data)> Files,
    IReadOnlyList<(string Name, string Target)> Executables,
    string Architecture = "x64",
    IReadOnlyList<(string Id, string Range)>? Dependencies = null,
    bool WritesToInstallDir = false,
    byte[]? ArchiveOverride = null);

/// <summary>
/// A locally built repository (A16: fixtures built at test time): artifacts,
/// manifests and an UNSIGNED index.json (Phase B ingest path) in a directory
/// served by <see cref="LocalHttpServer"/>. Example manifests point only at
/// the local server (A9).
/// </summary>
public sealed class FixtureRepository
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly List<FixturePackage> _packages = [];

    public FixtureRepository(string directory, Uri baseUrl)
    {
        Directory = directory;
        BaseUrl = baseUrl;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    public Uri BaseUrl { get; }

    public int IndexVersion { get; set; } = 1;

    public DateTimeOffset Expires { get; set; } = DateTimeOffset.UtcNow.AddDays(7);

    public FixtureRepository Add(FixturePackage package)
    {
        _packages.Add(package);
        return this;
    }

    /// <summary>The common two-version fixture: hello 1.0.0 (one console exe) and 2.0.0 (plus a GUI exe).</summary>
    public FixtureRepository AddHello()
    {
        Add(new FixturePackage("hello", "1.0.0",
            [("bin/hello.exe", PeBytes.Create()), ("README.txt", "v1"u8.ToArray())],
            [("hello", "bin/hello.exe")]));
        Add(new FixturePackage("hello", "2.0.0",
            [("bin/hello.exe", PeBytes.Create()), ("bin/hi.exe", PeBytes.Create(subsystem: 2)), ("README.txt", "v2"u8.ToArray())],
            [("hello", "bin/hello.exe"), ("hi", "bin/hi.exe")]));
        return this;
    }

    public string ArtifactPath(FixturePackage p) => Path.Combine(Directory, "artifacts", $"{p.Id}-{p.Version}-{p.Architecture}.zip");

    public string ManifestPath(string id, string version) => Path.Combine(Directory, "manifests", id, version + ".json");

    public string IndexPath => Path.Combine(Directory, "index.json");

    /// <summary>Writes every artifact, manifest and index.json. Returns the index bytes.</summary>
    public byte[] Publish()
    {
        var index = new JsonObject
        {
            ["version"] = IndexVersion,
            ["expires"] = Expires.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["packages"] = new JsonObject(),
        };
        var packages = (JsonObject)index["packages"]!;

        foreach (var group in _packages.GroupBy(p => (p.Id, p.Version)))
        {
            var artifacts = new JsonArray();
            foreach (var p in group)
            {
                var data = p.ArchiveOverride ?? ZipBytes.Create(p.Files);
                var path = ArtifactPath(p);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
                artifacts.Add(new JsonObject
                {
                    ["architecture"] = p.Architecture,
                    ["url"] = new Uri(BaseUrl, $"artifacts/{Path.GetFileName(path)}").ToString(),
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(data)),
                    ["size"] = data.LongLength,
                    ["format"] = "zip",
                    ["installType"] = "portable",
                });
            }

            var first = group.First();
            var dependencies = new JsonArray((first.Dependencies ?? []).Select(d => (JsonNode)new JsonObject { ["id"] = d.Id, ["range"] = d.Range }).ToArray());
            var manifest = new JsonObject
            {
                ["schemaVersion"] = "1",
                ["id"] = first.Id,
                ["version"] = first.Version,
                ["displayName"] = first.Id,
                ["description"] = $"{first.Id} fixture",
                ["homepage"] = "https://example.org/",
                ["license"] = "MIT",
                ["scope"] = new JsonArray("user"),
                ["writesToInstallDir"] = first.WritesToInstallDir,
                ["artifacts"] = artifacts,
                ["dependencies"] = dependencies,
                ["executables"] = new JsonArray(first.Executables.Select(e => (JsonNode)new JsonObject { ["name"] = e.Name, ["target"] = e.Target }).ToArray()),
            };
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Indented);
            var manifestPath = ManifestPath(first.Id, first.Version);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            File.WriteAllBytes(manifestPath, manifestBytes);

            if (packages[first.Id] is not JsonObject entry)
            {
                entry = new JsonObject { ["displayName"] = first.Id, ["description"] = $"{first.Id} fixture", ["versions"] = new JsonObject() };
                packages[first.Id] = entry;
            }

            ((JsonObject)entry["versions"]!)[first.Version] = new JsonObject
            {
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
                ["size"] = manifestBytes.LongLength,
                ["architectures"] = new JsonArray(group.Select(p => (JsonNode)p.Architecture).ToArray()),
                ["dependencies"] = dependencies.DeepClone(),
            };
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(index, Indented);
        File.WriteAllBytes(IndexPath, bytes);
        return bytes;
    }
}
