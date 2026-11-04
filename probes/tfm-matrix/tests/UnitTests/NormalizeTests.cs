using Core;
using Xunit;

public class NormalizeTests
{
    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("2.5", "2.5.0")]
    [InlineData("1.2.3-beta.1", "1.2.3-beta.1")]
    public void NuGetVersion_normalizes_per_D3(string input, string expected)
        => Assert.Equal(expected, VersionOps.Normalize(input));
}
