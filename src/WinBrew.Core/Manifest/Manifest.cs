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
    /// MSI/EXE installer declaration (v1.1, A9/A12) — an executable-code
    /// trust decision, not safe data. Required exactly when the artifacts
    /// are msi/exe; validated by <see cref="ManifestValidator"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Installers.InstallerDeclaration? Installer { get; init; }

    /// <summary>The install type shared by every artifact (portable | msi | exe).</summary>
    [JsonIgnore]
    public string InstallType => Artifacts.Count > 0 ? Artifacts[0].InstallType : "portable";
}

/// <summary>
/// One per architecture (A9): architecture (x64 | arm64 | x86), url (HTTPS),
/// sha256 (64 lowercase hex), size (bytes; client-side download.maxBytes
/// applies on top, A11 step 3), format (zip | msi | exe), installType
/// (portable | msi | exe).
/// </summary>
public sealed record ManifestArtifact(
    string Architecture,
    string Url,
    string Sha256,
    long Size,
    string Format,
    string InstallType);

/// <summary>
/// Dependency reference as written in the manifest (A9/D6, kept verbatim):
/// id = "&lt;id&gt;" (declaring repo) or "&lt;repo&gt;/&lt;id&gt;" (cross-repo); range =
/// NuGet interval notation only (D3).
/// </summary>
public sealed record ManifestDependency(string Id, string Range);

/// <summary>
/// Shim declaration, portable packages only (A9): name = shim name
/// (binary becomes bin\&lt;name&gt;.exe, A9/D4); target = relative path inside
/// the package, '/' separators, must end .exe.
/// </summary>
public sealed record ManifestExecutable(string Name, string Target);
