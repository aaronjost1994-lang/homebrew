using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinBrew.Application.Catalog;
using WinBrew.Core;
using WinBrew.Core.Bundles;
using WinBrew.Core.Release;
using WinBrew.Core.Trust;
using WinBrew.TestFixtures;

namespace WinBrew.UnitTests;

/// <summary>D6 trust rules (A13/A16 negative cases) against the pure Core implementation.</summary>
public sealed class TrustChainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(300);

    private static void Rejected(Action action, string fragment)
    {
        var ex = Assert.Throws<WinBrewException>(action);
        Assert.Equal(WinBrewErrorCode.VerificationFailure, ex.Code);
        Assert.Contains(fragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- SPKI rules (before import) ----------------------------------------------------------

    [Fact]
    public void Spki_P256NamedCurveUncompressed_IsAccepted_AndKeyIdIsSha256OfDer()
    {
        using var key = SigningKey.Create();
        SpkiRules.Validate(key.Spki);
        using var imported = SpkiRules.Import(key.Spki);
        Assert.Equal(256, imported.KeySize);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(key.Spki)), SpkiRules.KeyId(key.Spki));
    }

    [Fact]
    public void Spki_WrongCurve_P384_IsRejected() =>
        Rejected(() => SpkiRules.Validate(SigningKey.Create(ECCurve.NamedCurves.nistP384).Spki), "is not P-256");

    [Fact]
    public void Spki_ExplicitCurveParameters_AreRejected()
    {
        using var key = SigningKey.Create();
        var point = key.Key.ExportParameters(false).Q;
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(SpkiRules.EcPublicKeyOid);
                using (writer.PushSequence()) // SpecifiedECDomain instead of the named-curve OID
                {
                    writer.WriteInteger(1);
                }
            }

            writer.WriteBitString([0x04, .. point.X!, .. point.Y!]);
        }

        Rejected(() => SpkiRules.Validate(writer.Encode()), "explicit curve parameters");
    }

    [Fact]
    public void Spki_CompressedPoint_IsRejected()
    {
        using var key = SigningKey.Create();
        var point = key.Key.ExportParameters(false).Q;
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(SpkiRules.EcPublicKeyOid);
                writer.WriteObjectIdentifier(SpkiRules.P256CurveOid);
            }

            writer.WriteBitString([(byte)(0x02 | (point.Y![^1] & 1)), .. point.X!]);
        }

        Rejected(() => SpkiRules.Validate(writer.Encode()), "uncompressed");
    }

    [Fact]
    public void Spki_RsaKey_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        Rejected(() => SpkiRules.Validate(rsa.ExportSubjectPublicKeyInfo()), "not id-ecPublicKey");
    }

    [Fact]
    public void Signature_NotExactly64Bytes_IsRejected_EvenWhenOtherwiseValid()
    {
        using var key = SigningKey.Create();
        var payload = "x"u8.ToArray();
        var der = key.Key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        using var imported = SpkiRules.Import(key.Spki);
        Rejected(() => SpkiRules.Verify(imported, payload, der), "exactly 64");
        Assert.True(SpkiRules.Verify(imported, payload, key.Sign(payload)));
    }

    // ---- envelopes, thresholds, roles ---------------------------------------------------------

    [Fact]
    public void Bootstrap_AcceptsASelfSignedRoot_AndExposesSortedRootFingerprints()
    {
        var signer = new RepositorySigner(rootKeys: 3, rootThreshold: 2);
        var root = TrustChain.Bootstrap(signer.CurrentRoot, Now, Skew);
        Assert.Equal(1, root.Version);
        Assert.Equal(signer.RootFingerprints, root.RootFingerprints);
    }

    [Fact]
    public void Root_BelowThreshold_IsRejected()
    {
        var signer = new RepositorySigner(rootKeys: 3, rootThreshold: 2);
        var payload = signer.RootPayload(1, signer.RootKeys);
        var oneSignature = RepositorySigner.Envelope(payload, RepositorySigner.RootType, signer.RootKeys.Take(1));
        Rejected(() => TrustChain.Bootstrap(oneSignature, Now, Skew), "the threshold is 2");
    }

    [Fact]
    public void DuplicateKeyIds_InOneEnvelope_AreRejected_SoOneKeyCannotCountTwice()
    {
        var signer = new RepositorySigner(rootKeys: 2, rootThreshold: 2);
        var payload = signer.RootPayload(1, signer.RootKeys);
        var key = signer.RootKeys[0];
        var twice = RepositorySigner.RawEnvelope(payload, RepositorySigner.RootType,
            [new(key.Id, key.Sign(payload)), new(key.Id, key.Sign(payload))]);
        Rejected(() => TrustChain.Bootstrap(twice, Now, Skew), "duplicate key IDs");
    }

    [Fact]
    public void DuplicateKeyIds_InARoleDefinition_AreRejected()
    {
        var signer = new RepositorySigner();
        var root = JsonNode.Parse(signer.RootPayload(1, signer.RootKeys))!;
        var ids = root["roles"]!["root"]!["keyids"]!.AsArray();
        ids.Add(ids[0]!.GetValue<string>());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        Rejected(() => TrustMetadataParser.ParseRoot(bytes), "more than once");
    }

    [Fact]
    public void TamperedPayload_WrongKey_AndKeysOutsideTheRole_DoNotCount()
    {
        var signer = new RepositorySigner();
        var root = TrustChain.Bootstrap(signer.CurrentRoot, Now, Skew);
        var payload = "{}"u8.ToArray();

        var tampered = RepositorySigner.RawEnvelope("{ }"u8.ToArray(), RepositorySigner.IndexType,
            [new(signer.IndexKey.Id, signer.IndexKey.Sign(payload))]);
        Rejected(() => TrustChain.VerifyEnvelope(tampered, RepositorySigner.IndexType, root.Metadata, TrustRoles.Index, "index.json"), "0 valid");

        var stranger = RepositorySigner.Envelope(payload, RepositorySigner.IndexType, [SigningKey.Create()]);
        Rejected(() => TrustChain.VerifyEnvelope(stranger, RepositorySigner.IndexType, root.Metadata, TrustRoles.Index, "index.json"), "0 valid");

        // The timestamp key is in the root, but not in the index role.
        var otherRole = RepositorySigner.Envelope(payload, RepositorySigner.IndexType, [signer.TimestampKey]);
        Rejected(() => TrustChain.VerifyEnvelope(otherRole, RepositorySigner.IndexType, root.Metadata, TrustRoles.Index, "index.json"), "0 valid");

        var unsigned = RepositorySigner.RawEnvelope(payload, RepositorySigner.IndexType, []);
        Rejected(() => TrustChain.VerifyEnvelope(unsigned, RepositorySigner.IndexType, root.Metadata, TrustRoles.Index, "index.json"), "0 valid");

        Assert.Equal(payload, TrustChain.VerifyEnvelope(signer.SignIndex(payload), RepositorySigner.IndexType, root.Metadata, TrustRoles.Index, "index.json"));
    }

    [Fact]
    public void PartiallySignedIndex_BelowAThresholdOfTwo_IsRejected()
    {
        var signer = new RepositorySigner();
        var second = SigningKey.Create();
        var root = JsonNode.Parse(signer.RootPayload(1, signer.RootKeys))!;
        root["keys"]![second.Id] = new JsonObject { ["keytype"] = "ecdsa", ["scheme"] = "ecdsa-sha256-nistp256", ["public"] = Convert.ToBase64String(second.Spki) };
        root["roles"]!["index"] = new JsonObject { ["threshold"] = 2, ["keyids"] = new JsonArray(signer.IndexKey.Id, second.Id) };
        var metadata = TrustMetadataParser.ParseRoot(JsonSerializer.SerializeToUtf8Bytes(root));
        var payload = "{}"u8.ToArray();
        Rejected(() => TrustChain.VerifyEnvelope(signer.SignIndex(payload), RepositorySigner.IndexType, metadata, TrustRoles.Index, "index.json"), "threshold is 2");
        Assert.Equal(payload, TrustChain.VerifyEnvelope(RepositorySigner.Envelope(payload, RepositorySigner.IndexType, [signer.IndexKey, second]),
            RepositorySigner.IndexType, metadata, TrustRoles.Index, "index.json"));
    }

    [Fact]
    public void KeyWhoseIdDoesNotMatchItsSpki_IsRejected()
    {
        var signer = new RepositorySigner();
        var root = JsonNode.Parse(signer.RootPayload(1, signer.RootKeys))!;
        var keys = root["keys"]!.AsObject();
        var (id, entry) = keys.First();
        keys.Remove(id);
        keys[new string('e', 64)] = entry!.DeepClone();
        Rejected(() => TrustMetadataParser.ParseRoot(JsonSerializer.SerializeToUtf8Bytes(root)), "does not match");
    }

    // ---- rotation, expiry, replay ---------------------------------------------------------------

    [Fact]
    public void Rotation_RequiresBothThresholds_AndTheNextVersion()
    {
        var signer = new RepositorySigner();
        var v1 = TrustChain.Bootstrap(signer.CurrentRoot, Now, Skew);
        signer.RotateRoot();
        Assert.Equal(2, TrustChain.Rotate(v1, signer.CurrentRoot).Version);

        var crossOnly = new RepositorySigner();
        var c1 = TrustChain.Bootstrap(crossOnly.CurrentRoot, Now, Skew);
        crossOnly.RotateRoot(selfSign: false);
        Rejected(() => TrustChain.Rotate(c1, crossOnly.CurrentRoot), "signed by its own keys");

        var selfOnly = new RepositorySigner();
        var s1 = TrustChain.Bootstrap(selfOnly.CurrentRoot, Now, Skew);
        selfOnly.RotateRoot(crossSign: false);
        Rejected(() => TrustChain.Rotate(s1, selfOnly.CurrentRoot), "signed by root 1");

        var skip = new RepositorySigner();
        var k1 = TrustChain.Bootstrap(skip.CurrentRoot, Now, Skew);
        skip.RotateRoot();
        var v2 = TrustChain.Rotate(k1, skip.CurrentRoot);
        skip.RotateRoot();
        Rejected(() => TrustChain.Rotate(k1, skip.CurrentRoot), "rotation must go from 1 to 2");
        Assert.Equal(3, TrustChain.Rotate(v2, skip.CurrentRoot).Version);
    }

    [Fact]
    public void Timestamp_Expired_Replayed_TooLongLived_AreRejected()
    {
        var signer = new RepositorySigner();
        var root = TrustChain.Bootstrap(signer.CurrentRoot, Now, Skew);
        var index = signer.SignIndex("{}"u8.ToArray());

        Rejected(() => TrustChain.VerifyTimestamp(root, signer.SignTimestamp(3, Now.AddHours(-1), index), null, Now, Skew), "expired");
        Assert.Equal(3, TrustChain.VerifyTimestamp(root, signer.SignTimestamp(3, Now.AddSeconds(-60), index), null, Now, Skew).Version); // within skew
        Rejected(() => TrustChain.VerifyTimestamp(root, signer.SignTimestamp(3, Now.AddDays(1), index), 4, Now, Skew), "lower than the accepted version 4");
        Rejected(() => TrustChain.VerifyTimestamp(root, signer.SignTimestamp(5, Now.AddDays(30), index), 4, Now, Skew), "maximum lifetime of 7 days");
    }

    [Fact]
    public void Index_MustMatchTheTimestampLengthAndDigest_AndBeFresh()
    {
        var signer = new RepositorySigner();
        var root = TrustChain.Bootstrap(signer.CurrentRoot, Now, Skew);
        var index = signer.SignIndex("{}"u8.ToArray());
        var meta = new TimestampFileMeta(Convert.ToHexStringLower(SHA256.HashData(index)), index.Length);

        Rejected(() => TrustChain.VerifyIndexEnvelope(root, [.. index, (byte)' '], meta, RepositorySigner.IndexType), "bytes; timestamp.json declares");
        var other = signer.SignIndex("{}"u8.ToArray());
        Rejected(() => TrustChain.VerifyIndexEnvelope(root, other, meta, RepositorySigner.IndexType), "does not match timestamp.json");
        Assert.Equal("{}"u8.ToArray(), TrustChain.VerifyIndexEnvelope(root, index, meta, RepositorySigner.IndexType));

        Rejected(() => TrustChain.EnsureIndexFresh(2, Now.AddDays(1), 3, Now, Skew), "rollback");
        Rejected(() => TrustChain.EnsureIndexFresh(4, Now.AddDays(-1), 3, Now, Skew), "expired");
        Rejected(() => TrustChain.EnsureIndexFresh(4, Now.AddDays(45), 3, Now, Skew), "maximum lifetime of 30 days");
        TrustChain.EnsureIndexFresh(3, Now.AddDays(10), 3, Now, Skew);
    }

    [Fact]
    public void ExpiredRoot_IsRejectedAtBootstrap_ButAnAcceptedRootStillLoadsForTheWalk()
    {
        var signer = new RepositorySigner { RootExpires = Now.AddDays(-2) };
        signer.ResignInitialRoot();
        Rejected(() => TrustChain.Bootstrap(signer.CurrentRoot, Now, Skew), "expired");
        Assert.Equal(1, TrustChain.LoadAccepted(signer.CurrentRoot).Version);
    }

    // ---- build composition ------------------------------------------------------------------------

    /// <summary>
    /// Phase D acceptance: the release assembly contains no unsigned-metadata verifier.
    /// Since Phase D NO build has one — every build verifies through TrustChain.
    /// </summary>
    [Fact]
    public void TestHooksUnsignedVerifier_DoesNotExistInTheInfrastructureAssembly()
    {
        var assemblies = new[]
        {
            typeof(WinBrew.Infrastructure.Sqlite.SqliteStateStore).Assembly,
            typeof(RepositoryMetadataService).Assembly,
            typeof(TrustChain).Assembly,
        };
        foreach (var assembly in assemblies)
        {
            Assert.DoesNotContain(assembly.GetTypes(), t => t.Name is "TestHooksUnsignedVerifier" or "IMetadataVerifier");
        }
    }

    [Fact]
    public void FormatAge_RoundsDown_ToTheLargestUnit()
    {
        Assert.Equal("3 days", MetadataFreshness.FormatAge(TimeSpan.FromHours(80)));
        Assert.Equal("1 day", MetadataFreshness.FormatAge(TimeSpan.FromHours(25)));
        Assert.Equal("5 hours", MetadataFreshness.FormatAge(TimeSpan.FromMinutes(301)));
        Assert.Equal("1 minute", MetadataFreshness.FormatAge(TimeSpan.FromSeconds(5)));
    }
}

/// <summary>A15 bundle/lockfile parsing and A14 release-index parsing.</summary>
public sealed class BundleAndReleaseFileTests
{
    private const string Sha = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void Bundle_RoundTrips_AndRejectsUnknownMembersAndBadRanges()
    {
        var bundle = new Bundle("1", [new BundleRepository("local", "https://example.org")], [new BundlePackage("local/app", "[1.0.0,2.0.0)", true)]);
        var parsed = BundleFiles.ParseBundle(BundleFiles.Serialize(bundle), "b.json");
        Assert.Equal("[1.0.0,2.0.0)", Assert.Single(parsed.Packages).Range);

        Assert.Equal(WinBrewErrorCode.UsageError, Assert.Throws<WinBrewException>(() =>
            BundleFiles.ParseBundle("""{"schemaVersion":"1","repositories":[],"packages":[],"extra":1}"""u8, "b.json")).Code);
        Assert.Throws<WinBrewException>(() =>
            BundleFiles.ParseBundle("""{"schemaVersion":"1","repositories":[],"packages":[{"id":"app","range":"not a range"}]}"""u8, "b.json"));
        Assert.Throws<WinBrewException>(() =>
            BundleFiles.ParseBundle("""{"schemaVersion":"2","repositories":[],"packages":[]}"""u8, "b.json"));
    }

    [Fact]
    public void Lockfile_RequiresListedRepositories_NormalizedVersions_AndDigests()
    {
        static string Lock(string repo, string version, string sha) =>
            $$"""{"schemaVersion":"1","repositories":[{"name":"local","url":"https://example.org","rootKeyFingerprints":["{{Sha}}"]}],"packages":[{"repo":"{{repo}}","id":"app","version":"{{version}}","architecture":"x64","manifestSha256":"{{sha}}","artifactUrl":"https://example.org/a.zip","artifactSha256":"{{Sha}}","explicit":true}]}""";

        Assert.Single(BundleFiles.ParseLockfile(System.Text.Encoding.UTF8.GetBytes(Lock("local", "1.0.0", Sha)), "l.json").Packages);
        Assert.Throws<WinBrewException>(() => BundleFiles.ParseLockfile(System.Text.Encoding.UTF8.GetBytes(Lock("other", "1.0.0", Sha)), "l.json"));
        Assert.Throws<WinBrewException>(() => BundleFiles.ParseLockfile(System.Text.Encoding.UTF8.GetBytes(Lock("local", "1.0", Sha)), "l.json"));
        Assert.Throws<WinBrewException>(() => BundleFiles.ParseLockfile(System.Text.Encoding.UTF8.GetBytes(Lock("local", "1.0.0", "XYZ")), "l.json"));
    }

    [Fact]
    public void DefaultLockPath_SitsBesideTheBundle()
    {
        var bundle = Path.Combine(Path.GetTempPath(), "dev.json");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "dev.lock.json"), BundleFiles.DefaultLockPath(bundle));
    }

    [Fact]
    public void ReleaseIndex_IsStrict()
    {
        var ok = """{"version":1,"expires":"2030-01-01T00:00:00Z","releases":{"1.2.0":{"artifacts":[{"architecture":"x64","url":"https://example.org/w.zip","sha256":"SHA","size":10}]}}}""".Replace("SHA", Sha, StringComparison.Ordinal);
        Assert.Single(ReleaseIndexParser.Parse(System.Text.Encoding.UTF8.GetBytes(ok)).Releases);
        Assert.Throws<WinBrewException>(() => ReleaseIndexParser.Parse(System.Text.Encoding.UTF8.GetBytes(ok.Replace("1.2.0", "1.2", StringComparison.Ordinal))));
        Assert.Throws<WinBrewException>(() => ReleaseIndexParser.Parse(System.Text.Encoding.UTF8.GetBytes(ok.Replace("\"size\":10", "\"size\":0", StringComparison.Ordinal))));
    }
}
