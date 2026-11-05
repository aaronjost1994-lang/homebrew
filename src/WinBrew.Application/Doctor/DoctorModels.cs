namespace WinBrew.Application.Doctor;

/// <summary>Status of one doctor check.</summary>
public enum DoctorStatus
{
    Ok,
    Warn,
    Fail,
}

/// <summary>One doctor check result (A8: wbrew doctor [--json]).</summary>
public sealed record DoctorCheck(string Name, DoctorStatus Status, string Detail);

/// <summary>Full doctor report (rendered as text or the JSON envelope's data).</summary>
public sealed record DoctorReport(
    string WinBrewVersion,
    string Root,
    IReadOnlyList<DoctorCheck> Checks)
{
    /// <summary>True when every check is Ok or Warn (doctor exits 0); any Fail means a nonzero exit (1).</summary>
    public bool Healthy => Checks.All(c => c.Status != DoctorStatus.Fail);
}
