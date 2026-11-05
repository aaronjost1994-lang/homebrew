using WinBrew.Core;
using WinBrew.Core.Config;

namespace WinBrew.UnitTests;

public class ConfigDefinitionsTests
{
    // ---- registry completeness (D10) ------------------------------------

    [Fact]
    public void Registry_ContainsExactlyTheD10Keys()
    {
        var expected = new[]
        {
            "color", "interactive", "cache.maxBytes", "download.timeoutSeconds",
            "download.maxBytes", "download.retries", "retainVersions",
            "lock.timeoutSeconds", "trust.maxClockSkewSeconds", "trust.maxMetadataBytes",
            "archive.maxEntries", "archive.maxTotalBytes", "archive.maxCompressionRatio",
            "path.managed", "testing.allowInsecureLocalhost",
        };

        Assert.Equal(
            new HashSet<string>(expected, StringComparer.Ordinal),
            ConfigDefinitions.Keys.Keys.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Defaults_MatchTheD10Table()
    {
        Assert.Equal("auto", ConfigDefinitions.Keys["color"].Default);
        Assert.Equal("auto", ConfigDefinitions.Keys["interactive"].Default);
        Assert.Equal(5368709120L, ConfigDefinitions.Keys["cache.maxBytes"].Default);
        Assert.Equal(300, ConfigDefinitions.Keys["download.timeoutSeconds"].Default);
        Assert.Equal(4294967296L, ConfigDefinitions.Keys["download.maxBytes"].Default);
        Assert.Equal(3, ConfigDefinitions.Keys["download.retries"].Default);
        Assert.Equal(1, ConfigDefinitions.Keys["retainVersions"].Default);
        Assert.Equal(30, ConfigDefinitions.Keys["lock.timeoutSeconds"].Default);
        Assert.Equal(300, ConfigDefinitions.Keys["trust.maxClockSkewSeconds"].Default);
        Assert.Equal(67108864L, ConfigDefinitions.Keys["trust.maxMetadataBytes"].Default);
        Assert.Equal(100000, ConfigDefinitions.Keys["archive.maxEntries"].Default);
        Assert.Equal(8589934592L, ConfigDefinitions.Keys["archive.maxTotalBytes"].Default);
        Assert.Equal(200, ConfigDefinitions.Keys["archive.maxCompressionRatio"].Default);
        Assert.Equal(false, ConfigDefinitions.Keys["path.managed"].Default);
        Assert.Equal(false, ConfigDefinitions.Keys["testing.allowInsecureLocalhost"].Default);
    }

    // ---- ValidateAndParse --------------------------------------------------

    [Theory]
    [InlineData("color", "never", "never")]
    [InlineData("interactive", "never", "never")]
    public void EnumKeys_AcceptAllowedValues(string key, string raw, string expected) =>
        Assert.Equal(expected, ConfigDefinitions.ValidateAndParse(key, raw, testHooksBuild: false));

    [Theory]
    [InlineData("color", "sometimes")]
    [InlineData("interactive", "always")] // allowed values are auto|never only (D10)
    public void EnumKeys_RejectDisallowedValues(string key, string raw) =>
        Assert.Throws<WinBrewException>(() => ConfigDefinitions.ValidateAndParse(key, raw, testHooksBuild: false));

    [Fact]
    public void IntKey_WithinRange_Parses() =>
        Assert.Equal(2, ConfigDefinitions.ValidateAndParse("download.retries", "2", testHooksBuild: false));

    [Fact]
    public void IntKey_OutOfRange_IsUsageError() // D10: config set validates range, exit 2
    {
        var ex = Assert.Throws<WinBrewException>(
            () => ConfigDefinitions.ValidateAndParse("download.retries", "9", testHooksBuild: false));
        Assert.Equal(WinBrewErrorCode.UsageError, ex.Code);
        Assert.Contains("0..5", ex.Message);
    }

    [Fact]
    public void LongKey_ParsesLargeBudget() =>
        Assert.Equal(10737418240L, ConfigDefinitions.ValidateAndParse("cache.maxBytes", "10737418240", testHooksBuild: false));

    [Theory]
    [InlineData("cache.maxBytes", "abc")]
    [InlineData("lock.timeoutSeconds", "1.5")]
    [InlineData("path.managed", "yes-please")]
    public void WrongTypes_AreUsageErrors(string key, string raw) =>
        Assert.Throws<WinBrewException>(() => ConfigDefinitions.ValidateAndParse(key, raw, testHooksBuild: false));

    [Fact]
    public void UnknownKey_IsUsageError() // D10: unknown keys exit 2
    {
        var ex = Assert.Throws<WinBrewException>(
            () => ConfigDefinitions.ValidateAndParse("no.such.key", "1", testHooksBuild: false));
        Assert.Equal(WinBrewErrorCode.UsageError, ex.Code);
        Assert.Contains("Unknown configuration key", ex.Message);
    }

    [Fact]
    public void TestingKey_RejectedInReleaseBuild() // D10: testing.* only in test-hooks builds
    {
        var ex = Assert.Throws<WinBrewException>(
            () => ConfigDefinitions.ValidateAndParse("testing.allowInsecureLocalhost", "true", testHooksBuild: false));
        Assert.Equal(WinBrewErrorCode.UsageError, ex.Code);
        Assert.Contains("test-hooks", ex.Message);
    }

    [Fact]
    public void TestingKey_AcceptedInTestHooksBuild() =>
        Assert.Equal(true, ConfigDefinitions.ValidateAndParse("testing.allowInsecureLocalhost", "true", testHooksBuild: true));

    [Theory]
    [InlineData("path.managed", "TRUE", true)]
    [InlineData("path.managed", "False", false)]
    public void BoolKeys_ParseCaseInsensitively(string key, string raw, bool expected) =>
        Assert.Equal(expected, ConfigDefinitions.ValidateAndParse(key, raw, testHooksBuild: false));
}
