using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WinBrew.Core.Catalog;

/// <summary>index.json payload (D6, schemas/repository-index.v1.schema.json).</summary>
public sealed record RepositoryIndex(
    int Version,
    DateTimeOffset Expires,
    IReadOnlyDictionary<string, IndexPackage> Packages);

/// <summary>Package summary + versions (D6 sparse index).</summary>
public sealed record IndexPackage(
    string DisplayName,
    string Description,
    IReadOnlyDictionary<string, IndexVersion> Versions);

/// <summary>
/// Version entry: the MANIFEST digest and size, the artifact architectures
/// and the manifest's dependencies verbatim (D6).
/// </summary>
public sealed record IndexVersion(
    string Sha256,
    long Size,
    IReadOnlyList<string> Architectures,
    IReadOnlyList<IndexDependency> Dependencies);

/// <summary>Dependency exactly as written in the manifest (D6/A9).</summary>
public sealed record IndexDependency(string Id, string Range);

/// <summary>
/// Strict parser + validator for index.json payloads. Unknown members are
/// rejected (additionalProperties: false), required members must be
/// present, and every value is checked against the D3/D6/A9 rules. Any
/// problem is a VerificationFailure (exit 5): metadata that does not match
/// its specification is never ingested.
/// </summary>
public static partial class RepositoryIndexParser
{
    private static readonly string[] AllowedArchitectures = ["x64", "arm64", "x86"];

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

    public static RepositoryIndex Parse(ReadOnlySpan<byte> payload)
    {
        RepositoryIndex? index;
        try
        {
            index = JsonSerializer.Deserialize(payload, CatalogJsonContext.Default.RepositoryIndex);
        }
        catch (JsonException ex)
        {
            throw Invalid($"index.json does not match repository-index.v1: {ex.Message}");
        }

        if (index is null)
        {
            throw Invalid("index.json is empty (null document).");
        }

        Validate(index);
        return index;
    }

    private static void Validate(RepositoryIndex index)
    {
        if (index.Version < 1)
        {
            throw Invalid($"index.json version must be >= 1 (got {index.Version}).");
        }

        foreach (var (id, package) in index.Packages)
        {
            if (!Identifiers.IsValidPackageId(id))
            {
                throw Invalid($"index.json package id '{id}' is not a valid package id (A9).");
            }

            if (package.Versions.Count == 0)
            {
                throw Invalid($"index.json package '{id}' has no versions.");
            }

            foreach (var (version, entry) in package.Versions)
            {
                var where = $"{id}@{version}";
                if (!VersionOps.TryNormalizeVersion(version, out var normalized) ||
                    !string.Equals(normalized, version, StringComparison.Ordinal))
                {
                    throw Invalid($"index.json version key '{where}' is not a normalized NuGet version (D3).");
                }

                if (!Sha256Regex().IsMatch(entry.Sha256))
                {
                    throw Invalid($"index.json entry '{where}' has an invalid manifest sha256.");
                }

                if (entry.Size < 0)
                {
                    throw Invalid($"index.json entry '{where}' has a negative manifest size.");
                }

                if (entry.Architectures.Count == 0 ||
                    entry.Architectures.Distinct(StringComparer.Ordinal).Count() != entry.Architectures.Count ||
                    entry.Architectures.Any(a => !AllowedArchitectures.Contains(a, StringComparer.Ordinal)))
                {
                    throw Invalid($"index.json entry '{where}' has an invalid architectures list (x64/arm64/x86, unique, non-empty).");
                }

                foreach (var dependency in entry.Dependencies)
                {
                    if (!Identifiers.TrySplitReference(dependency.Id, out _, out _))
                    {
                        throw Invalid($"index.json entry '{where}' depends on invalid id '{dependency.Id}'.");
                    }

                    try
                    {
                        VersionOps.ValidateRange(dependency.Range, $"index.json entry '{where}'");
                    }
                    catch (WinBrewException ex)
                    {
                        throw Invalid(ex.Message);
                    }
                }
            }
        }
    }

    private static WinBrewException Invalid(string message) =>
        new(WinBrewErrorCode.VerificationFailure, message);
}

/// <summary>Strict source-generated context for signed repository metadata (A4).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(RepositoryIndex))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext
{
}
