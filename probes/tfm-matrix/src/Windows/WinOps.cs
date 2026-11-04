using Microsoft.Win32; // compile-only check: does net10.0-windows expose Registry without packages?

namespace Windows;

public static class WinOps
{
    public static string ReadPath() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Environment", "Path", "")?.ToString() ?? "";
}
