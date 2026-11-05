namespace WinBrew.Application.Ports;

/// <summary>Build identity of the running binary (fast-path --version, doctor).</summary>
public interface IVersionInfo
{
    /// <summary>Semantic version of the running wbrew, from assembly metadata.</summary>
    string Version { get; }

    /// <summary>The RID the binary was built for (e.g. win-x64), when known.</summary>
    string RuntimeIdentifier { get; }
}
