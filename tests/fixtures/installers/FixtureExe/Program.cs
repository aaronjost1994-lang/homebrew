// Minimal EXE installer fixture (Phase E, A16). Explicit switches only:
//   /S /install [/machine]   copy itself to the install dir + write the Uninstall key
//   /S /uninstall [/machine] remove both
// Per-user: %LOCALAPPDATA%\WinBrewFixtureExe + HKCU; per-machine: %ProgramFiles%\WinBrewFixtureExe + HKLM.
// Manifest detection: uninstallKey "WinBrewFixtureExe" + file {LocalAppData}/WinBrewFixtureExe/winbrew-fixture-setup.exe version 1.0.0.0.
using Microsoft.Win32;

var machine = args.Contains("/machine");
var root = Environment.GetFolderPath(machine ? Environment.SpecialFolder.ProgramFiles : Environment.SpecialFolder.LocalApplicationData);
var dir = Path.Combine(root, "WinBrewFixtureExe");
var target = Path.Combine(dir, "winbrew-fixture-setup.exe");
var hive = machine ? Registry.LocalMachine : Registry.CurrentUser;
const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinBrewFixtureExe";

if (!args.Contains("/S"))
{
    Console.Error.WriteLine("Silent mode only (/S).");
    return 2;
}

if (args.Contains("/install"))
{
    Directory.CreateDirectory(dir);
    File.Copy(Environment.ProcessPath!, target, overwrite: true);
    using var key = hive.CreateSubKey(Key);
    key.SetValue("DisplayName", "WinBrew Fixture EXE");
    key.SetValue("DisplayVersion", "1.0.0");
    return 0;
}

if (args.Contains("/uninstall"))
{
    hive.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
    // A running image cannot delete itself; schedule nothing, just remove what we can.
    try { if (!string.Equals(Environment.ProcessPath, target, StringComparison.OrdinalIgnoreCase)) File.Delete(target); }
    catch (IOException) { }
    return 0;
}

return 2;
