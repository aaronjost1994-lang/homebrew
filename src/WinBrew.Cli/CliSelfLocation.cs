using WinBrew.Application.Ports;

namespace WinBrew.Cli;

/// <summary>
/// Where the running wbrew lives (A14, D4). For the published single-file
/// bundle AppContext.BaseDirectory is the directory of wbrew.exe — the one
/// holding e_sqlite3.dll and wbrew-shim.exe (A4: nothing is extracted).
/// </summary>
public sealed class CliSelfLocation : ISelfLocation
{
    public string ExecutableDirectory { get; } = Path.GetFullPath(AppContext.BaseDirectory);

    public string ExecutablePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "wbrew.exe");
}
