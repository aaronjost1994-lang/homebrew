using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WinBrew.Core.Release;

/// <summary>One release zip (A14: wbrew-&lt;ver&gt;-win-&lt;arch&gt;.zip).</summary>
public sealed record ReleaseArtifact(string Architecture, string Url, string Sha256, long Size);

public sealed record ReleaseEntry(IReadOnlyList<ReleaseArtifact> Artifacts);

/// <summary>
/// The self-update feed's index payload (A14). It travels in the same D6
/// envelope (payloadType <c>application/vnd.winbrew.releases+json;version=1</c>),
/// is bound by the feed's timestamp.json and signed by the feed's index role.
/// </summary>
public sealed record ReleaseIndex(int Version, DateTimeOffset Expires, IReadOnlyDictionary<string, ReleaseEntry> Releases);

public static partial class ReleaseIndexParser
{
    private static readonly string[] Architectures = ["x64", "arm64", "x86"];

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

    public static ReleaseIndex Parse(ReadOnlySpan<byte> payload)
    {
        ReleaseIndex? index;
        try
        {
            index = JsonSerializer.Deserialize(payload, ReleaseJsonContext.Default.ReleaseIndex);
        }
        catch (JsonException ex)
        {
            throw Invalid($"the release index does not match its schema: {ex.Message}");
        }

        if (index is null || index.Version < 1)
        {
            throw Invalid("the release index is empty or has a version below 1.");
        }

        foreach (var (version, entry) in index.Releases)
        {
            if (!VersionOps.TryNormalizeVersion(version, out var normalized) || normalized != version)
            {
                throw Invalid($"release '{version}' is not a normalized version (D3).");
            }

            if (entry.Artifacts.Count == 0 ||
                entry.Artifacts.Select(a => a.Architecture).Distinct(StringComparer.Ordinal).Count() != entry.Artifacts.Count)
            {
                throw Invalid($"release {version} needs one artifact per architecture.");
            }

            foreach (var artifact in entry.Artifacts)
            {
                if (!Architectures.Contains(artifact.Architecture, StringComparer.Ordinal) ||
                    !Sha256Regex().IsMatch(artifact.Sha256) || artifact.Size <= 0 ||
                    !Uri.TryCreate(artifact.Url, UriKind.Absolute, out _))
                {
                    throw Invalid($"release {version} has an invalid {artifact.Architecture} artifact entry.");
                }
            }
        }

        return index;
    }

    private static WinBrewException Invalid(string message) => new(WinBrewErrorCode.VerificationFailure, message + " (A14)");
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ReleaseIndex))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext
{
}
