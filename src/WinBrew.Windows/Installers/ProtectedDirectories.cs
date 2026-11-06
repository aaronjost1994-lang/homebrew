using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using WinBrew.Core;
using WinBrew.Core.Installers;

namespace WinBrew.Windows.Installers;

/// <summary>
/// The elevated helper's directory rules (A12) — never repair, never adopt:
/// <list type="number">
/// <item>Every path component is opened with FILE_FLAG_OPEN_REPARSE_POINT and
/// its attributes are read from the HANDLE (never by path); a reparse point
/// anywhere is a refusal.</item>
/// <item>An existing WinBrew directory is trusted only if the owner AND the
/// DACL read from that same handle pass <see cref="DirectoryTrustRules"/>.</item>
/// <item>A missing directory is created with CreateDirectoryW carrying its
/// FINAL security descriptor (owner BUILTIN\Administrators, protected DACL:
/// Administrators + SYSTEM full control only). ERROR_ALREADY_EXISTS re-runs
/// the checks on what now exists — never assumed new.</item>
/// <item>No ACL is ever applied to anything that already existed.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProtectedDirectories
{
    private const uint FileReadAttributes = 0x0080;
    private const uint ReadControl = 0x0002_0000;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileAttributeDirectory = 0x10;
    private const int ErrorAlreadyExists = 183;
    private const int SeFileObject = 1;
    private const int OwnerSecurityInformation = 0x1;
    private const int DaclSecurityInformation = 0x4;

    /// <summary>Opens <paramref name="path"/> as a directory handle WITHOUT following reparse points.</summary>
    public static SafeFileHandle OpenNoFollow(string path)
    {
        var handle = CreateFileW(path, FileReadAttributes | ReadControl, FileShareAll, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Cannot open '{path}'");
        }

        return handle;
    }

    /// <summary>Owner, DACL and reparse attribute of an OPEN handle (the A12 inputs).</summary>
    public static DirectorySecuritySnapshot Snapshot(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetFileInformationByHandle failed");
        }

        var reparse = (info.FileAttributes & FileAttributeReparsePoint) != 0 || (info.FileAttributes & FileAttributeDirectory) == 0;
        var status = GetSecurityInfo(handle, SeFileObject, OwnerSecurityInformation | DaclSecurityInformation,
            out _, out _, out var dacl, out _, out var descriptor);
        if (status != 0)
        {
            throw new Win32Exception(status, "GetSecurityInfo failed");
        }

        try
        {
            var length = GetSecurityDescriptorLength(descriptor);
            var bytes = new byte[length];
            Marshal.Copy(descriptor, bytes, 0, length);
            var security = new RawSecurityDescriptor(bytes, 0);
            var aces = new List<AceSnapshot>();
            foreach (var ace in security.DiscretionaryAcl ?? new RawAcl(0, 0))
            {
                if (ace is CommonAce common)
                {
                    aces.Add(new AceSnapshot(common.SecurityIdentifier.Value, common.AceQualifier == AceQualifier.AccessAllowed,
                        common.AccessMask, common.IsInherited, (common.AceFlags & AceFlags.InheritOnly) != 0));
                }
                else
                {
                    // Object or callback ACEs never appear on a directory WinBrew trusts: treat as a writable unknown.
                    aces.Add(new AceSnapshot("unknown-ace-" + ace.AceType, true, DirectoryTrustRules.GenericAll, ace.IsInherited, false));
                }
            }

            return new DirectorySecuritySnapshot(security.Owner?.Value ?? "", aces, reparse, DaclPresent: dacl != IntPtr.Zero);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>
    /// Ensures <paramref name="path"/> exists and is trusted: refuses reparse points in any
    /// component, refuses an untrusted existing directory, and creates a missing one with its
    /// final protected descriptor. Returns true when the helper created it.
    /// </summary>
    public static bool EnsureTrustedOrCreate(string path)
    {
        RefuseReparsePointsInParents(path);
        if (Directory.Exists(path) || File.Exists(path))
        {
            CheckExisting(path);
            return false;
        }

        if (CreateProtected(path))
        {
            return true;
        }

        // ERROR_ALREADY_EXISTS: something was created between our check and our create — check it, never adopt it.
        CheckExisting(path);
        return false;
    }

    /// <summary>The helper's random &lt;op&gt; directory under a trusted parent (never from the request).</summary>
    public static string CreateOperationDirectory(string parent)
    {
        var path = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        if (!CreateProtected(path))
        {
            throw new WinBrewException(WinBrewErrorCode.GeneralFailure, $"'{path}' already exists; refusing to use it (A12).");
        }

        return path;
    }

    public static void CheckExisting(string path)
    {
        using var handle = OpenNoFollow(path);
        var problems = DirectoryTrustRules.Evaluate(Snapshot(handle));
        if (problems is not null)
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure, DirectoryTrustRules.RefusalMessage(path, problems),
                new Dictionary<string, object?> { ["path"] = path, ["problems"] = problems });
        }
    }

    /// <summary>The final security descriptor: owner Administrators; protected DACL granting only Administrators and SYSTEM.</summary>
    public static byte[] ProtectedDescriptor()
    {
        var security = new DirectorySecurity();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { admins, system })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security.GetSecurityDescriptorBinaryForm();
    }

    private static bool CreateProtected(string path)
    {
        var descriptor = ProtectedDescriptor();
        var buffer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, buffer, descriptor.Length);
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = buffer, InheritHandle = 0 };
            if (CreateDirectoryW(path, ref attributes))
            {
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            if (error == ErrorAlreadyExists)
            {
                return false;
            }

            throw new Win32Exception(error, $"CreateDirectoryW('{path}') failed");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void RefuseReparsePointsInParents(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).SkipLast(1))
        {
            current = Path.Combine(current, segment);
            using var handle = OpenNoFollow(current);
            if (!GetFileInformationByHandle(handle, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetFileInformationByHandle failed");
            }

            if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                    DirectoryTrustRules.RefusalMessage(current, ["a path component is a reparse point (junction or symbolic link)"]));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);

    [DllImport("advapi32.dll")]
    private static extern int GetSecurityInfo(SafeFileHandle handle, int objectType, int securityInfo,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll")]
    private static extern int GetSecurityDescriptorLength(IntPtr descriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
