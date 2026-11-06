using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WinBrew.Core.Installers;

/// <summary>
/// The typed request handed to WinBrew.ElevatedHelper (A12). It names an
/// operation, the artifact's source path and its expected SHA-256, plus the
/// typed parameters of that operation. It NEVER carries a command line, a
/// script or an executable path to run: the helper runs only its own copy of
/// the hash-verified artifact (install), msiexec /x with a ProductCode, or an
/// EXE uninstaller under a machine root resolved from a pattern.
/// </summary>
public sealed record ElevatedRequest(
    string SchemaVersion,
    string Operation,
    string? ArtifactPath = null,
    string? ArtifactSha256 = null,
    IReadOnlyDictionary<string, string>? MsiProperties = null,
    string? ProductCode = null,
    IReadOnlyList<string>? ExeArgs = null,
    ExeCommand? ExeUninstall = null);

/// <summary>The helper's typed result: installer exit code, log path, detected ProductCode.</summary>
// Optional members need defaults: the context omits nulls on write
// (WhenWritingNull) and RespectRequiredConstructorParameters makes every
// parameter WITHOUT a default required on read.
public sealed record ElevatedResult(string SchemaVersion, int? ExitCode = null, string? LogPath = null, string? ProductCode = null, string? Error = null);

/// <summary>Stores an installer declaration with the installed record (uninstall never depends on the catalog).</summary>
public static class InstallerDeclarationJson
{
    public static string Serialize(InstallerDeclaration declaration) =>
        JsonSerializer.Serialize(declaration, ElevatedJsonContext.Default.InstallerDeclaration);

    public static InstallerDeclaration Parse(string json) =>
        JsonSerializer.Deserialize(json, ElevatedJsonContext.Default.InstallerDeclaration)
        ?? throw new WinBrewException(WinBrewErrorCode.GeneralFailure, "The recorded installer declaration is empty.");
}

public static partial class ElevatedProtocol
{
    public const string MsiInstall = "msi-install";
    public const string MsiUninstall = "msi-uninstall";
    public const string ExeInstall = "exe-install";
    public const string ExeUninstall = "exe-uninstall";

    /// <summary>Request files are small; anything larger is refused before parsing.</summary>
    public const int MaxRequestBytes = 64 * 1024;

    public static readonly IReadOnlyList<string> Operations = [MsiInstall, MsiUninstall, ExeInstall, ExeUninstall];

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

    /// <summary>Request file name: request-&lt;32 hex&gt;.json (the helper rejects anything else).</summary>
    [GeneratedRegex("^request-[0-9a-f]{32}\\.json$")]
    public static partial Regex RequestFileNameRegex();

    public static string ResultPathFor(string requestPath) => Path.ChangeExtension(requestPath, ".result.json");

    public static string LogPathFor(string requestPath) => Path.ChangeExtension(requestPath, ".log");

    /// <summary>Strict validation of an UNTRUSTED request (unknown members rejected). Problems are VerificationFailure.</summary>
    public static ElevatedRequest Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxRequestBytes)
        {
            throw Invalid($"the request is {json.Length} bytes (max {MaxRequestBytes})");
        }

        ElevatedRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(json, ElevatedJsonContext.Default.ElevatedRequest);
        }
        catch (JsonException ex)
        {
            throw Invalid(ex.Message);
        }

        if (request is null || request.SchemaVersion != "1" || !Operations.Contains(request.Operation, StringComparer.Ordinal))
        {
            throw Invalid("unknown schemaVersion or operation");
        }

        var installs = request.Operation is MsiInstall or ExeInstall;
        if (installs)
        {
            if (request.ArtifactPath is null || !Path.IsPathFullyQualified(request.ArtifactPath) ||
                request.ArtifactSha256 is null || !Sha256Regex().IsMatch(request.ArtifactSha256))
            {
                throw Invalid("an install needs an absolute artifactPath and a lowercase hex artifactSha256");
            }

            var extension = request.Operation == MsiInstall ? ".msi" : ".exe";
            if (!request.ArtifactPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid($"the artifact of {request.Operation} must be a {extension} file");
            }
        }
        else if (request.ArtifactPath is not null || request.ArtifactSha256 is not null)
        {
            throw Invalid("an uninstall carries no artifact");
        }

        switch (request.Operation)
        {
            case MsiInstall:
                Forbid(request.ProductCode is not null || request.ExeArgs is not null || request.ExeUninstall is not null);
                foreach (var (k, v) in request.MsiProperties ?? new Dictionary<string, string>())
                {
                    if (!InstallerArguments.PropertyNameRegex().IsMatch(k) || !InstallerArguments.PropertyValueRegex().IsMatch(v))
                    {
                        throw Invalid($"MSI property '{k}' is not allowed");
                    }
                }

                break;
            case MsiUninstall:
                Forbid(request.MsiProperties is not null || request.ExeArgs is not null || request.ExeUninstall is not null);
                if (request.ProductCode is null || !InstallerArguments.GuidRegex().IsMatch(request.ProductCode))
                {
                    throw Invalid("msi-uninstall needs a ProductCode GUID");
                }

                break;
            case ExeInstall:
                Forbid(request.MsiProperties is not null || request.ProductCode is not null || request.ExeUninstall is not null);
                if (request.ExeArgs is null || request.ExeArgs.Count == 0 || !request.ExeArgs.All(InstallerArguments.IsSafeArgument))
                {
                    throw Invalid("exe-install needs explicit silent arguments without control characters");
                }

                break;
            case ExeUninstall:
                Forbid(request.MsiProperties is not null || request.ProductCode is not null || request.ExeArgs is not null);
                if (request.ExeUninstall is null || !request.ExeUninstall.Args.All(InstallerArguments.IsSafeArgument) ||
                    InstallerPaths.Split(request.ExeUninstall.Path, out _) is not { } split || !InstallerPaths.IsMachineRoot(split.Root) ||
                    !request.ExeUninstall.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    throw Invalid("exe-uninstall needs an .exe under a machine root ({ProgramFiles}, {ProgramFilesX86}, {ProgramData}) and safe arguments");
                }

                break;
        }

        return request;
    }

    public static byte[] Serialize(ElevatedRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, ElevatedJsonContext.Default.ElevatedRequest);

    public static byte[] Serialize(ElevatedResult result) => JsonSerializer.SerializeToUtf8Bytes(result, ElevatedJsonContext.Default.ElevatedResult);

    public static ElevatedResult ParseResult(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ElevatedJsonContext.Default.ElevatedResult) ?? throw Invalid("empty result");
        }
        catch (JsonException ex)
        {
            throw Invalid("result: " + ex.Message);
        }
    }

    private static void Forbid(bool present)
    {
        if (present)
        {
            throw Invalid("the request carries parameters that do not belong to its operation");
        }
    }

    private static WinBrewException Invalid(string reason) =>
        new(WinBrewErrorCode.VerificationFailure, $"Invalid elevated request: {reason} (A12).");
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ElevatedRequest))]
[JsonSerializable(typeof(ElevatedResult))]
[JsonSerializable(typeof(InstallerDeclaration))]
internal sealed partial class ElevatedJsonContext : JsonSerializerContext
{
}
