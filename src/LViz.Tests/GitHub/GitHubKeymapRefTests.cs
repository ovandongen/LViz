using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// <see cref="GitHubKeymapRef"/>: the <c>github:owner/repo:branch:path</c>
/// recent-entry encoding — round trips for normal, slash-branch and
/// colon-path references, and rejection of every malformed or local-path form.
/// </summary>
public class GitHubKeymapRefTests
{
    private static GitHubKeymapRef Sample() =>
        new("ovandongen", "zmk-config-cosmos82", "main", "config/cosmos82.keymap");

    [Fact]
    public void ToString_UsesGithubPrefixAndColonSeparators()
    {
        Assert.Equal(
            "github:ovandongen/zmk-config-cosmos82:main:config/cosmos82.keymap",
            Sample().ToString());
    }

    [Fact]
    public void RoundTrip_TryParseOfToString_GivesEqualRecord()
    {
        var entry = Sample().ToString();

        Assert.True(GitHubKeymapRef.TryParse(entry, out var parsed));

        Assert.Equal(Sample(), parsed);
    }

    [Fact]
    public void RoundTrip_BranchWithSlash_Survives()
    {
        var keymap = new GitHubKeymapRef("a", "b", "feature/new-layer", "config/x.keymap");

        Assert.True(GitHubKeymapRef.TryParse(keymap.ToString(), out var parsed));

        Assert.Equal(keymap, parsed);
    }

    [Fact]
    public void RoundTrip_PathWithColon_Survives()
    {
        var keymap = new GitHubKeymapRef("a", "b", "main", "config/a:b.keymap");

        Assert.True(GitHubKeymapRef.TryParse(keymap.ToString(), out var parsed));

        Assert.Equal(keymap, parsed);
        Assert.Equal("config/a:b.keymap", parsed!.Path);
    }

    [Fact]
    public void FileName_IsLastPathSegment()
    {
        Assert.Equal("cosmos82.keymap", Sample().FileName);
    }

    [Fact]
    public void RepoFullName_IsOwnerSlashRepo()
    {
        Assert.Equal("ovandongen/zmk-config-cosmos82", Sample().RepoFullName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/Users/x/config/a.keymap")]
    [InlineData("C:\\keymaps\\a.keymap")]
    [InlineData("github:")]
    [InlineData("github:owner:main:a.keymap")]
    [InlineData("github:a/b/c:main:x.keymap")]
    [InlineData("github:a/b::x.keymap")]
    [InlineData("github:a/b:main:")]
    [InlineData("github:a/b:main:/abs.keymap")]
    [InlineData("github:a/b:main:config/../x.keymap")]
    [InlineData("github:a/b:main:config//x.keymap")]
    [InlineData("github:a/b:main:config\\x.keymap")]
    public void TryParse_MalformedEntries_ReturnFalse(string? entry)
    {
        Assert.False(GitHubKeymapRef.TryParse(entry, out var keymap));
        Assert.Null(keymap);
    }

    [Fact]
    public void IsGitHubEntry_PrefixOnlyCheck()
    {
        Assert.True(GitHubKeymapRef.IsGitHubEntry("github:x"));
        Assert.False(GitHubKeymapRef.IsGitHubEntry(null));
        Assert.False(GitHubKeymapRef.IsGitHubEntry("/Users/x/config/a.keymap"));
    }
}
