using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinBrew.RepoTool;

/// <summary>An ECDSA P-256 key and its D6 key ID (lowercase hex SHA-256 of the DER SubjectPublicKeyInfo).</summary>
internal sealed class SigningKey : IDisposable
{
    private SigningKey(ECDsa key)
    {
        Key = key;
        Spki = key.ExportSubjectPublicKeyInfo();
        Id = Convert.ToHexStringLower(SHA256.HashData(Spki));
    }

    public ECDsa Key { get; }

    public byte[] Spki { get; }

    public string Id { get; }

    /// <summary>Loads a PKCS#8 PEM key, or creates one and stores it (owner-only permissions on Unix).</summary>
    public static SigningKey LoadOrCreate(string path)
    {
        var ecdsa = ECDsa.Create();
        if (File.Exists(path))
        {
            ecdsa.ImportFromPem(File.ReadAllText(path));
            if (ecdsa.KeySize != 256)
            {
                ecdsa.Dispose();
                throw new InvalidDataException($"{path} is not a P-256 key");
            }

            return new SigningKey(ecdsa);
        }

        ecdsa.Dispose();
        ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(path, ecdsa.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return new SigningKey(ecdsa);
    }

    /// <summary>IEEE P1363 (64-byte) signature over the exact payload bytes (D6).</summary>
    public byte[] Sign(byte[] payload) =>
        Key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => Key.Dispose();
}

/// <summary>
/// The three role keys plus the signed root v1, kept in the keys directory
/// (a Docker volume) so the repository identity survives restarts. The root
/// is written once: clients pin it when they run <c>repo add</c>.
/// </summary>
internal sealed class RepositoryKeys
{
    private RepositoryKeys(SigningKey root, SigningKey timestamp, SigningKey index, byte[] rootEnvelope)
    {
        Root = root;
        Timestamp = timestamp;
        Index = index;
        RootEnvelope = rootEnvelope;
    }

    public SigningKey Root { get; }

    public SigningKey Timestamp { get; }

    public SigningKey Index { get; }

    public byte[] RootEnvelope { get; }

    public static RepositoryKeys LoadOrCreate(string directory, DateTimeOffset rootExpiresIfNew)
    {
        Directory.CreateDirectory(directory);
        var root = SigningKey.LoadOrCreate(Path.Combine(directory, "root.pem"));
        var timestamp = SigningKey.LoadOrCreate(Path.Combine(directory, "timestamp.pem"));
        var index = SigningKey.LoadOrCreate(Path.Combine(directory, "index.pem"));

        var rootPath = Path.Combine(directory, "root.1.json");
        byte[] envelope;
        if (File.Exists(rootPath))
        {
            envelope = File.ReadAllBytes(rootPath);
        }
        else
        {
            var keys = new JsonObject();
            foreach (var k in new[] { root, timestamp, index })
            {
                keys[k.Id] = new JsonObject { ["keytype"] = "ecdsa", ["scheme"] = "ecdsa-sha256-nistp256", ["public"] = Convert.ToBase64String(k.Spki) };
            }

            var payload = new JsonObject
            {
                ["version"] = 1,
                ["expires"] = RepositoryBuilder.Iso(rootExpiresIfNew),
                ["keys"] = keys,
                ["roles"] = new JsonObject
                {
                    ["root"] = Role(root),
                    ["timestamp"] = Role(timestamp),
                    ["index"] = Role(index),
                },
            };
            envelope = Envelope.Sign(JsonSerializer.SerializeToUtf8Bytes(payload), Envelope.RootType, [root]);
            File.WriteAllBytes(rootPath, envelope);
        }

        File.WriteAllText(Path.Combine(directory, "fingerprint.txt"), root.Id + Environment.NewLine);
        return new RepositoryKeys(root, timestamp, index, envelope);
    }

    private static JsonObject Role(SigningKey key) => new() { ["threshold"] = 1, ["keyids"] = new JsonArray(key.Id) };
}

/// <summary>D6 signed envelope: base64 payload, payload type, signatures.</summary>
internal static class Envelope
{
    public const string RootType = "application/vnd.winbrew.root+json;version=1";
    public const string TimestampType = "application/vnd.winbrew.timestamp+json;version=1";
    public const string IndexType = "application/vnd.winbrew.index+json;version=1";

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] Sign(byte[] payload, string payloadType, IEnumerable<SigningKey> signers) =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["payload"] = Convert.ToBase64String(payload),
            ["payloadType"] = payloadType,
            ["signatures"] = new JsonArray(signers.Select(k => (JsonNode)new JsonObject
            {
                ["keyid"] = k.Id,
                ["sig"] = Convert.ToBase64String(k.Sign(payload)),
            }).ToArray()),
        }, Json);
}
