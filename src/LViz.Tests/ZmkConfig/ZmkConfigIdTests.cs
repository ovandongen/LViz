using LViz.Core.ZmkConfig;
using Xunit;

namespace LViz.Tests.ZmkConfig;

/// <summary>
/// <see cref="ZmkConfigId.Matches"/>: zmk-hid-viz's 0xFA reply holds at most
/// 31 characters, so a GUID config ID comes back cut and must still match.
/// </summary>
public class ZmkConfigIdTests
{
    private const string Guid = "5a75ba84-85bd-4694-8bb2-d4242e5d3e87";

    [Fact]
    public void ReplyMaxLength_IsOneReportMinusTheOpcode() => Assert.Equal(31, ZmkConfigId.ReplyMaxLength);

    [Theory]
    [InlineData("5a75ba84-85bd-4694-8bb2-d4242e5", true)] // what the firmware sends for a GUID
    [InlineData(Guid, true)] // a host that ever gets the whole ID
    [InlineData("5a75ba84-85bd-4694-8bb2-d4242e", false)] // one short
    [InlineData("5a75ba84-85bd-4694-8bb2-d4242e6", false)]
    [InlineData("5A75BA84-85BD-4694-8BB2-D4242E5", false)] // ordinal
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Guid_MatchesItsCutReply(string? reported, bool matches) =>
        Assert.Equal(matches, ZmkConfigId.Matches(reported, Guid));

    [Theory]
    [InlineData("corne-main", "corne-main", true)]
    [InlineData("corne-mai", "corne-main", false)] // a short ID is never cut, so a prefix is no match
    [InlineData("corne-main", null, false)]
    [InlineData("corne-main", "", false)]
    public void ShortId_MatchesOnlyWhole(string? reported, string? expected, bool matches) =>
        Assert.Equal(matches, ZmkConfigId.Matches(reported, expected));
}
