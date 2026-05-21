using LViz.Core.Layout;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Verifies the per-profile HID device matcher used to scope Raw HID
/// discovery to whichever keyboard the user has selected. All ZMK boards
/// register under the same VID/PID (0x16C0/0x27DB — the shared
/// pid.codes ZMK identifier), so the matcher has to combine the IDs with
/// a case-insensitive **substring** match on the product name.
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
