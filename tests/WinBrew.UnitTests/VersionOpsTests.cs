using NuGet.Versioning;
using WinBrew.Core;

namespace WinBrew.UnitTests;

public class VersionOpsTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.2.3-beta.1", "1.2.3-beta.1")]
    [InlineData("2.0.0-rc.2+build.5", "2.0.0-rc.2")]  // metadata is dropped by normalization
    public void NormalizeVersion_Normalizes(string input, string expected) =>
        Assert.Equal(expected, VersionOps.NormalizeVersion(input));

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("v1.0.0")]
    [InlineData("")]
    public void NormalizeVersion_RejectsInvalid(string input) =>
        Assert.Throws<WinBrewException>(() => VersionOps.NormalizeVersion(input));

    [Fact]
    public void TryNormalizeVersion_NonThrowingVariant()
    {
        Assert.True(VersionOps.TryNormalizeVersion("1.0", out var normalized));
        Assert.Equal("1.0.0", normalized);
        Assert.False(VersionOps.TryNormalizeVersion("abc", out _));
    }

    [Theory]
    [InlineData("[1.0,2.0)")]
    [InlineData("[1.2.3]")]
    [InlineData("(,3.0)")]
    [InlineData("[1.0.0, )")]
    public void ValidateRange_AcceptsIntervalNotation(string range)
    {
        var parsed = VersionOps.ValidateRange(range, "test");
        Assert.NotNull(parsed);
    }

    [Fact]
    public void ValidateRange_RejectsBareVersion_ExplainingWhy() // D3: bare = >= 1.2.3 surprise
    {
        var ex = Assert.Throws<WinBrewException>(() => VersionOps.ValidateRange("1.2.3", "deps[0]"));
        Assert.Equal(WinBrewErrorCode.UsageError, ex.Code);
        Assert.Contains("interval notation", ex.Message);
    }

    [Theory]
    [InlineData("1.*")]
    [InlineData("[1.*,2.0)")]
    public void ValidateRange_RejectsFloatingVersions(string range) =>
        Assert.Throws<WinBrewException>(() => VersionOps.ValidateRange(range, "test"));

    [Fact]
    public void ValidateRange_RejectsGarbage() =>
        Assert.Throws<WinBrewException>(() => VersionOps.ValidateRange("[banana]", "test"));

    [Fact]
    public void RangeRequestsPrerelease_TrueOnlyWhenBoundsArePrerelease()
    {
        Assert.True(VersionOps.RangeRequestsPrerelease(
            VersionRange.Parse("[1.0.0-beta.1,2.0.0)")));
        Assert.False(VersionOps.RangeRequestsPrerelease(
            VersionRange.Parse("[1.0.0,2.0.0)")));
    }
}
