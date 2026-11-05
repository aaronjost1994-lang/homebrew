using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinBrew.Core.Manifests;

/// <summary>
/// Typed package manifest model (A9). JSON shape is defined by
/// schemas/manifest.v1.schema.json; this model is deserialized with a
/// source-generated serializer context (A4). Rules beyond the JSON Schema
/// are enforced by <see cref="ManifestValidator"/>.
/// </summary>
public sealed record Manifest
{
    /// <summary>"1" (A9).</summary>
    public string SchemaVersion { get; init; } = "1";

    public required string Id { get; init; }

    /// <summary>Normalized NuGetVersion string (A9/D3) — strict: must already be normalized.</summary>
    public required string Version { get; init; }

    /// <summary>Optional, free-form, display-only (D3).</summary>
    public string? VendorVersion { get; init; }

    public required string DisplayName { get; init; }
    public required string Description { get; init; }

    /// <summary>HTTPS URL (A9).</summary>
    public required string Homepage { get; init; }

    /// <summary>SPDX expression (A9).</summary>
    public required string License { get; init; }

    /// <summary>"user" for portable; MSI/EXE may declare user and/or machine (v1.1, A9).</summary>
    public required IReadOnlyList<string> Scope { get; init; }

    /// <summary>Default false: true warns that state inside the install dir is lost on upgrade (A3/A9).</summary>
    public bool WritesToInstallDir { get; init; }

    public required IReadOnlyList<ManifestArtifact> Artifacts { get; init; }

    public IReadOnlyList<ManifestDependency> Dependencies { get; init; } = [];

    /// <summary>Portable packages only (A9).</summary>
    public IReadOnlyList<ManifestExecutable> Executables { get; init; } = [];

    /// <summary>
    /// Reserved for v1.1 (MSI/EXE installer declarations, A9/A12). Typed
    /// shape lands in Phase E; v1.0 validators reject any manifest that
    /// declares it. Represented as raw JSON to keep the v1 model honest
    /// about not interpreting installer content.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Installer { get; init; }
}

/// <summary>One per architecture (A9).</summary>
public sealed record ManifestArtifact
{
    /// <summary>x64 | arm64 | x86 (A9/A10).</summary>
    public required string Architecture { get; init; }

    public required string Url { get; init; }

    /// <summary>64 lowercase hex characters (A9).</summary>
    public required string Sha256 { get; init; }

    /// <summary>Size in bytes (A9). Client-side download.maxBytes applies on top (A11 step 3).</summary>
    public required long Size { get; init; }

    /// <summary>zip | msi | exe (A9).</summary>
    public required string Format { get; init; }

    /// <summary>portable | msi | exe (A9).</summary>
    public required string InstallType { get; init; }
}

/// <summary>Dependency reference as written in the manifest (A9/D6, kept verbatim).</summary>
public sealed record ManifestDependency
{
    /// <summary>"&lt;id&gt;" (declaring repo) or "&lt;repo&gt;/&lt;id&gt;" (cross-repo, A9).</summary>
    public required string Id { get; init; }

    /// <summary>NuGet interval notation only (D3).</summary>
    public required string Range { get; init; }
}

/// <summary>Shim declaration, portable packages only (A9).</summary>
public sealed record ManifestExecutable
{
    /// <summary>Shim name; WinBrew places bin\&lt;name&gt;.exe (A9/D4).</summary>
    public required string Name { get; init; }

    /// <summary>Relative path inside the package, '/' separators, must end .exe (A9).</summary>
    public required string Target { get; init; }
}
