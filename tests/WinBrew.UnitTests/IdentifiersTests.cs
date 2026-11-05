using WinBrew.Core;

namespace WinBrew.UnitTests;

public class IdentifiersTests
{
    [Theory]
    [InlineData("curl")]
    [InlineData("a")]
    [InlineData("0abc")]
    [InlineData("my-app.v2")]
    public void ValidPackageIds_Accepted(string id) => Assert.True(Identifiers.IsValidPackageId(id));

    [Theory]
    [InlineData("")]              // empty
    [InlineData("Curl")]          // uppercase (A9: normalized lowercase)
    [InlineData("-abc")]          // leading dash
    [InlineData("abc-")]          // trailing dash
    [InlineData("a/b")]           // slash inside id
    [InlineData("a..b")]          // '..' forbidden (A9)
    [InlineData("con")]           // reserved device name (A9/A13)
    [InlineData("com1")]          // reserved
    [InlineData("nul.zip")]       // reserved base name
    public void InvalidPackageIds_Rejected(string id) => Assert.False(Identifiers.IsValidPackageId(id));

    [Theory]
    [InlineData("main")]
    [InlineData("a1")]
    [InlineData("my-repo")]
    public void ValidRepoNames_Accepted(string name) => Assert.True(Identifiers.IsValidRepoName(name));

    [Theory]
    [InlineData("")]
    [InlineData("-x")]
    [InlineData("x-")]
    [InlineData("a_b")]           // underscore not allowed in repo names
    [InlineData("a.b")]           // dot not allowed in repo names
    public void InvalidRepoNames_Rejected(string name) => Assert.False(Identifiers.IsValidRepoName(name));

    [Fact]
    public void UnqualifiedReference_ParsesWithNullRepo()
    {
        var ok = Identifiers.TrySplitReference("curl", out var repo, out var id);
        Assert.True(ok);
        Assert.Null(repo);
        Assert.Equal("curl", id);
    }

    [Fact]
    public void QualifiedReference_ParsesBothParts()
    {
        var ok = Identifiers.TrySplitReference("main/curl", out var repo, out var id);
        Assert.True(ok);
        Assert.Equal("main", repo);
        Assert.Equal("curl", id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad/repo/curl")]   // two slashes
    [InlineData("Bad-Repo/curl")]   // invalid repo part
    [InlineData("main/cu rl")]      // invalid id part
    public void InvalidReferences_Rejected(string reference)
    {
        Assert.False(Identifiers.TrySplitReference(reference, out _, out _));
    }

    [Fact]
    public void Qualified_FormatsRepoSlashId()
    {
        Assert.Equal("main/curl", Identifiers.Qualified("main", "curl"));
    }
}
