using WinBrew.Core;

namespace WinBrew.Cli;

/// <summary>
/// The single place where WinBrew error codes become process exit codes and
/// names (D9). The enum values themselves are in WinBrew.Core
/// (WinBrewErrorCode) so every layer can throw typed errors; this class is
/// the authoritative mapping the CLI applies centrally — handlers never map
/// per call site. A unit test asserts this table against the D9 spec,
/// including InstallerBusy (13) and the Cancelled=130 convention.
/// </summary>
public static class ExitCodes
{
    public static int For(WinBrewErrorCode code) => (int)code;

    public static string Name(WinBrewErrorCode code) => code switch
    {
        WinBrewErrorCode.Success => "Success",
        WinBrewErrorCode.GeneralFailure => "GeneralFailure",
        WinBrewErrorCode.UsageError => "UsageError",
        WinBrewErrorCode.NotFound => "NotFound",
        WinBrewErrorCode.NetworkFailure => "NetworkFailure",
        WinBrewErrorCode.VerificationFailure => "VerificationFailure",
        WinBrewErrorCode.ConflictOrRefused => "ConflictOrRefused",
        WinBrewErrorCode.LockUnavailable => "LockUnavailable",
        WinBrewErrorCode.PartialOrRecoveryRequired => "PartialOrRecoveryRequired",
        WinBrewErrorCode.RebootRequired => "RebootRequired",
        WinBrewErrorCode.ConsentRequired => "ConsentRequired",
        WinBrewErrorCode.FilesInUse => "FilesInUse",
        WinBrewErrorCode.InsufficientSpace => "InsufficientSpace",
        WinBrewErrorCode.InstallerBusy => "InstallerBusy",
        WinBrewErrorCode.Cancelled => "Cancelled",
        _ => code.ToString(),
    };
}
