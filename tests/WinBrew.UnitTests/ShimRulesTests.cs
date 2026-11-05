using WinBrew.Core.Shims;
using WinBrew.Shim;

namespace WinBrew.UnitTests;

/// <summary>
/// D4 / A16 argument-quoting corpus for the shim's argv[0] strip (MSVC
/// C-runtime rules: '"' toggles, backslashes are literal, no separator
/// insertion), the composed command line, the 32,767-character limit, and
/// sidecar containment. ShimRules.cs is linked from src/WinBrew.Shim.
/// </summary>
public class ShimRulesTests
{
    /// <summary>Argument tails that must be forwarded VERBATIM after either argv[0] style.</summary>
    public static readonly string[] Tails =
    [
        "",                                   // no arguments
        " a",
        "  a",                                // two spaces kept
        " \"q r\" s",                         // quoted argument with a space
        " \"\"",                              // one empty argument
        " \"\" \"\"",                         // two empty arguments
        " a\\",                               // trailing backslash
        " \"a\\\\\"",                         // backslashes before a closing quote
        " a\\\"b",                            // backslash before an embedded quote
        " \\\\\\\"",                          // backslash run then quote
        " \"C:\\path with space\\\\\"",       // quoted path ending in backslashes
        " C:\\path\\with\\trailing\\",
        " ünïcödé",                           // Unicode
        " 日本語 テスト",
        " 🙂 emoji",
        " %PATH%",                            // cmd metacharacters: never interpreted (no shell)
        " ^&",
        " a & b",
        " a | b",
        " a > out.txt",
        " \"%\" \"^\"",
        " !x!",
        " (x) ; , =",
        "\t-v",                               // tab separator
        "\t\t-v",
        " \t-x",
        " -",
        " --flag=\"x y\"",
        " 'single quotes are literal'",
        " a\"b c\"d",                         // quotes inside an argument
        " \"unterminated",
        " \\",
        " \"\\\"\"",
        " \"a\"\"b\"",                        // doubled quotes
        " " + new string('a', 1000),          // long single argument
        " " + string.Join(' ', Enumerable.Repeat("arg", 300)), // many arguments
    ];

    public static TheoryData<string, string> UnquotedCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var tail in Tails)
        {
            data.Add(@"C:\Users\me\AppData\Local\WinBrew\bin\x.exe" + tail, tail);
            data.Add("x" + tail, tail);
        }

        return data;
    }

    public static TheoryData<string, string> QuotedCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var tail in Tails)
        {
            data.Add("\"C:\\Program Files\\a b\\x.exe\"" + tail, tail);
        }

        return data;
    }

    [Fact]
    public void Corpus_HasAtLeast35Cases()
    {
        Assert.True(Tails.Length >= 35, $"A16 requires >= 35 quoting cases; have {Tails.Length}");
    }

    [Theory]
    [MemberData(nameof(UnquotedCases))]
    public void UnquotedArgv0_ForwardsTheTailVerbatim(string commandLine, string expectedTail) =>
        Assert.Equal(expectedTail, ShimRules.Tail(commandLine));

    [Theory]
    [MemberData(nameof(QuotedCases))]
    public void QuotedArgv0_ForwardsTheTailVerbatim(string commandLine, string expectedTail) =>
        Assert.Equal(expectedTail, ShimRules.Tail(commandLine));

    [Theory]
    [InlineData("C:\\Pro\"gram Fi\"les\\x.exe a", " a")]     // embedded quotes toggle (A16 named case)
    [InlineData("\"x.exe\"arg", "")]                          // no separator insertion: argv[0] = x.exearg
    [InlineData("\"x.exe\"arg b", " b")]
    [InlineData("\"C:\\a b\\x.exe\" \"q r\" s", " \"q r\" s")] // unchanged
    [InlineData("x.exe\t-v", "\t-v")]                         // tab separator
    [InlineData("\"x.exe", "")]                               // unterminated quote
    [InlineData("\"x.exe  a b", "")]                          // unterminated: the rest is argv[0]
    [InlineData("", "")]                                      // empty command line
    [InlineData("x.exe", "")]
    [InlineData("C:\\dir\\\"x y\".exe z", " z")]              // backslash before a quote is literal
    [InlineData("C:\\a\\\\\" b\"\\x.exe c", " c")]            // backslash run before a quote is literal
    [InlineData("C:\\a\\x.exe\\ next", " next")]              // trailing backslash in argv[0]
    public void NamedCases(string commandLine, string expectedTail) =>
        Assert.Equal(expectedTail, ShimRules.Tail(commandLine));

    [Fact]
    public void NullCommandLine_IsAnEmptyTail() => Assert.Equal(string.Empty, ShimRules.Tail(null));

    [Theory]
    [MemberData(nameof(UnquotedCases))]
    [MemberData(nameof(QuotedCases))]
    public void EveryTail_IsEmptyOrWhitespaceLed(string commandLine, string _) =>
        Assert.True(ShimRules.IsWellFormedTail(ShimRules.Tail(commandLine)));

    [Fact]
    public void BackslashesInArgv0_HaveNoSpecialMeaning()
    {
        // CommandLineToArgvW would treat \" as an escaped quote and swallow the space;
        // the C runtime's argv[0] rule does not.
        Assert.Equal(" next", ShimRules.Tail("C:\\x\\\" y\"\\z.exe next"));
        Assert.Equal(" -h", ShimRules.Tail("\\\\server\\share\\tool.exe -h"));
    }

    [Fact]
    public void Compose_QuotesOnlyTheTarget_AndNeverReQuotesTheTail()
    {
        Assert.Equal("\"C:\\p q\\t.exe\" \"a b\"\\ c", ShimRules.Compose("C:\\p q\\t.exe", " \"a b\"\\ c"));
        Assert.Equal("\"t.exe\"", ShimRules.Compose("t.exe", string.Empty));
    }

    [Fact]
    public void LengthLimit_Is32767IncludingTheTerminatingNul()
    {
        var target = "C:\\t.exe"; // composed = "\"C:\\t.exe\"" (10 chars) + tail
        var fits = ShimRules.Compose(target, " " + new string('a', 32766 - 10 - 1));
        Assert.Equal(32766, fits.Length);
        Assert.False(ShimRules.ExceedsLimit(fits));

        var overflow = ShimRules.Compose(target, " " + new string('a', 32767 - 10 - 1));
        Assert.Equal(32767, overflow.Length);
        Assert.True(ShimRules.ExceedsLimit(overflow));
        Assert.Equal(206, ShimRules.ExitCommandLineTooLong);
    }

    // ---- containment (D4) ------------------------------------------------------------------

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "wbrew-shimroot");
    private static readonly string ShimExe = Path.Combine(Root, "bin", "hello.exe");
    private static readonly string SelfShimExe = Path.Combine(Root, "bin", "wbrew.exe");

    [Fact]
    public void Target_InsidePackages_IsAccepted()
    {
        var allowed = ShimRules.AllowedRoot(ShimExe, "hello");
        Assert.Equal(Path.Combine(Root, "packages"), allowed);
        Assert.True(ShimRules.IsContained(allowed, Path.Combine(Root, "packages", "hello", "1.0.0", "x64", "hello.exe"), out _));
    }

    [Fact]
    public void SelfShim_MayTargetApp_AndOnlyApp()
    {
        var allowed = ShimRules.AllowedRoot(SelfShimExe, "wbrew");
        Assert.Equal(Path.Combine(Root, "app"), allowed);
        Assert.True(ShimRules.IsContained(allowed, Path.Combine(Root, "app", "1.0.0", "wbrew.exe"), out _));
        Assert.False(ShimRules.IsContained(allowed, Path.Combine(Root, "packages", "x", "wbrew.exe"), out _));
    }

    [Fact]
    public void OtherShims_TargetingApp_AreRefused()
    {
        var allowed = ShimRules.AllowedRoot(ShimExe, "hello");
        Assert.False(ShimRules.IsContained(allowed, Path.Combine(Root, "app", "1.0.0", "wbrew.exe"), out _));
    }

    [Theory]
    [InlineData("packages/../app/x.exe")]          // '..' escape, normalized away
    [InlineData("packages/a/../../bin/x.exe")]
    [InlineData("packagesX/x.exe")]                // prefix trick without the trailing separator
    [InlineData("packages")]                       // the root itself is not "inside"
    public void Escapes_AreRefused(string relative)
    {
        var allowed = ShimRules.AllowedRoot(ShimExe, "hello");
        var target = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.False(ShimRules.IsContained(allowed, target, out _));
    }

    [Theory]
    [InlineData("hello.exe")]
    [InlineData("packages/hello.exe")]
    [InlineData("")]
    public void RelativeOrEmptyTargets_AreRefused(string target) =>
        Assert.False(ShimRules.IsContained(ShimRules.AllowedRoot(ShimExe, "hello"), target, out _));

    [Fact]
    public void ContainmentRoot_IgnoresWinBrewRoot()
    {
        var previous = Environment.GetEnvironmentVariable("WINBREW_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("WINBREW_ROOT", Path.Combine(Path.GetTempPath(), "attacker"));
            Assert.Equal(Path.Combine(Root, "packages"), ShimRules.AllowedRoot(ShimExe, "hello"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINBREW_ROOT", previous);
        }
    }

    [Fact]
    public void PathPrefixes_CoverEveryComponent()
    {
        var target = Path.Combine(Root, "packages", "a", "b.exe");
        var prefixes = ShimRules.PathPrefixes(target).ToList();
        Assert.Equal(target, prefixes[^1]);
        Assert.Contains(Path.Combine(Root, "packages"), prefixes);
        Assert.Contains(Root, prefixes);
    }

    // ---- sidecar contract: Core writer ↔ shim reader --------------------------------------------

    [Theory]
    [InlineData("console")]
    [InlineData("gui")]
    public void Sidecar_RoundTrips_FromTheWriterToTheShimReader(string subsystem)
    {
        var written = new ShimSidecar(@"C:\WinBrew\packages\hello\1.0.0\x64\bin\hello.exe", "local/hello", "1.0.0", subsystem);
        var read = SidecarReader.Parse(written.ToUtf8Bytes());
        Assert.NotNull(read);
        Assert.Equal(written.Target, read.Target);
        Assert.Equal(written.Package, read.Package);
        Assert.Equal(written.Version, read.Version);
        Assert.Equal(subsystem, read.Subsystem);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"target\":\"x\",\"package\":\"p\",\"version\":\"1\",\"subsystem\":\"service\"}")]
    [InlineData("{\"package\":\"p\",\"version\":\"1\",\"subsystem\":\"console\"}")]
    [InlineData("{\"target\":5,\"package\":\"p\",\"version\":\"1\",\"subsystem\":\"console\"}")]
    public void InvalidSidecars_AreRejected(string json) =>
        Assert.Null(SidecarReader.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
}
