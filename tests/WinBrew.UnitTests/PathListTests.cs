using WinBrew.Core;

namespace WinBrew.UnitTests;

public class PathListTests
{
    [Fact]
    public void Split_MissingValue_YieldsNoEntries() // A12 missing-value rule
    {
        Assert.Empty(PathList.Split(null));
        Assert.Empty(PathList.Split(string.Empty));
    }

    [Fact]
    public void Split_PreservesOrder()
    {
        var entries = PathList.Split(@"C:\A;C:\B;C:\C");
        Assert.Equal([@"C:\A", @"C:\B", @"C:\C"], entries);
    }

    [Fact]
    public void TryAppend_MissingValue_YieldsExactlyTheNewEntry()
    {
        var result = PathList.TryAppend(null, @"C:\WinBrew\bin");
        Assert.Equal(@"C:\WinBrew\bin", result.NewValue);
        Assert.False(result.AlreadyPresent);
        Assert.True(result.CanApply);
    }

    [Fact]
    public void TryAppend_KeepsOrderAndAppends()
    {
        var result = PathList.TryAppend(@"C:\A;C:\B", @"C:\WinBrew\bin");
        Assert.Equal(@"C:\A;C:\B;C:\WinBrew\bin", result.NewValue);
    }

    [Theory]
    [InlineData(@"C:\WinBrew\bin")]
    [InlineData(@"C:\WinBrew\BIN")]
    [InlineData(@"c:\winbrew\bin")]
    [InlineData(@"C:\WinBrew\bin\")]     // trailing slash normalizes for compare (A12)
    [InlineData(@"c:\WINBREW\bin\\\")]
    public void TryAppend_DetectsDuplicatesCaseAndSlashInsensitively(string existing)
    {
        var result = PathList.TryAppend(existing, @"C:\WinBrew\bin");
        Assert.True(result.AlreadyPresent);
        // The user's original value is preserved EXACTLY — never rewritten.
        Assert.Equal(existing, result.NewValue);
        Assert.True(result.CanApply);
    }

    [Fact]
    public void TryAppend_BeyondHardLimit_Refuses() // A12: 32,767 refusal
    {
        var big = new string('x', PathList.HardLimit);
        var result = PathList.TryAppend(big, @"C:\WinBrew\bin");
        Assert.True(result.ExceedsLimit);
        Assert.False(result.CanApply);
    }

    [Fact]
    public void TryAppend_BeyondWarnLimit_WarnsButAllows() // A12: 2,047 warning
    {
        var big = new string('x', PathList.WarnLimit);
        var result = PathList.TryAppend(big, @"C:\WinBrew\bin");
        Assert.False(result.ExceedsLimit);
        Assert.True(result.ShouldWarn);
        Assert.True(result.CanApply);
    }

    [Fact]
    public void TryRemove_RemovesOnlyTheExactEntry()
    {
        var newValue = PathList.TryRemove(@"C:\A;C:\WinBrew\bin;C:\B", @"C:\WinBrew\bin");
        Assert.Equal(@"C:\A;C:\B", newValue);
    }

    [Fact]
    public void TryRemove_MatchesNormalized_ButKeepsOthers()
    {
        var newValue = PathList.TryRemove(@"C:\A;c:\WINBREW\bin\;C:\B", @"C:\WinBrew\bin");
        Assert.Equal(@"C:\A;C:\B", newValue);
    }

    [Fact]
    public void TryRemove_NoMatch_ReturnsNull() // nothing removed (A12: only the exact entry)
    {
        Assert.Null(PathList.TryRemove(@"C:\A;C:\B", @"C:\WinBrew\bin"));
        Assert.Null(PathList.TryRemove(null, @"C:\WinBrew\bin"));
    }
}
