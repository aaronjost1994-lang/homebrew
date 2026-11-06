namespace WinBrew.Core.Installers;

/// <summary>One access-control entry as read from an opened directory handle.</summary>
/// <param name="Sid">String SID (e.g. S-1-5-32-544).</param>
/// <param name="Allow">Allow (true) or deny ACE.</param>
/// <param name="Mask">Raw ACCESS_MASK (generic rights NOT mapped).</param>
/// <param name="Inherited">Inherited from a parent (counts exactly like an explicit ACE).</param>
/// <param name="InheritOnly">Applies only to children (still refused: children would be writable).</param>
public sealed record AceSnapshot(string Sid, bool Allow, int Mask, bool Inherited, bool InheritOnly);

/// <summary>The owner, the DACL and the attributes of a directory, read from ONE handle opened with FILE_FLAG_OPEN_REPARSE_POINT.</summary>
public sealed record DirectorySecuritySnapshot(string OwnerSid, IReadOnlyList<AceSnapshot> Dacl, bool IsReparsePoint, bool DaclPresent = true);

/// <summary>
/// A12 helper rules for <c>%ProgramData%\WinBrew</c> and
/// <c>%ProgramData%\WinBrew\elevated</c>: a pre-existing directory is trusted
/// only if its owner is BUILTIN\Administrators or NT AUTHORITY\SYSTEM, it is
/// not a reparse point, it has a DACL, and NO principal other than those two
/// holds write, append, delete, WRITE_DAC or WRITE_OWNER (explicitly or
/// inherited, including generic rights). Anything else is REFUSED — never
/// repaired, never adopted.
/// </summary>
public static class DirectoryTrustRules
{
    public const string Administrators = "S-1-5-32-544";
    public const string LocalSystem = "S-1-5-18";

    // File/directory specific rights.
    public const int FileWriteData = 0x0002;       // FILE_ADD_FILE
    public const int FileAppendData = 0x0004;      // FILE_ADD_SUBDIRECTORY
    public const int FileWriteEa = 0x0010;
    public const int FileDeleteChild = 0x0040;
    public const int FileWriteAttributes = 0x0100;

    // Standard and generic rights.
    public const int Delete = 0x0001_0000;
    public const int WriteDac = 0x0004_0000;
    public const int WriteOwner = 0x0008_0000;
    public const int GenericAll = 0x1000_0000;
    public const int GenericWrite = 0x4000_0000;
    public const int MaximumAllowed = 0x0200_0000;

    /// <summary>Any of these bits in an allow ACE for an untrusted principal is a refusal.</summary>
    public const int DangerousMask = FileWriteData | FileAppendData | FileWriteEa | FileDeleteChild | FileWriteAttributes |
                                     Delete | WriteDac | WriteOwner | GenericAll | GenericWrite | MaximumAllowed;

    public static readonly IReadOnlySet<string> TrustedPrincipals = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Administrators, LocalSystem };

    /// <summary>Returns null when trusted, otherwise the reasons for refusal.</summary>
    public static IReadOnlyList<string>? Evaluate(DirectorySecuritySnapshot snapshot)
    {
        var problems = new List<string>();
        if (snapshot.IsReparsePoint)
        {
            problems.Add("it is a reparse point (junction or symbolic link)");
        }

        if (!TrustedPrincipals.Contains(snapshot.OwnerSid))
        {
            problems.Add($"its owner is {snapshot.OwnerSid}, not BUILTIN\\Administrators or NT AUTHORITY\\SYSTEM");
        }

        if (!snapshot.DaclPresent)
        {
            problems.Add("it has a NULL DACL (everyone has full access)");
        }

        foreach (var ace in snapshot.Dacl.Where(a => a.Allow && !TrustedPrincipals.Contains(a.Sid) && (a.Mask & DangerousMask) != 0))
        {
            problems.Add($"{ace.Sid} holds write/delete/WRITE_DAC/WRITE_OWNER rights (mask 0x{ace.Mask:X8}" +
                         $"{(ace.Inherited ? ", inherited" : "")}{(ace.InheritOnly ? ", inherit-only" : "")})");
        }

        return problems.Count == 0 ? null : problems;
    }

    /// <summary>The refusal message with remediation (A12: diagnose, never repair).</summary>
    public static string RefusalMessage(string path, IReadOnlyList<string> problems) =>
        $"Refusing to use '{path}': {string.Join("; ", problems)}. WinBrew never adopts or repairs a directory it did not create. " +
        $"Remediation: as an administrator, inspect and remove '{path}' (it may have been pre-created by another user), then retry (A12).";
}
