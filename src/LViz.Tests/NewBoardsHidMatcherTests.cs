using LViz.Core.Layout;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// HID matcher coverage for the §4 multi-profile expansion. Mirrors the
/// Corne pattern: each board matches its own substring (case-insensitive,
/// Linux-style manufacturer-prefixed names work), rejects mismatched names,
/// and rejects mismatched VID/PID.
/// </summary>
public class NewBoardsHidMatcherTests
{
    private const int ZmkVid = 0x16C0;
    private const int ZmkPid = 0x27DB;

    public static IEnumerable<object[]> ProfileExpectations =>
        new[]
        {
            new object[] { (IKeyboardProfile)new ADuxProfile(), "a_dux", "A_Dux Left" },
            new object[] { new KyriaProfile(), "Kyria", "Kyria Right" },
            new object[] { new Lily58Profile(), "Lily58", "Lily58 Left" },
            new object[] { new SofleProfile(), "Sofle", "Sofle Right" },
            new object[] { new Go60Profile(), "Go60", "Moergo Go60" },
            new object[] { new Glove80Profile(), "Glove80", "Moergo Glove80 Left" },
        };

    [Theory]
    [MemberData(nameof(ProfileExpectations))]
    public void Matches_GoodProductNames(IKeyboardProfile p, string baseName, string prefixedName)
    {
        Assert.True(p.MatchesHidDevice(ZmkVid, ZmkPid, baseName));
        Assert.True(p.MatchesHidDevice(ZmkVid, ZmkPid, prefixedName));
        Assert.True(p.MatchesHidDevice(ZmkVid, ZmkPid, baseName.ToLowerInvariant()));
    }

    [Theory]
    [MemberData(nameof(ProfileExpectations))]
    public void DoesNotMatch_UnrelatedDevice(IKeyboardProfile p, string baseName, string _)
    {
        Assert.False(p.MatchesHidDevice(ZmkVid, ZmkPid, "Corne Left"));
        // Cross-board negative — make sure no two new profiles' substrings
        // are accidentally each other's substring.
        foreach (var other in (IEnumerable<object[]>)ProfileExpectations)
        {
            var otherName = (string)other[1];
            if (!string.Equals(otherName, baseName, StringComparison.OrdinalIgnoreCase))
                Assert.False(p.MatchesHidDevice(ZmkVid, ZmkPid, otherName));
        }
    }

    [Theory]
    [MemberData(nameof(ProfileExpectations))]
    public void DoesNotMatch_WhenVendorOrProductIdDiffers(IKeyboardProfile p, string baseName, string _)
    {
        Assert.False(p.MatchesHidDevice(0x046D, ZmkPid, baseName));
        Assert.False(p.MatchesHidDevice(ZmkVid, 0x1234, baseName));
    }

    [Fact]
    public void DoesNotMatch_WhenProductNameMissing()
    {
        foreach (var row in ProfileExpectations)
        {
            var p = (IKeyboardProfile)row[0];
            Assert.False(p.MatchesHidDevice(ZmkVid, ZmkPid, null), $"{p.Id} should reject null name");
            Assert.False(p.MatchesHidDevice(ZmkVid, ZmkPid, ""), $"{p.Id} should reject empty name");
        }
    }
}
