namespace WinBrew.Shim;

/// <summary>
/// The pure D4 rules of the shim, free of any Windows API so the unit tests
/// can link this file and run the quoting corpus anywhere.
/// </summary>
internal static class ShimRules
{
    /// <summary>CreateProcessW's lpCommandLine limit: 32,767 characters INCLUDING the terminating NUL.</summary>
    public const int MaxCommandLineChars = 32767;

    public const int ExitTargetMissing = 9009;          // cmd.exe "not recognized" (D4)
    public const int ExitCommandLineTooLong = 206;      // ERROR_FILENAME_EXCED_RANGE (D4)

    /// <summary>
    /// Strips argv[0] with the MSVC C-runtime rules (D4): '"' toggles quoting
    /// anywhere inside argv[0], backslashes have NO special meaning, and the
    /// first space or tab outside quotes ends it. Returns the tail exactly as
    /// it appears in the raw command line — empty, or starting with ' ' or
    /// '\t'. There is no separator insertion: characters after a closing
    /// quote still belong to argv[0] (<c>"x.exe"arg</c> forwards nothing).
    /// An empty (or null) command line yields an empty tail.
    /// </summary>
    public static string Tail(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return string.Empty;
        }

        var inQuote = false;
        var i = 0;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if ((c == ' ' || c == '\t') && !inQuote)
            {
                break;
            }

            i++;
        }

        return commandLine[i..];
    }

    /// <summary>True when the tail obeys the D4 invariant (empty or whitespace-led).</summary>
    public static bool IsWellFormedTail(string tail) => tail.Length == 0 || tail[0] == ' ' || tail[0] == '\t';

    /// <summary>"\"&lt;targetPath&gt;\"" + tail — never re-parsed, never re-quoted (D4).</summary>
    public static string Compose(string targetPath, string tail) => "\"" + targetPath + "\"" + tail;

    /// <summary>The length check before CreateProcessW (D4): the string plus its NUL must fit.</summary>
    public static bool ExceedsLimit(string composed) => composed.Length + 1 > MaxCommandLineChars;

    /// <summary>
    /// Sidecar containment (D4), path-string part: <paramref name="target"/>
    /// must be absolute and resolve strictly inside <paramref name="allowedRoot"/>
    /// after full normalization with a trailing separator. Comparison is
    /// case-insensitive (Windows paths). The reparse-point walk is done by
    /// the caller on the real file system.
    /// </summary>
    public static bool IsContained(string allowedRoot, string target, out string normalizedTarget)
    {
        normalizedTarget = string.Empty;
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target))
        {
            return false;
        }

        var root = Path.GetFullPath(allowedRoot);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        normalizedTarget = Path.GetFullPath(target);
        return normalizedTarget.Length > root.Length &&
               normalizedTarget.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The containment root for a shim (D4): always the parent of the shim's
    /// own directory (bin\..) — never WINBREW_ROOT or any other environment
    /// variable. The self-shim (sidecar wbrew.shim.json) targets app\, every
    /// other shim targets packages\.
    /// </summary>
    public static string AllowedRoot(string shimExecutablePath, string shimName)
    {
        var binDirectory = Path.GetDirectoryName(Path.GetFullPath(shimExecutablePath))!;
        var root = Path.GetDirectoryName(binDirectory) ?? binDirectory;
        var isSelfShim = string.Equals(shimName, "wbrew", StringComparison.OrdinalIgnoreCase);
        return Path.Combine(root, isSelfShim ? "app" : "packages");
    }

    /// <summary>Every path prefix of <paramref name="fullPath"/> below the volume root (for the reparse walk).</summary>
    public static IEnumerable<string> PathPrefixes(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = root;
        foreach (var part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            yield return current;
        }
    }
}
