using NuGet.Versioning;

namespace Core;

public static class VersionOps
{
    // D3: version parses as NuGetVersion, normalized with ToNormalizedString()
    public static string Normalize(string v) => NuGetVersion.Parse(v).ToNormalizedString();
}
