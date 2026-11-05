using System.Text.Json.Serialization;
using WinBrew.Application.Catalog;
using WinBrew.Application.Doctor;
using WinBrew.Application.Lifecycle;
using WinBrew.Application.Packages;
using WinBrew.Application.SelfManagement;

namespace WinBrew.Cli.Output;

/// <summary>
/// The JSON output envelope (A8): exactly one JSON document on stdout with
/// --json, following schemas/cli-output.v1.schema.json. Versioned
/// separately from the manifest schema. With the context's WhenWritingNull,
/// a null Data is omitted from the wire — schema-valid on success (data is
/// not required) and REQUIRED to be absent on failure (schema allOf).
/// </summary>
public sealed record JsonEnvelope(
    string SchemaVersion,
    string Command,
    bool Ok,
    object? Data,
    IReadOnlyList<string> Warnings,
    JsonError? Error)
{
    public const string CurrentSchemaVersion = "1";

    public static JsonEnvelope Success(string command, object? data, IReadOnlyList<string>? warnings = null) =>
        new(CurrentSchemaVersion, command, Ok: true, data, warnings ?? [], Error: null);

    public static JsonEnvelope Failure(string command, int code, string name, string message, IReadOnlyDictionary<string, object?>? details, IReadOnlyList<string>? warnings = null) =>
        new(CurrentSchemaVersion, command, Ok: false, Data: null, warnings ?? [], new JsonError(code, name, message, details));
}

/// <summary>Structured error inside a failed envelope (A8).</summary>
public sealed record JsonError(
    int Code,
    string Name,
    string Message,
    IReadOnlyDictionary<string, object?>? Details);

// ---- data payloads (registered in the source-gen context) ----------------

public sealed record VersionData(string Version, string RuntimeIdentifier);

public sealed record ConfigGetData(string Key, string Value, bool IsDefault);

public sealed record ConfigSetData(string Key, string Value);

public sealed record ConfigValueRow(string Key, string Value, bool IsDefault);

public sealed record ConfigListData(IReadOnlyList<ConfigValueRow> Values);

/// <summary>Source-generated serializer context for CLI output types (A4).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(JsonEnvelope))]
[JsonSerializable(typeof(JsonError))]
[JsonSerializable(typeof(VersionData))]
[JsonSerializable(typeof(ConfigGetData))]
[JsonSerializable(typeof(ConfigSetData))]
[JsonSerializable(typeof(ConfigValueRow))]
[JsonSerializable(typeof(ConfigListData))]
[JsonSerializable(typeof(DoctorReport))]
[JsonSerializable(typeof(DoctorCheck))]
[JsonSerializable(typeof(InstallResult))]
[JsonSerializable(typeof(InstallPlan))]
[JsonSerializable(typeof(UninstallResult))]
[JsonSerializable(typeof(UninstallPlan))]
[JsonSerializable(typeof(ListResult))]
[JsonSerializable(typeof(InstalledRow))]
[JsonSerializable(typeof(InfoResult))]
[JsonSerializable(typeof(InfoVersion))]
[JsonSerializable(typeof(RecoverResult))]
[JsonSerializable(typeof(RecoveredOperation))]
[JsonSerializable(typeof(SelfInstallResult))]
[JsonSerializable(typeof(IngestResult))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(int))]
internal sealed partial class CliJsonContext : JsonSerializerContext
{
}
