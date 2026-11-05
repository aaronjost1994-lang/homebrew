using NuGet.Versioning;

namespace WinBrew.Core;

/// <summary>
/// Version and range handling (D3). Versions are <see cref="NuGetVersion"/>,
/// normalized with <c>ToNormalizedString()</c>. Dependency ranges use NuGet
/// interval notation only; a bare version ("1.2.3") is rejected because NuGet
/// would read it as "&gt;= 1.2.3", which surprises people.
/// </summary>
public static class VersionOps
{
    /// <summary>Parses and normalizes a single version (D3).</summary>
    /// <exception cref="WinBrewException">UsageError when the version does not parse.</exception>
    public static string NormalizeVersion(string version)
    {
        if (!NuGetVersion.TryParse(version, out var parsed))
        {
            throw new WinBrewException(
                WinBrewErrorCode.UsageError,
                $"'{version}' is not a valid version (NuGetVersion syntax required).");
        }

        return parsed.ToNormalizedString();
    }

    /// <summary>Parses a single version; no exceptions. Used on hot paths and by tests.</summary>
    public static bool TryNormalizeVersion(string version, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (!NuGetVersion.TryParse(version, out var parsed))
        {
            return false;
        }

        normalized = parsed.ToNormalizedString();
        return true;
    }

    /// <summary>
    /// Validates a dependency range string (D3): NuGet interval notation only.
    /// Rejects bare versions, floating versions ("1.*") and anything
    /// <see cref="VersionRange"/> cannot parse. The original string is kept
    /// verbatim in metadata and explanations (D2/D6).
    /// </summary>
    /// <exception cref="WinBrewException">UsageError on invalid ranges.</exception>
    public static VersionRange ValidateRange(string range, string context)
    {
        var trimmed = range.Trim();
        if (trimmed.Length == 0)
        {
            throw new WinBrewException(WinBrewErrorCode.UsageError, $"Empty version range in {context}.");
        }

        if (trimmed[0] is not ('[' or '('))
        {
            throw new WinBrewException(
                WinBrewErrorCode.UsageError,
                $"Version range '{range}' in {context} must use NuGet interval notation " +
                "(e.g. '[1.0,2.0)', '[1.2.3]', '(,3.0)'). A bare version is rejected " +
                "because NuGet would read it as '>='.");
        }

        if (trimmed.Contains('*', StringComparison.Ordinal))
        {
            throw new WinBrewException(
                WinBrewErrorCode.UsageError,
                $"Version range '{range}' in {context} uses floating versions ('*'), " +
                "which WinBrew does not allow (interval notation only, D3).");
        }

        if (!VersionRange.TryParse(trimmed, allowFloating: false, out var parsed) || parsed is null)
        {
            throw new WinBrewException(
                WinBrewErrorCode.UsageError,
                $"Version range '{range}' in {context} is not a valid NuGet interval.");
        }

        return parsed;
    }

    /// <summary>True when the range's lower bound (or only bound) is a prerelease version (D2 rule 6).</summary>
    public static bool RangeRequestsPrerelease(VersionRange range) =>
        range.MinVersion?.IsPrerelease == true || range.MaxVersion?.IsPrerelease == true;
}
