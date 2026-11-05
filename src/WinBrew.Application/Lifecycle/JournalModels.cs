using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinBrew.Application.Lifecycle;

// Intent/outcome payloads written into operation_steps (A11). Intent is
// written BEFORE each filesystem action and carries everything recovery
// needs to roll forward or compensate that action idempotently.

/// <summary>The "plan" step: what the operation is about (first step of every operation).</summary>
public sealed record PlanIntent(
    string Repo,
    string Id,
    string Version,
    string Arch,
    string? PreviousVersion,
    string? PreviousArch,
    string StagingDirectory);

public sealed record StagingIntent(string StagingDirectory);

public sealed record ArtifactIntent(string Url, string Sha256, long Size);

public sealed record PlaceIntent(string PackageDirectory, string ExtractDirectory, string? DisplacedDirectory);

/// <summary>Per-shim sidecar step: the previous sidecar (base64) restores on compensation; null = the shim is new.</summary>
public sealed record SidecarIntent(string Name, string? PreviousSidecarBase64, string NewSidecarBase64);

/// <summary>Per-shim binary step: BinaryExisted=false means compensation deletes the binary we created.</summary>
public sealed record BinaryIntent(string Name, bool BinaryExisted);

/// <summary>Post-commit work (roll-forward only).</summary>
public sealed record CommitIntent(
    IReadOnlyList<string> StaleShims,
    IReadOnlyList<string> PruneDirectories,
    string? StagingDirectory);

public sealed record UninstallIntent(
    string Repo,
    string Id,
    string Version,
    IReadOnlyList<string> Shims,
    string PackageRoot);

public sealed record ShimRemoveIntent(string Name);

public sealed record SelfInstallIntent(
    string Version,
    string AppDirectory,
    string TemporaryDirectory,
    bool AppDirectoryExisted,
    bool ModifyPath,
    string BinDirectory);

public sealed record PathIntent(string Entry);

public sealed record StepOutcome(string Result, string? Detail = null)
{
    public static readonly StepOutcome Done = new("done");
}

/// <summary>Source-generated context for journal payloads (A4).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlanIntent))]
[JsonSerializable(typeof(StagingIntent))]
[JsonSerializable(typeof(ArtifactIntent))]
[JsonSerializable(typeof(PlaceIntent))]
[JsonSerializable(typeof(SidecarIntent))]
[JsonSerializable(typeof(BinaryIntent))]
[JsonSerializable(typeof(CommitIntent))]
[JsonSerializable(typeof(UninstallIntent))]
[JsonSerializable(typeof(ShimRemoveIntent))]
[JsonSerializable(typeof(SelfInstallIntent))]
[JsonSerializable(typeof(PathIntent))]
[JsonSerializable(typeof(StepOutcome))]
internal sealed partial class JournalJsonContext : JsonSerializerContext
{
}

internal static class JournalJson
{
    public static string Write<T>(T value) =>
        JsonSerializer.Serialize(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)JournalJsonContext.Default.GetTypeInfo(typeof(T))!);

    public static T Read<T>(string json) =>
        JsonSerializer.Deserialize(json, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)JournalJsonContext.Default.GetTypeInfo(typeof(T))!)
        ?? throw new InvalidDataException($"Journal payload for {typeof(T).Name} is null.");
}
