using System.Security.Cryptography;

namespace WinBrew.Core.Trust;

/// <summary>A root that has been verified (bootstrap or rotation), with its exact envelope bytes.</summary>
public sealed record TrustedRoot(byte[] Envelope, RootMetadata Metadata)
{
    public int Version => Metadata.Version;

    /// <summary>The root role's key IDs = the fingerprints shown on <c>repo add</c> (D6).</summary>
    public IReadOnlyList<string> RootFingerprints =>
        Metadata.Roles[TrustRoles.Root].Keyids.OrderBy(k => k, StringComparer.Ordinal).ToList();
}

/// <summary>
/// D6 client verification, pure (no I/O): envelope signatures and thresholds,
/// root bootstrap and N→N+1 rotation, timestamp, index; replay/rollback
/// (high-water marks), expiry with clock skew, and maximum lifetimes.
/// Order: root (walk N+1… sequentially) → timestamp → index → manifest
/// digest → artifact SHA-256 (the last two happen at install time).
/// </summary>
public static class TrustChain
{
    /// <summary>
    /// Verifies an envelope against a role of <paramref name="root"/> and
    /// returns the payload bytes. Rejects a wrong payload type, duplicate key
    /// IDs within the envelope, any signature that is not exactly 64 bytes,
    /// and fewer than <c>threshold</c> DISTINCT valid role keys (each key counts
    /// at most once).
    /// </summary>
    public static byte[] VerifyEnvelope(ReadOnlySpan<byte> document, string expectedPayloadType, RootMetadata root, string role, string what)
    {
        var envelope = TrustMetadataParser.ParseEnvelope(document, what);
        if (!string.Equals(envelope.PayloadType, expectedPayloadType, StringComparison.Ordinal))
        {
            throw Fail($"{what} has payloadType '{envelope.PayloadType}', expected '{expectedPayloadType}'.");
        }

        var payload = TrustMetadataParser.DecodePayload(envelope, what);
        var keys = root.Roles[role];
        var counted = CountValidSignatures(envelope, payload, keys, root, what);
        if (counted < keys.Threshold)
        {
            throw Fail($"{what} has {counted} valid signature(s) from '{role}' keys; the threshold is {keys.Threshold} (D6).",
                new Dictionary<string, object?> { ["role"] = role, ["valid"] = counted, ["threshold"] = keys.Threshold });
        }

        return payload;
    }

    /// <summary>
    /// Bootstrap (repo add, first self-update): the root must be signed by a
    /// threshold of its OWN root keys and must not be expired. The caller
    /// then shows <see cref="TrustedRoot.RootFingerprints"/> for approval.
    /// </summary>
    public static TrustedRoot Bootstrap(byte[] envelope, DateTimeOffset now, TimeSpan skew)
    {
        var root = ParseRootUnverified(envelope, "root.json");
        VerifyEnvelope(envelope, TrustRoles.RootPayloadType, root, TrustRoles.Root, "root.json");
        EnsureNotExpired("root.json", root.Expires, now, skew);
        EnsureLifetime("root.json", root.Expires, TrustRoles.RootMaxLifetime, now, skew);
        return new TrustedRoot(envelope, root);
    }

    /// <summary>
    /// A root accepted earlier (stored in SQLite): its self-signatures are
    /// re-verified (local tampering is caught) but no time rule applies — the
    /// refresh that follows walks to the current root and checks expiry there.
    /// </summary>
    public static TrustedRoot LoadAccepted(byte[] envelope)
    {
        var root = ParseRootUnverified(envelope, "the accepted root.json");
        VerifyEnvelope(envelope, TrustRoles.RootPayloadType, root, TrustRoles.Root, "the accepted root.json");
        return new TrustedRoot(envelope, root);
    }

    /// <summary>
    /// Key rotation: root N+1 must have version N+1 and be signed by a
    /// threshold of root N's root keys AND a threshold of its own root keys.
    /// Intermediate roots may be expired; the final one is checked by
    /// <see cref="EnsureCurrent"/>.
    /// </summary>
    public static TrustedRoot Rotate(TrustedRoot current, byte[] nextEnvelope)
    {
        var what = $"root/{current.Version + 1}.json";
        var next = ParseRootUnverified(nextEnvelope, what);
        if (next.Version != current.Version + 1)
        {
            throw Fail($"{what} declares version {next.Version}; rotation must go from {current.Version} to {current.Version + 1} (D6).");
        }

        VerifyEnvelope(nextEnvelope, TrustRoles.RootPayloadType, current.Metadata, TrustRoles.Root, $"{what} (signed by root {current.Version})");
        VerifyEnvelope(nextEnvelope, TrustRoles.RootPayloadType, next, TrustRoles.Root, $"{what} (signed by its own keys)");
        return new TrustedRoot(nextEnvelope, next);
    }

    /// <summary>The root reached after the walk must be valid now (and within its lifetime).</summary>
    public static void EnsureCurrent(TrustedRoot root, DateTimeOffset now, TimeSpan skew)
    {
        EnsureNotExpired($"root.json v{root.Version}", root.Metadata.Expires, now, skew);
        EnsureLifetime($"root.json v{root.Version}", root.Metadata.Expires, TrustRoles.RootMaxLifetime, now, skew);
    }

    /// <summary>
    /// timestamp.json: signed by the timestamp role; version ≥ the accepted
    /// high-water mark (a lower one is a replay); not expired (an expired one
    /// is a freeze attack); within its 7-day lifetime.
    /// </summary>
    public static TimestampMetadata VerifyTimestamp(TrustedRoot root, ReadOnlySpan<byte> envelope, int? acceptedVersion, DateTimeOffset now, TimeSpan skew)
    {
        var payload = VerifyEnvelope(envelope, TrustRoles.TimestampPayloadType, root.Metadata, TrustRoles.Timestamp, "timestamp.json");
        var timestamp = TrustMetadataParser.ParseTimestamp(payload);
        EnsureNotLower("timestamp.json", timestamp.Version, acceptedVersion);
        EnsureNotExpired("timestamp.json", timestamp.Expires, now, skew);
        EnsureLifetime("timestamp.json", timestamp.Expires, TrustRoles.TimestampMaxLifetime, now, skew);
        return timestamp;
    }

    /// <summary>
    /// index.json bytes must match the length and SHA-256 that timestamp.json
    /// declares, then the envelope must be signed by the index role. Returns
    /// the payload; the caller parses it and calls <see cref="EnsureIndexFresh"/>.
    /// </summary>
    public static byte[] VerifyIndexEnvelope(TrustedRoot root, ReadOnlySpan<byte> envelope, TimestampFileMeta meta, string expectedPayloadType)
    {
        if (envelope.Length != meta.Length)
        {
            throw Fail($"index.json is {envelope.Length} bytes; timestamp.json declares {meta.Length} (D6).");
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(envelope));
        if (sha != meta.Sha256)
        {
            throw Fail($"index.json SHA-256 {sha} does not match timestamp.json ({meta.Sha256}) (D6).",
                new Dictionary<string, object?> { ["expected"] = meta.Sha256, ["actual"] = sha });
        }

        return VerifyEnvelope(envelope, expectedPayloadType, root.Metadata, TrustRoles.Index, "index.json");
    }

    /// <summary>index.json version ≥ the accepted mark, not expired, within its 30-day lifetime.</summary>
    public static void EnsureIndexFresh(int version, DateTimeOffset expires, int? acceptedVersion, DateTimeOffset now, TimeSpan skew)
    {
        EnsureNotLower("index.json", version, acceptedVersion);
        EnsureNotExpired("index.json", expires, now, skew);
        EnsureLifetime("index.json", expires, TrustRoles.IndexMaxLifetime, now, skew);
    }

    public static bool IsExpired(DateTimeOffset expires, DateTimeOffset now, TimeSpan skew) => expires.Add(skew) < now;

    private static RootMetadata ParseRootUnverified(byte[] envelope, string what)
    {
        var parsed = TrustMetadataParser.ParseEnvelope(envelope, what);
        if (!string.Equals(parsed.PayloadType, TrustRoles.RootPayloadType, StringComparison.Ordinal))
        {
            throw Fail($"{what} has payloadType '{parsed.PayloadType}', expected '{TrustRoles.RootPayloadType}'.");
        }

        return TrustMetadataParser.ParseRoot(TrustMetadataParser.DecodePayload(parsed, what));
    }

    private static int CountValidSignatures(SignedEnvelope envelope, byte[] payload, RoleKeys keys, RootMetadata root, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signature in envelope.Signatures)
        {
            if (!seen.Add(signature.Keyid))
            {
                throw Fail($"{what} carries more than one signature for key {signature.Keyid} (D6: duplicate key IDs are rejected).");
            }
        }

        var valid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signature in envelope.Signatures)
        {
            byte[] sig;
            try
            {
                sig = Convert.FromBase64String(signature.Sig);
            }
            catch (FormatException)
            {
                throw Fail($"{what}: the signature for key {signature.Keyid} is not valid base64.");
            }

            if (sig.Length != SpkiRules.SignatureLength)
            {
                throw Fail($"{what}: the signature for key {signature.Keyid} is {sig.Length} bytes; exactly {SpkiRules.SignatureLength} are required (IEEE P1363, D6).");
            }

            if (!keys.Keyids.Contains(signature.Keyid, StringComparer.Ordinal) || !root.Keys.TryGetValue(signature.Keyid, out var key))
            {
                continue; // a key outside this role never counts
            }

            using var ecdsa = SpkiRules.Import(TrustMetadataParser.DecodeSpki(key, signature.Keyid));
            if (SpkiRules.Verify(ecdsa, payload, sig))
            {
                valid.Add(signature.Keyid);
            }
        }

        return valid.Count;
    }

    private static void EnsureNotLower(string what, int version, int? accepted)
    {
        if (accepted is { } mark && version < mark)
        {
            throw Fail($"{what} version {version} is lower than the accepted version {mark} (rollback/replay rejected, D6).",
                new Dictionary<string, object?> { ["accepted"] = mark, ["offered"] = version });
        }
    }

    private static void EnsureNotExpired(string what, DateTimeOffset expires, DateTimeOffset now, TimeSpan skew)
    {
        if (IsExpired(expires, now, skew))
        {
            throw Fail($"{what} expired at {expires:O} (D6; allowed clock skew {skew.TotalSeconds:0}s).",
                new Dictionary<string, object?> { ["expires"] = expires.ToString("O", System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    private static void EnsureLifetime(string what, DateTimeOffset expires, TimeSpan max, DateTimeOffset now, TimeSpan skew)
    {
        if (expires - now > max + skew)
        {
            throw Fail($"{what} expires at {expires:O}, beyond its maximum lifetime of {max.TotalDays:0} days (D6).");
        }
    }

    private static WinBrewException Fail(string message, IReadOnlyDictionary<string, object?>? details = null) =>
        new(WinBrewErrorCode.VerificationFailure, message, details);
}
