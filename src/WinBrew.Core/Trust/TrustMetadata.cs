using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WinBrew.Core.Trust;

/// <summary>One signature in an envelope (D6).</summary>
public sealed record EnvelopeSignature(string Keyid, string Sig);

/// <summary>
/// The D6 envelope. Signatures cover the EXACT payload bytes (base64-decoded
/// <see cref="Payload"/>), so no JSON canonicalization is ever needed.
/// </summary>
public sealed record SignedEnvelope(string Payload, string PayloadType, IReadOnlyList<EnvelopeSignature> Signatures);

/// <summary>A root key entry: ECDSA P-256, SPKI DER as base64.</summary>
public sealed record RootKey(string Keytype, string Scheme, string Public);

/// <summary>A role's threshold and key IDs.</summary>
public sealed record RoleKeys(int Threshold, IReadOnlyList<string> Keyids);

/// <summary>root.json payload (schemas/repository-root.v1.schema.json).</summary>
public sealed record RootMetadata(
    int Version,
    DateTimeOffset Expires,
    IReadOnlyDictionary<string, RootKey> Keys,
    IReadOnlyDictionary<string, RoleKeys> Roles);

/// <summary>The digest and length of the file a timestamp binds.</summary>
public sealed record TimestampFileMeta(string Sha256, long Length);

/// <summary>timestamp.json payload (schemas/repository-timestamp.v1.schema.json).</summary>
public sealed record TimestampMetadata(
    int Version,
    DateTimeOffset Expires,
    IReadOnlyDictionary<string, TimestampFileMeta> Meta)
{
    public TimestampFileMeta Index => Meta[TrustRoles.IndexFile];
}

/// <summary>Role names, payload types and maximum lifetimes (D6, A14).</summary>
public static class TrustRoles
{
    public const string Root = "root";
    public const string Timestamp = "timestamp";
    public const string Index = "index";

    public const string IndexFile = "index.json";

    public const string RootPayloadType = "application/vnd.winbrew.root+json;version=1";
    public const string TimestampPayloadType = "application/vnd.winbrew.timestamp+json;version=1";
    public const string IndexPayloadType = "application/vnd.winbrew.index+json;version=1";

    /// <summary>The self-update feed's index (A14: same envelope, roles and key model as D6).</summary>
    public const string ReleasesPayloadType = "application/vnd.winbrew.releases+json;version=1";

    public static readonly TimeSpan RootMaxLifetime = TimeSpan.FromDays(365);
    public static readonly TimeSpan TimestampMaxLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan IndexMaxLifetime = TimeSpan.FromDays(30);

    public static readonly IReadOnlyList<string> All = [Root, Timestamp, Index];
}

/// <summary>
/// Strict parsers for the D6 envelope and role payloads. Unknown members are
/// rejected, required members must be present, every key is validated with
/// <see cref="SpkiRules"/> and must match its key ID. Problems are
/// VerificationFailure (exit 5).
/// </summary>
public static partial class TrustMetadataParser
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex KeyIdRegex();

    public static SignedEnvelope ParseEnvelope(ReadOnlySpan<byte> document, string what)
    {
        SignedEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(document, TrustJsonContext.Default.SignedEnvelope);
        }
        catch (JsonException ex)
        {
            throw Invalid($"{what} is not a signed envelope: {ex.Message}");
        }

        return envelope ?? throw Invalid($"{what} is empty (null document).");
    }

    public static byte[] DecodePayload(SignedEnvelope envelope, string what)
    {
        try
        {
            return Convert.FromBase64String(envelope.Payload);
        }
        catch (FormatException)
        {
            throw Invalid($"{what}: the payload is not valid base64.");
        }
    }

    public static RootMetadata ParseRoot(ReadOnlySpan<byte> payload)
    {
        var root = Deserialize(payload, TrustJsonContext.Default.RootMetadata, "root.json");
        if (root.Version < 1)
        {
            throw Invalid($"root.json version must be >= 1 (got {root.Version}).");
        }

        foreach (var (keyId, key) in root.Keys)
        {
            if (!KeyIdRegex().IsMatch(keyId))
            {
                throw Invalid($"root.json key ID '{keyId}' is not 64 lowercase hex characters.");
            }

            if (key.Keytype != "ecdsa" || key.Scheme != "ecdsa-sha256-nistp256")
            {
                throw Invalid($"root.json key {keyId} has keytype '{key.Keytype}' / scheme '{key.Scheme}'; only ecdsa / ecdsa-sha256-nistp256 is accepted.");
            }

            var spki = DecodeSpki(key, keyId);
            SpkiRules.Validate(spki);
            if (SpkiRules.KeyId(spki) != keyId)
            {
                throw Invalid($"root.json key {keyId} does not match the SHA-256 of its SubjectPublicKeyInfo.");
            }
        }

        foreach (var role in TrustRoles.All)
        {
            if (!root.Roles.TryGetValue(role, out var keys))
            {
                throw Invalid($"root.json has no '{role}' role.");
            }

            if (keys.Keyids.Count == 0 || keys.Threshold < 1)
            {
                throw Invalid($"root.json role '{role}' needs at least one key and a threshold >= 1.");
            }

            if (keys.Keyids.Distinct(StringComparer.Ordinal).Count() != keys.Keyids.Count)
            {
                throw Invalid($"root.json role '{role}' lists a key ID more than once (D6: duplicate key IDs are rejected).");
            }

            if (keys.Threshold > keys.Keyids.Count)
            {
                throw Invalid($"root.json role '{role}' has threshold {keys.Threshold} but only {keys.Keyids.Count} key(s).");
            }

            var unknown = keys.Keyids.FirstOrDefault(k => !root.Keys.ContainsKey(k));
            if (unknown is not null)
            {
                throw Invalid($"root.json role '{role}' references unknown key {unknown}.");
            }
        }

        if (root.Roles.Keys.Any(r => !TrustRoles.All.Contains(r, StringComparer.Ordinal)))
        {
            throw Invalid("root.json declares a role other than root, timestamp and index.");
        }

        return root;
    }

    public static TimestampMetadata ParseTimestamp(ReadOnlySpan<byte> payload)
    {
        var timestamp = Deserialize(payload, TrustJsonContext.Default.TimestampMetadata, "timestamp.json");
        if (timestamp.Version < 1)
        {
            throw Invalid($"timestamp.json version must be >= 1 (got {timestamp.Version}).");
        }

        if (timestamp.Meta.Count != 1 || !timestamp.Meta.TryGetValue(TrustRoles.IndexFile, out var index))
        {
            throw Invalid("timestamp.json meta must describe exactly index.json.");
        }

        if (!KeyIdRegex().IsMatch(index.Sha256) || index.Length < 0)
        {
            throw Invalid("timestamp.json meta for index.json has an invalid sha256 or length.");
        }

        return timestamp;
    }

    internal static byte[] DecodeSpki(RootKey key, string keyId)
    {
        try
        {
            return Convert.FromBase64String(key.Public);
        }
        catch (FormatException)
        {
            throw Invalid($"root.json key {keyId}: 'public' is not valid base64.");
        }
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, string what)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, type) ?? throw Invalid($"{what} is empty (null document).");
        }
        catch (JsonException ex)
        {
            throw Invalid($"{what} does not match its schema: {ex.Message}");
        }
    }

    internal static WinBrewException Invalid(string message) => new(WinBrewErrorCode.VerificationFailure, message);
}

/// <summary>Strict source-generated context for trust metadata (A4).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(SignedEnvelope))]
[JsonSerializable(typeof(RootMetadata))]
[JsonSerializable(typeof(TimestampMetadata))]
internal sealed partial class TrustJsonContext : JsonSerializerContext
{
}
