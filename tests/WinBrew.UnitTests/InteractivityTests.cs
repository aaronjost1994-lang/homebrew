using WinBrew.Application.Ports;

namespace WinBrew.UnitTests;

/// <summary>The A8 non-interactive matrix: --non-interactive, interactive=never,
/// WINBREW_NONINTERACTIVE=1, or stdin not a terminal.</summary>
public class InteractivityTests
{
    private static InteractivityOptions Opts(
        bool flag = false,
        string? config = null,
        string? env = null,
        bool stdinIsTerminal = true) =>
        new(flag, config, env, stdinIsTerminal);

    [Fact]
    public void FullyInteractive_ByDefault() =>
        Assert.False(Opts().IsNonInteractive);

    [Theory]
    [InlineData(true, null, null, true)]   // --non-interactive
    [InlineData(false, "never", null, true)]  // config interactive=never
    [InlineData(false, null, "1", true)]   // WINBREW_NONINTERACTIVE=1
    [InlineData(false, null, null, false)] // stdin not a terminal
    public void EachCondition_Alone_MakesNonInteractive(bool flag, string? config, string? env, bool terminal) =>
        Assert.True(Opts(flag, config, env, terminal).IsNonInteractive);

    [Fact]
    public void OtherEnvValues_DoNotTrigger() =>
        Assert.False(Opts(env: "0").IsNonInteractive);

    [Fact]
    public void AutoConfigValue_KeepsInteractive() =>
        Assert.False(Opts(config: "auto").IsNonInteractive);
}
