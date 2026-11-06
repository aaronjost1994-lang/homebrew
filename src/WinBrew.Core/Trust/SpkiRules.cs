using System.Formats.Asn1;
using System.Security.Cryptography;

namespace WinBrew.Core.Trust;

/// <summary>
/// D6 key rules for ECDSA P-256 SubjectPublicKeyInfo. The SPKI is parsed with
/// <see cref="System.Formats.Asn1"/> BEFORE any import, because
/// <c>ExportParameters(false).Curve.Oid.Value</c> can be null on Windows CNG
/// (only a FriendlyName is set) — an Oid.Value-only check after import would
/// reject valid keys or be skipped by defensive code.
/// </summary>
public static class SpkiRules
{
    /// <summary>id-ecPublicKey.</summary>
    public const string EcPublicKeyOid = "1.2.840.10045.2.1";

    /// <summary>prime256v1 / secp256r1 / nistP256.</summary>
    public const string P256CurveOid = "1.2.840.10045.3.1.7";

    /// <summary>IEEE P1363 r‖s for P-256.</summary>
    public const int SignatureLength = 64;

    /// <summary>Key ID = fingerprint: lowercase hex SHA-256 of the DER-encoded SPKI (D6).</summary>
    public static string KeyId(ReadOnlySpan<byte> spki) => Convert.ToHexStringLower(SHA256.HashData(spki));

    /// <summary>
    /// Requires algorithm id-ecPublicKey, parameters EXACTLY the named-curve OID
    /// P-256 (explicit curve parameters are rejected), and an uncompressed
    /// 65-byte point (0x04 prefix). Any violation is VerificationFailure (exit 5).
    /// </summary>
    public static void Validate(ReadOnlySpan<byte> spki)
    {
        try
        {
            var reader = new AsnReader(spki.ToArray(), AsnEncodingRules.DER);
            var info = reader.ReadSequence();
            reader.ThrowIfNotEmpty();

            var algorithm = info.ReadSequence();
            var oid = algorithm.ReadObjectIdentifier();
            if (oid != EcPublicKeyOid)
            {
                throw Invalid($"algorithm OID {oid} is not id-ecPublicKey ({EcPublicKeyOid})");
            }

            if (!algorithm.HasData)
            {
                throw Invalid("the EC parameters are missing");
            }

            if (algorithm.PeekTag() != Asn1Tag.ObjectIdentifier)
            {
                throw Invalid("explicit curve parameters are not accepted; only the named curve P-256 is");
            }

            var curve = algorithm.ReadObjectIdentifier();
            if (curve != P256CurveOid)
            {
                throw Invalid($"curve {curve} is not P-256 ({P256CurveOid})");
            }

            algorithm.ThrowIfNotEmpty();

            var point = info.ReadBitString(out var unusedBits);
            info.ThrowIfNotEmpty();
            if (unusedBits != 0 || point.Length != 65 || point[0] != 0x04)
            {
                throw Invalid("the public key is not an uncompressed 65-byte P-256 point (0x04 prefix)");
            }
        }
        catch (AsnContentException ex)
        {
            throw Invalid($"malformed SubjectPublicKeyInfo ({ex.Message})");
        }
    }

    /// <summary>Validates, imports, and asserts <c>KeySize == 256</c> after import (D6).</summary>
    public static ECDsa Import(byte[] spki)
    {
        Validate(spki);
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length)
            {
                throw Invalid("trailing bytes after the SubjectPublicKeyInfo");
            }

            if (ecdsa.KeySize != 256)
            {
                throw Invalid($"imported key size is {ecdsa.KeySize}, not 256");
            }

            return ecdsa;
        }
        catch (CryptographicException ex)
        {
            ecdsa.Dispose();
            throw Invalid($"the key cannot be imported ({ex.Message})");
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Verifies one signature over the exact payload bytes. The signature must
    /// be exactly 64 bytes; IEEE P1363 is always specified explicitly.
    /// </summary>
    public static bool Verify(ECDsa key, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureLength)
        {
            throw Invalid($"a signature is {signature.Length} bytes; IEEE P1363 P-256 signatures are exactly {SignatureLength}");
        }

        return key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static WinBrewException Invalid(string reason) =>
        new(WinBrewErrorCode.VerificationFailure, $"Untrusted key material: {reason} (D6).");
}
