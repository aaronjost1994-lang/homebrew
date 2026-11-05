using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinBrew.Core.Manifests;

/// <summary>
/// Strict manifest deserialization (A9). Manifests fetched during install
/// are bound to index.json by digest (D6), parsed with unknown members
/// rejected, then checked by <see cref="ManifestValidator"/>. Any problem is
/// a VerificationFailure (exit 5).
/// </summary>
public static class ManifestParser
{
    public static Manifest Parse(ReadOnlySpan<byte> utf8, bool testHooksBuild)
    {
        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(utf8, StrictManifestJsonContext.Default.Manifest);
        }
        catch (JsonException ex)
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                $"Manifest does not match manifest.v1: {ex.Message}");
        }

        if (manifest is null)
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure, "Manifest is empty (null document).");
        }

        var errors = ManifestValidator.Validate(manifest, testHooksBuild);
        if (errors.Count > 0)
        {
            throw new WinBrewException(
                WinBrewErrorCode.VerificationFailure,
                $"Manifest {manifest.Id}@{manifest.Version} is invalid: " + string.Join("; ", errors),
                new Dictionary<string, object?> { ["errors"] = errors.Select(e => e.ToString()).ToList() });
        }

        return manifest;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(Manifest))]
internal sealed partial class StrictManifestJsonContext : JsonSerializerContext
{
}
