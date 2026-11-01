using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// <see cref="GitHubTokens"/>: the keychain JSON round trip (full and
/// minimal), rejection of malformed stores, and the token-free
/// <see cref="GitHubTokens.ToString"/>.
/// </summary>
public class GitHubTokensTests
{
    private static readonly DateTimeOffset AccessExpiry =
        new(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(1));

    private static readonly DateTimeOffset RefreshExpiry =
        new(2027, 2, 1, 6, 30, 0, TimeSpan.Zero);

    [Fact]
    public void RoundTrip_AllFieldsSet_Survives()
    {
        var tokens = new GitHubTokens(
            "ghu_access", AccessExpiry, "ghu_refresh", RefreshExpiry, "ovandongen");

        var parsed = GitHubTokens.TryFromJson(tokens.ToJson());

        Assert.Equal(tokens, parsed);
    }

    [Fact]
    public void RoundTrip_ExpiriesAndRefreshNull_Survives()
    {
        var tokens = new GitHubTokens("ghu_access", null, null, null, "ovandongen");

        var parsed = GitHubTokens.TryFromJson(tokens.ToJson());

        Assert.Equal(tokens, parsed);
        Assert.Null(parsed!.AccessTokenExpiresAt);
        Assert.Null(parsed.RefreshToken);
        Assert.Null(parsed.RefreshTokenExpiresAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"accessToken":"ghu_access"}""")]
    [InlineData("""{"accessToken":"","login":"ovandongen"}""")]
    public void TryFromJson_MissingOrInvalidPayloads_ReturnNull(string? json)
    {
        Assert.Null(GitHubTokens.TryFromJson(json));
    }

    [Fact]
    public void ToJson_UsesCamelCasePropertyNames()
    {
        var json = new GitHubTokens("a", null, null, null, "l").ToJson();

        Assert.Contains("\"accessToken\"", json);
        Assert.Contains("\"accessTokenExpiresAt\"", json);
        Assert.Contains("\"refreshToken\"", json);
        Assert.Contains("\"refreshTokenExpiresAt\"", json);
        Assert.Contains("\"login\"", json);
    }

    [Fact]
    public void ToString_ShowsLoginButNeverTokens()
    {
        var tokens = new GitHubTokens(
            "ghu_secret_access", AccessExpiry, "ghu_secret_refresh", RefreshExpiry, "ovandongen");

        var text = tokens.ToString();

        Assert.Contains("ovandongen", text);
        Assert.DoesNotContain("ghu_secret_access", text);
        Assert.DoesNotContain("ghu_secret_refresh", text);
    }
}
