using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinBrew.Core.Shims;

/// <summary>
/// bin\&lt;name&gt;.shim.json (D4): the target path, the owning package ID and
/// version, and the subsystem. The Native AOT shim parses exactly these
/// four string members (WinBrew.Shim/SidecarReader.cs) — keep both sides in
/// sync; the unit tests round-trip one through the other.
/// </summary>
public sealed record ShimSidecar(string Target, string Package, string Version, string Subsystem)
{
    public const string Console = "console";
    public const string Gui = "gui";

    /// <summary>The WinBrew self-shim sidecar name — the only one allowed to target app\ (D4).</summary>
    public const string SelfShimName = "wbrew";

    public const string SidecarSuffix = ".shim.json";

    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, ShimJsonContext.Default.ShimSidecar);

    public static ShimSidecar? TryParse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, ShimJsonContext.Default.ShimSidecar);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ShimSidecar))]
internal sealed partial class ShimJsonContext : JsonSerializerContext
{
}
