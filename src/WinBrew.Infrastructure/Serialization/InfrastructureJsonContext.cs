using System.Text.Json.Serialization;
using WinBrew.Infrastructure.Config;
using WinBrew.Infrastructure.Locking;

namespace WinBrew.Infrastructure;

/// <summary>Source-generated serializer context for Infrastructure types (A4).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConfigFileModel))]
[JsonSerializable(typeof(LockMetadata))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(string))]
internal sealed partial class InfrastructureJsonContext : JsonSerializerContext
{
}
