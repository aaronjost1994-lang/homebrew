using WinBrew.Core;
using WinBrew.Cli;

namespace WinBrew.UnitTests;

/// <summary>
/// The D9 exit-code table (A16: including 13). Cli.ExitCodes is the single
/// mapping place; the enum in Core must agree with it exactly.
/// </summary>
public class ExitCodesTests
{
    public static TheoryData<WinBrewErrorCode, int, string> Table => new()
    {
        { WinBrewErrorCode.Success, 0, "Success" },
        { WinBrewErrorCode.GeneralFailure, 1, "GeneralFailure" },
        { WinBrewErrorCode.UsageError, 2, "UsageError" },
        { WinBrewErrorCode.NotFound, 3, "NotFound" },
        { WinBrewErrorCode.NetworkFailure, 4, "NetworkFailure" },
        { WinBrewErrorCode.VerificationFailure, 5, "VerificationFailure" },
        { WinBrewErrorCode.ConflictOrRefused, 6, "ConflictOrRefused" },
        { WinBrewErrorCode.LockUnavailable, 7, "LockUnavailable" },
        { WinBrewErrorCode.PartialOrRecoveryRequired, 8, "PartialOrRecoveryRequired" },
        { WinBrewErrorCode.RebootRequired, 9, "RebootRequired" },
        { WinBrewErrorCode.ConsentRequired, 10, "ConsentRequired" },
        { WinBrewErrorCode.FilesInUse, 11, "FilesInUse" },
        { WinBrewErrorCode.InsufficientSpace, 12, "InsufficientSpace" },
        { WinBrewErrorCode.InstallerBusy, 13, "InstallerBusy" },  // A16: including 13
        { WinBrewErrorCode.Cancelled, 130, "Cancelled" },        // POSIX convention per D9
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Mapping_MatchesTheD9Table(WinBrewErrorCode code, int exitCode, string name)
    {
        Assert.Equal(exitCode, ExitCodes.For(code));
        Assert.Equal(name, ExitCodes.Name(code));
        Assert.Equal(name, code.ToString()); // enum member names ARE the D9 names
    }

    [Fact]
    public void Table_CoversEveryErrorCode()
    {
        var enumValues = Enum.GetValues<WinBrewErrorCode>().ToHashSet();
        var tableCodes = Table.Select(t => (WinBrewErrorCode)t[0]).ToHashSet();
        Assert.Equal(enumValues, tableCodes); // no unlisted code may sneak in
    }
}
