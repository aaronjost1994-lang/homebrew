namespace WinBrew.Core;

/// <summary>
/// WinBrew exit/error codes (D9). The single authoritative mapping between
/// these values and their names lives in <c>WinBrew.Cli/ExitCodes.cs</c>;
/// the unit test suite asserts the two stay in sync.
/// </summary>
public enum WinBrewErrorCode
{
    Success = 0,
    GeneralFailure = 1,
    UsageError = 2,
    NotFound = 3,
    NetworkFailure = 4,
    VerificationFailure = 5,
    ConflictOrRefused = 6,
    LockUnavailable = 7,
    PartialOrRecoveryRequired = 8,
    RebootRequired = 9,
    ConsentRequired = 10,
    FilesInUse = 11,
    InsufficientSpace = 12,
    InstallerBusy = 13,

    /// <summary>POSIX cancel convention, kept for CLI ergonomics; the deviation from the
    /// Windows 0xC000013A convention is documented in cli-reference.md (D9).</summary>
    Cancelled = 130,
}
