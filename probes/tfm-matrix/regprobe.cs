#:package Microsoft.Win32.Registry@5.0.0
using Microsoft.Win32;
try { var v = Registry.GetValue(@"HKEY_CURRENT_USER\Environment", "Path", ""); Console.WriteLine($"returned: {v}"); }
catch (Exception ex) { Console.WriteLine($"THREW: {ex.GetType().Name}: {ex.Message}"); }
