using System.Buffers;

namespace WinBrew.Core.Archive;

/// <summary>Why an archive entry name was rejected (A13, one value per defense).</summary>
public enum ArchiveDefense
{
    EmptyName,
    AbsolutePath,
    DriveLetter,
    UncPath,
    DotDotSegment,
    SeparatorAmbiguity,
    ReservedDeviceName,
    AlternateDataStream,
    TrailingDotOrSpace,
    InvalidCharacter,
    CaseInsensitiveDuplicate,
    SymbolicLink,
    TooManyEntries,
    TotalSizeExceeded,
    EntryCompressionRatio,
    TotalCompressionRatio,
    EscapesStagingRoot,
    ReparsePoint,
    Encrypted,
}

/// <summary>Result of validating one archive entry name.</summary>
public sealed record ArchiveNameCheck(bool Ok, string? NormalizedPath, bool IsDirectory, ArchiveDefense? Defense, string? Problem)
{
    public static ArchiveNameCheck Reject(ArchiveDefense defense, string problem) => new(false, null, false, defense, problem);
}

/// <summary>
/// Pure ZIP entry-name rules (A13). No I/O: the extractor in
/// WinBrew.Infrastructure calls these for every entry, then enforces the
/// size/ratio limits on actual decompressed bytes and the post-extraction
/// containment and reparse-point walk.
/// </summary>
public static class ArchiveEntryRules
{
    /// <summary>Unix file-type bits in the high word of the ZIP external attributes.</summary>
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymlinkType = 0xA000;

    private static readonly SearchValues<char> InvalidNameChars = SearchValues.Create("<>\"|?*");

    /// <summary>
    /// Validates and normalizes one entry name. Backslashes are normalized to
    /// '/' and the result is re-checked (A13: backslash/forward-slash
    /// ambiguity). Directory entries end with '/'.
    /// </summary>
    public static ArchiveNameCheck CheckName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
        {
            return ArchiveNameCheck.Reject(ArchiveDefense.EmptyName, "empty entry name");
        }

        if (fullName.StartsWith(@"\\", StringComparison.Ordinal) || fullName.StartsWith("//", StringComparison.Ordinal))
        {
            return ArchiveNameCheck.Reject(ArchiveDefense.UncPath, $"UNC path '{fullName}'");
        }

        if (fullName.Length >= 2 && char.IsAsciiLetter(fullName[0]) && fullName[1] == ':')
        {
            return ArchiveNameCheck.Reject(ArchiveDefense.DriveLetter, $"drive-letter path '{fullName}'");
        }

        if (fullName[0] is '/' or '\\')
        {
            return ArchiveNameCheck.Reject(ArchiveDefense.AbsolutePath, $"absolute path '{fullName}'");
        }

        // Normalize, then re-check everything on the normalized form.
        var normalized = fullName.Replace('\\', '/');
        var isDirectory = normalized.EndsWith('/');
        var body = isDirectory ? normalized[..^1] : normalized;

        if (body.Length == 0)
        {
            return ArchiveNameCheck.Reject(ArchiveDefense.EmptyName, $"entry '{fullName}' has no name");
        }

        if (body.StartsWith('/'))
        {
            return ArchiveNameCheck.Reject(ArchiveDefense.AbsolutePath, $"absolute path '{fullName}'");
        }

        var segments = body.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return ArchiveNameCheck.Reject(ArchiveDefense.SeparatorAmbiguity,
                    $"entry '{fullName}' has an empty path segment (separator ambiguity)");
            }

            if (segment is "." or "..")
            {
                return ArchiveNameCheck.Reject(ArchiveDefense.DotDotSegment, $"entry '{fullName}' contains a '{segment}' segment");
            }

            if (segment.Contains(':', StringComparison.Ordinal))
            {
                return ArchiveNameCheck.Reject(ArchiveDefense.AlternateDataStream,
                    $"entry '{fullName}' contains ':' (alternate data stream or drive)");
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return ArchiveNameCheck.Reject(ArchiveDefense.TrailingDotOrSpace,
                    $"entry '{fullName}' has a segment ending in a dot or space");
            }

            if (segment.AsSpan().ContainsAny(InvalidNameChars) || segment.Any(char.IsControl))
            {
                return ArchiveNameCheck.Reject(ArchiveDefense.InvalidCharacter,
                    $"entry '{fullName}' contains a character that is invalid in Windows file names");
            }

            if (IsReservedDeviceName(segment))
            {
                return ArchiveNameCheck.Reject(ArchiveDefense.ReservedDeviceName,
                    $"entry '{fullName}' uses the reserved device name '{segment}'");
            }
        }

        return new ArchiveNameCheck(true, body, isDirectory, null, null);
    }

    /// <summary>
    /// Reserved device names in any segment, with or without extensions
    /// (A13): "CON", "con.txt", "COM1.tar.gz", "CONIN$"… Windows also strips
    /// trailing spaces before the extension check ("CON .txt").
    /// </summary>
    public static bool IsReservedDeviceName(string segment)
    {
        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ');
        return Identifiers.ReservedDeviceNames.Contains(stem.ToUpperInvariant());
    }

    /// <summary>True when the ZIP external attributes carry Unix symlink mode bits (A13).</summary>
    public static bool HasUnixSymlinkMode(int externalAttributes)
    {
        var unixMode = (externalAttributes >> 16) & 0xFFFF;
        return (unixMode & UnixFileTypeMask) == UnixSymlinkType;
    }

    /// <summary>
    /// Case-insensitive duplicate key (A13). Also used to detect a file and a
    /// directory claiming the same path ("a" vs "a/").
    /// </summary>
    public static string DuplicateKey(string normalizedPath) => normalizedPath.ToUpperInvariant();

    /// <summary>
    /// Containment: <paramref name="candidateFullPath"/> must be strictly
    /// inside <paramref name="rootFullPath"/> after full normalization with a
    /// trailing separator (A13). Both inputs must already be full paths.
    /// </summary>
    public static bool IsContained(string rootFullPath, string candidateFullPath, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = rootFullPath.EndsWith(Path.DirectorySeparatorChar) ? rootFullPath : rootFullPath + Path.DirectorySeparatorChar;
        return candidateFullPath.StartsWith(root, comparison) && candidateFullPath.Length > root.Length;
    }
}

/// <summary>Archive limits (D10 archive.* keys).</summary>
public sealed record ArchiveLimits(int MaxEntries, long MaxTotalBytes, int MaxCompressionRatio);
