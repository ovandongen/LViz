using LViz.Core.Layout;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Verifies the per-profile HID device matcher used to scope Raw HID
/// discovery to whichever keyboard the user has selected. ZMK boards share
/// two VID/PID pairs (MoErgo's 0x16C0/0x27DB and upstream ZMK's default
/// 0x1D50/0x615E), so the matcher has to combine the IDs with a
/// case-insensitive **substring** match on the product name.
/// <para>Substring (rather than prefix) is necessary because Linux's
/// hidraw layer prepends the manufacturer name — the device shows up as
/// e.g. <c>"foostan Corne Left"</c> there. macOS and Windows expose the
/// bare product string. A prefix match would silently stop finding the
/// device on Linux.</para>
/// </summary>
public class KeyboardProfileHidMatcherTests
{
    private const int ZmkVid = 0x16C0;
    private const int ZmkPid = 0x27DB;

    [Theory]
    [InlineData("Corne Left")]
    [InlineData("Corne")]
    [InlineData("foostan Corne Left")]
    [InlineData("corne right")]
    public void Corne_Matches_GoodProductNames(string product)
    {
        var p = new CorneProfile();
        Assert.True(p.MatchesHidDevice(ZmkVid, ZmkPid, product));
    }

    [Fact]
    public void Corne_DoesNotMatch_UnrelatedDevice()
    {
        var p = new CorneProfile();
        Assert.False(p.MatchesHidDevice(ZmkVid, ZmkPid, "Glove80 Left"));
        Assert.False(p.MatchesHidDevice(ZmkVid, ZmkPid, "Go60"));
    }

    [Fact]
    public void DoesNotMatch_WhenVendorOrProductIdDiffers()
    {
        var p = new CorneProfile();
        Assert.False(p.MatchesHidDevice(0x046D, ZmkPid, "Corne"));   // wrong VID
        Assert.False(p.MatchesHidDevice(ZmkVid, 0x1234, "Corne"));   // wrong PID
    }

    [Fact]
    public void Matches_UpstreamZmkIds()
    {
        // Upstream ZMK's default USB identifier (a Cosmos82 on zmkfirmware
        // v0.3 reports 1D50:615E); only the MoErgo pair matched before.
        var p = new CorneProfile();
        Assert.True(p.MatchesHidDevice(0x1D50, 0x615E, "Corne"));
    }

    [Fact]
    public void DoesNotMatch_AMixedIdPair()
    {
        var p = new CorneProfile();
        Assert.False(p.MatchesHidDevice(0x16C0, 0x615E, "Corne"));
        Assert.False(p.MatchesHidDevice(0x1D50, 0x27DB, "Corne"));
    }

    [Fact]
    public void DoesNotMatch_WhenProductNameMissing()
    {
        // Some HID devices return null/empty for GetProductName(); without
        // a name we can't disambiguate boards, so refuse to match rather
        // than guess.
        var p = new CorneProfile();
        Assert.False(p.MatchesHidDevice(ZmkVid, ZmkPid, null));
        Assert.False(p.MatchesHidDevice(ZmkVid, ZmkPid, ""));
    }
}
