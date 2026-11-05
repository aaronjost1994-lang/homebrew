using System.Diagnostics.CodeAnalysis;

namespace WinBrew.Core;

/// <summary>
/// Package and repository identifier rules (A9).
/// Package identity is <c>&lt;repo&gt;/&lt;id&gt;</c>; an unqualified id always
/// belongs to the repository of the package that declares it.
/// </summary>
public static class Identifiers
{
    /// <summary>Normalized lowercase package id (A9).</summary>
    public const string PackageIdPattern = @"^[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?$";

    /// <summary>Repository short name (A9).</summary>
    public const string RepoNamePattern = @"^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$";

    private static readonly System.Text.RegularExpressions.Regex PackageIdRegex =
        new(PackageIdPattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex RepoNameRegex =
        new(RepoNamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Reserved Windows device names (A9/A13).</summary>
    public static readonly IReadOnlySet<string> ReservedDeviceNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "CONIN$", "CONOUT$",
    };

    /// <summary>Validates a package id per A9 (normalized lowercase, regex, no "..", no reserved device names).</summary>
    public static bool IsValidPackageId(string id)
    {
        if (string.IsNullOrEmpty(id) || !PackageIdRegex.IsMatch(id))
        {
            return false;
        }

        if (id.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var baseName = id.Split('.', StringSplitOptions.RemoveEmptyEntries)[0];
        return !ReservedDeviceNames.Contains(baseName.ToUpperInvariant());
    }

    /// <summary>Validates a repository short name per A9.</summary>
    public static bool IsValidRepoName(string name) =>
        !string.IsNullOrEmpty(name) && RepoNameRegex.IsMatch(name);

    /// <summary>
    /// Parses a package reference into its repository and id parts (A9).
    /// Returns false when the string is not a valid reference of either form.
    /// On success <paramref name="id"/> is always non-null; <paramref name="repo"/>
    /// is null for unqualified references (they belong to the declaring
    /// repository, A9).
    /// </summary>
    /// <param name="reference">"id" (unqualified) or "repo/id" (qualified).</param>
    public static bool TrySplitReference(
        string reference,
        out string? repo,
        [NotNullWhen(true)] out string? id)
    {
        repo = null;
        id = null;

        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var slash = reference.IndexOf('/');
        if (slash < 0)
        {
            if (IsValidPackageId(reference))
            {
                repo = null; // unqualified: belongs to the declaring repository (A9)
                id = reference;
                return true;
            }

            return false;
        }

        var repoPart = reference[..slash];
        var idPart = reference[(slash + 1)..];

        if (IsValidRepoName(repoPart) && IsValidPackageId(idPart))
        {
            repo = repoPart;
            id = idPart;
            return true;
        }

        return false;
    }

    /// <summary>Formats a repository-qualified identity <c>&lt;repo&gt;/&lt;id&gt;</c> (A9, D2 explanations).</summary>
    public static string Qualified(string repo, string id) => $"{repo}/{id}";
}
