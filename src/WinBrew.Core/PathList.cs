namespace WinBrew.Core;

/// <summary>
/// Pure PATH value string manipulation (A12). Registry I/O and
/// WM_SETTINGCHANGE live in WinBrew.Windows; this class holds only the
/// string rules so they are unit-testable everywhere:
/// missing-value handling, order preservation, case-insensitive comparison
/// with normalized trailing slashes, no duplicates, the 32,767 refusal and
/// the 2,047 warning limits.
/// </summary>
public static class PathList
{
    public const int HardLimit = 32767;
    public const int WarnLimit = 2047;

    /// <summary>Result of an append attempt.</summary>
    public sealed record AppendResult(string NewValue, bool AlreadyPresent, bool ExceedsLimit, bool ShouldWarn)
    {
        /// <summary>True when the append may be written (within the hard limit).</summary>
        public bool CanApply => !ExceedsLimit;
    }

    /// <summary>Splits a raw PATH value into entries, preserving order. Missing/empty → no entries.</summary>
    public static IReadOnlyList<string> Split(string? rawValue)
    {
        if (string.IsNullOrEmpty(rawValue))
        {
            return [];
        }

        return rawValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Normalizes an entry for comparison: trim, strip trailing separators, lowercase (A12).</summary>
    public static string NormalizeForCompare(string entry)
    {
        var trimmed = entry.Trim().TrimEnd('\\', '/');
        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Computes the new PATH value with <paramref name="binPath"/> appended.
    /// Never mutates; never adds a duplicate; keeps the order of existing
    /// entries (A12). A missing (null/empty) value yields exactly the new
    /// entry.
    /// </summary>
    public static AppendResult TryAppend(string? rawValue, string binPath)
    {
        var entries = Split(rawValue);
        var binNorm = NormalizeForCompare(binPath);
        var alreadyPresent = entries.Any(e => NormalizeForCompare(e) == binNorm);

        if (alreadyPresent)
        {
            var same = string.Join(";", entries);
            return new AppendResult(same, AlreadyPresent: true, ExceedsLimit: false, ShouldWarn: same.Length > WarnLimit);
        }

        var appended = entries.Append(binPath.Trim());
        var newValue = string.Join(";", appended);

        return new AppendResult(
            newValue,
            AlreadyPresent: false,
            ExceedsLimit: newValue.Length > HardLimit,
            ShouldWarn: newValue.Length > WarnLimit);
    }

    /// <summary>
    /// Removes an entry when it matches <paramref name="binPath"/>
    /// (case-insensitive, trailing-slash-normalized). Returns the new value
    /// or null when nothing was removed. Only the exact WinBrew entry is
    /// ever removed (A12).
    /// </summary>
    public static string? TryRemove(string? rawValue, string binPath)
    {
        var entries = Split(rawValue);
        var binNorm = NormalizeForCompare(binPath);
        var remaining = entries.Where(e => NormalizeForCompare(e) != binNorm).ToList();

        if (remaining.Count == entries.Count)
        {
            return null;
        }

        return string.Join(";", remaining);
    }
}
