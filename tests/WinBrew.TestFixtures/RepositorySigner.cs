using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinBrew.TestFixtures;

/// <summary>An ECDSA test key with its DER SPKI and D6 key ID. TEST KEYS ONLY.</summary>
public sealed class SigningKey : IDisposable
{
    private SigningKey(ECDsa key, byte[]? spkiOverride = null)
    {
        Key = key;
        Spki = spkiOverride ?? key.ExportSubjectPublicKeyInfo();
        Id = Convert.ToHexStringLower(SHA256.HashData(Spki));
    }

    public ECDsa Key { get; }

    public byte[] Spki { get; }

    public string Id { get; }

    public static SigningKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static SigningKey Create(ECCurve curve) => new(ECDsa.Create(curve));

    /// <summary>A P-256 key whose advertised SPKI is replaced (explicit parameters, compressed point …).</summary>
    public static SigningKey WithSpki(byte[] spki) => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), spki);

    public byte[] Sign(byte[] payload) =>
        Key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => Key.Dispose();
}

/// <summary>One signature as written into an envelope (tests can corrupt it).</summary>
public sealed record FixtureSignature(string KeyId, byte[] Signature);

/// <summary>
/// Builds D6 metadata for fixture repositories and the self-update feed:
/// root (with history root/&lt;N&gt;.json and N→N+1 rotation), timestamp, index.
/// Signatures cover the exact payload bytes (IEEE P1363, 64 bytes). Mirrors
/// scripts/new-fixture-repo.ps1.
/// </summary>
public sealed class RepositorySigner
{
    public const string RootType = "application/vnd.winbrew.root+json;version=1";
    public const string TimestampType = "application/vnd.winbrew.timestamp+json;version=1";
    public const string IndexType = "application/vnd.winbrew.index+json;version=1";
    public const string ReleasesType = "application/vnd.winbrew.releases+json;version=1";

    /// <summary>Base64 '+' and '/' stay literal, so re-signed envelopes keep their length.</summary>
    private static readonly JsonSerializerOptions EnvelopeJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly SortedDictionary<int, byte[]> _roots = [];

    public RepositorySigner(int rootKeys = 1, int rootThreshold = 1)
    {
        RootKeys = [.. Enumerable.Range(0, rootKeys).Select(_ => SigningKey.Create())];
        RootThreshold = rootThreshold;
        TimestampKey = SigningKey.Create();
        IndexKey = SigningKey.Create();
        _roots[1] = BuildRoot(1, RootKeys, RootKeys.Take(rootThreshold));
    }

    public List<SigningKey> RootKeys { get; private set; }

    public int RootThreshold { get; private set; }

    public SigningKey TimestampKey { get; set; }

    public SigningKey IndexKey { get; set; }

    public int RootVersion => _roots.Keys.Max();

    public DateTimeOffset RootExpires { get; set; } = DateTimeOffset.UtcNow.AddDays(300);

    /// <summary>The root envelopes by version (root/&lt;N&gt;.json); the highest is root.json.</summary>
    public IReadOnlyDictionary<int, byte[]> Roots => _roots;

    public byte[] CurrentRoot => _roots[RootVersion];

    /// <summary>The current root role's key IDs = the fingerprints for <c>--trust-root</c>.</summary>
    public IReadOnlyList<string> RootFingerprints => RootKeys.Select(k => k.Id).Order(StringComparer.Ordinal).ToList();

    /// <summary>Re-signs root v1 with its own keys (e.g. after changing the timestamp/index keys).</summary>
    public void ResignInitialRoot()
    {
        if (RootVersion != 1)
        {
            throw new InvalidOperationException("Only root v1 can be re-signed in place; rotate instead.");
        }

        _roots[1] = BuildRoot(1, RootKeys, RootKeys.Take(RootThreshold));
    }

    /// <summary>
    /// Rotates root N → N+1 with fresh root keys. <paramref name="crossSign"/> = signed by a
    /// threshold of root N (D6 requires it); <paramref name="selfSign"/> = signed by its own keys.
    /// </summary>
    public void RotateRoot(int newKeys = 1, int threshold = 1, bool crossSign = true, bool selfSign = true)
    {
        var old = RootKeys.Take(RootThreshold).ToList();
        var fresh = Enumerable.Range(0, newKeys).Select(_ => SigningKey.Create()).ToList();
        var signers = new List<SigningKey>();
        if (crossSign)
        {
            signers.AddRange(old);
        }

        if (selfSign)
        {
            signers.AddRange(fresh.Take(threshold));
        }

        var version = RootVersion + 1;
        RootKeys = fresh;
        RootThreshold = threshold;
        _roots[version] = BuildRoot(version, fresh, signers);
    }

    public byte[] RootPayload(int version, IReadOnlyList<SigningKey> rootKeys, int? threshold = null, DateTimeOffset? expires = null)
    {
        var keys = new JsonObject();
        foreach (var k in rootKeys.Concat([TimestampKey, IndexKey]).DistinctBy(k => k.Id))
        {
            keys[k.Id] = new JsonObject { ["keytype"] = "ecdsa", ["scheme"] = "ecdsa-sha256-nistp256", ["public"] = Convert.ToBase64String(k.Spki) };
        }

        var root = new JsonObject
        {
            ["version"] = version,
            ["expires"] = Iso(expires ?? RootExpires),
            ["keys"] = keys,
            ["roles"] = new JsonObject
            {
                ["root"] = Role(threshold ?? RootThreshold, rootKeys),
                ["timestamp"] = Role(1, [TimestampKey]),
                ["index"] = Role(1, [IndexKey]),
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    public static byte[] TimestampPayload(int version, DateTimeOffset expires, byte[] indexEnvelope) =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["version"] = version,
            ["expires"] = Iso(expires),
            ["meta"] = new JsonObject
            {
                ["index.json"] = new JsonObject
                {
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(indexEnvelope)),
                    ["length"] = indexEnvelope.LongLength,
                },
            },
        });

    public byte[] SignTimestamp(int version, DateTimeOffset expires, byte[] indexEnvelope) =>
        Envelope(TimestampPayload(version, expires, indexEnvelope), TimestampType, [TimestampKey]);

    public byte[] SignIndex(byte[] payload, string payloadType = IndexType) => Envelope(payload, payloadType, [IndexKey]);

    /// <summary>A D6 envelope signed by <paramref name="signers"/> (in order).</summary>
    public static byte[] Envelope(byte[] payload, string payloadType, IEnumerable<SigningKey> signers) =>
        RawEnvelope(payload, payloadType, signers.Select(k => new FixtureSignature(k.Id, k.Sign(payload))));

    /// <summary>An envelope with arbitrary (possibly corrupt) signatures.</summary>
    public static byte[] RawEnvelope(byte[] payload, string payloadType, IEnumerable<FixtureSignature> signatures) =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["payload"] = Convert.ToBase64String(payload),
            ["payloadType"] = payloadType,
            ["signatures"] = new JsonArray(signatures.Select(s => (JsonNode)new JsonObject
            {
                ["keyid"] = s.KeyId,
                ["sig"] = Convert.ToBase64String(s.Signature),
            }).ToArray()),
        }, EnvelopeJson);

    /// <summary>Writes root.json (= the newest root) and root/&lt;N&gt;.json for every version.</summary>
    public void WriteRoots(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "root"));
        foreach (var (version, bytes) in _roots)
        {
            File.WriteAllBytes(Path.Combine(directory, "root", $"{version}.json"), bytes);
        }

        File.WriteAllBytes(Path.Combine(directory, "root.json"), CurrentRoot);
    }

    public static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private byte[] BuildRoot(int version, IReadOnlyList<SigningKey> rootKeys, IEnumerable<SigningKey> signers) =>
        Envelope(RootPayload(version, rootKeys), RootType, signers.DistinctBy(k => k.Id));

    private static JsonObject Role(int threshold, IEnumerable<SigningKey> keys) => new()
    {
        ["threshold"] = threshold,
        ["keyids"] = new JsonArray(keys.Select(k => (JsonNode)k.Id).ToArray()),
    };
}
