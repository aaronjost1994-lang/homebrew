using System.Runtime.InteropServices;
using System.Text;
using WinBrew.Application.Ports;

namespace WinBrew.Windows;

/// <summary>
/// Windows Credential Manager lookup (A13): the generic credential
/// <c>WinBrew:&lt;origin&gt;</c> (origin = scheme://host:port, lowercase).
/// A credential whose user name is empty or "Bearer" is sent as a bearer
/// token (the stored password); any other user name is sent as HTTP Basic.
/// The value is returned only for that exact origin; the HTTP client strips
/// it on any origin change.
/// </summary>
public sealed class CredentialManagerProvider : ICredentialProvider
{
    public const string TargetPrefix = "WinBrew:";

    public OriginCredential? GetForOrigin(string origin)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!CredReadW(TargetPrefix + origin, CredTypeGeneric, 0, out var handle))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(handle);
            var secret = credential.CredentialBlobSize > 0 && credential.CredentialBlob != IntPtr.Zero
                ? Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2)
                : string.Empty;
            var user = credential.UserName == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(credential.UserName) ?? string.Empty;

            if (secret.Length == 0)
            {
                return null;
            }

            return user.Length == 0 || string.Equals(user, "Bearer", StringComparison.OrdinalIgnoreCase)
                ? new OriginCredential("Bearer", secret)
                : new OriginCredential("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));
        }
        finally
        {
            CredFree(handle);
        }
    }

    private const int CredTypeGeneric = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
