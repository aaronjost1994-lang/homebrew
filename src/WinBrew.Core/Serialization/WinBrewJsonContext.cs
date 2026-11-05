using System.Text.Json;
using System.Text.Json.Serialization;
using WinBrew.Core.Manifests;

namespace WinBrew.Core.Serialization;

/// <summary>
/// Source-generated serializer context for WinBrew.Core types (A4:
/// System.Text.Json with source-generated serializer contexts).
/// camelCase JSON, matching the schema files.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(ManifestArtifact))]
[JsonSerializable(typeof(ManifestDependency))]
[JsonSerializable(typeof(ManifestExecutable))]
[JsonSerializable(typeof(List<ManifestError>))]
internal sealed partial class WinBrewJsonContext : JsonSerializerContext
{
}
