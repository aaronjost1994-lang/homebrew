using System.Reflection;
using System.Runtime.InteropServices;
using WinBrew.Application.Ports;

namespace WinBrew.Cli;

/// <summary>
/// Build identity from assembly metadata. The RID is inferred from the
/// platform+architecture of the running binary (the official RID is not
/// embedded in .NET assemblies); for published WinBrew builds (win-x64,
/// win-arm64, self-contained per A4) this inference is exact.
/// </summary>
public sealed class CliVersionInfo : IVersionInfo
{
    public string Version { get; }

    public string RuntimeIdentifier { get; }

    public CliVersionInfo()
    {
        Version = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? "0.0.0-dev";

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture.X86 => "x86",
            _ => "x64",
        };

        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "unknown";

        RuntimeIdentifier = $"{os}-{arch}";
    }
}
