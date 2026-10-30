using LViz.Core.Layout;
using LViz.Core.Layout.Dtsi;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary>
/// Turning a <see cref="UserLayoutFile"/> into a renderable profile: identity
/// fields, normalisation of (possibly negative, pivot-rotated) coordinates onto
/// the canvas, hands taken from the file, and HID name/device matching.
/// </summary>
public sealed class UserLayoutProfileTests
{
    private static UserLayoutKey Key(
        int x, int y, int w = 100, int h = 100,
        int r = 0, int rx = 0, int ry = 0, Hand hand = Hand.Left)
        => new(x, y, w, h, r, rx, ry, hand);

    private static UserLayoutFile Layout(
        string id = "board",
        string name = "Board",
        string? hidName = null,
        IReadOnlyList<UserLayoutKey>? keys = null)
        => new(UserLayoutFile.CurrentSchemaVersion, id, name, hidName, keys ?? [Key(0, 0)]);

    // --- identity ---

    [Fact]
    public void Ctor_ExposesIdentityFieldsAndSource()
    {
        var file = Layout(keys: [Key(0, 0), Key(150, 0, hand: Hand.Right), Key(300, 0, hand: Hand.Right)]);

        var profile = new UserLayoutProfile(file);

        Assert.Equal("user:board", profile.Id);
        Assert.Equal("Board (3)", profile.DisplayName);
        Assert.Equal(3, profile.KeyCount);
        Assert.Same(file, profile.Source);
        Assert.Equal("Key 0|Key 1|Key 2",
            string.Join("|", profile.Keys.Select(k => k.Description)));
    }

    // --- normalisation onto the canvas ---

    [Fact]
    public void NegativeAndRotatedLayout_NormalisesBoundsOntoCanvas()
    {
        var file = Layout(keys:
        [
            Key(-1400, -200),                                      // sits left of and above the origin
            Key(-200, 300, 100, 140, r: -3700, rx: -150, ry: 1200), // rotated about an explicit pivot
            Key(200, 250, hand: Hand.Right),
        ]);

        var profile = new UserLayoutProfile(file);

        ProfileGeometryAssertions.AssertFiniteNonZeroSizes(profile);
        ProfileGeometryAssertions.AssertKeysFitCanvas(profile);

        var (minX, minY, _, _) = profile.Keys.RotatedBounds();
        Assert.Equal(30.0, minX, 2);
        Assert.Equal(60.0, minY, 2);
    }

    [Fact]
    public void Normalisation_TranslatesPivotWithItsKey()
    {
        var thumb = Key(-200, 300, 100, 140, r: -3700, rx: -150, ry: 1200);
        var file = Layout(keys: [Key(-1400, -200), thumb, Key(200, 250, hand: Hand.Right)]);

        var profile = new UserLayoutProfile(file);

        // The same builder run before the profile's normalisation shift.
        var before = DtsiBackedLayoutBuilder.BuildFromKeys(
            [new DtsiKeyPhysical(thumb.W, thumb.H, thumb.X, thumb.Y, thumb.R, thumb.Rx, thumb.Ry)],
            (_, _) => Hand.Left,
            (_, _, _) => "Key 0");

        var after = profile.Keys.Single(k => k.RotationDegrees != 0);
        Assert.NotNull(after.RotationOriginX);
        Assert.NotNull(after.RotationOriginY);
        Assert.Equal(before[0].RotationOriginX!.Value - before[0].X,
            after.RotationOriginX!.Value - after.X, 6);
        Assert.Equal(before[0].RotationOriginY!.Value - before[0].Y,
            after.RotationOriginY!.Value - after.Y, 6);
    }

    [Fact]
    public void LayoutWithMinimumAtOrigin_LandsOnInsets()
    {
        var profile = new UserLayoutProfile(Layout(keys: [Key(0, 0), Key(150, 0, hand: Hand.Right)]));

        var first = profile.Keys[0];
        Assert.Equal(30.0, first.X, 6);
        Assert.Equal(60.0, first.Y, 6);
        Assert.Equal(60.0, first.Width, 6);   // w/h 100 centi-u × 0.6 px
        Assert.Equal(60.0, first.Height, 6);
    }

    // --- hands ---

    [Fact]
    public void Hands_ComeFromFileNotGeometry()
    {
        // A right-hand key placed left of a left-hand key: a midline cutoff
        // would flip them; the file's explicit hand must win.
        var profile = new UserLayoutProfile(Layout(keys:
            [Key(-1000, 0, hand: Hand.Right), Key(500, 0, hand: Hand.Left)]));

        Assert.Equal(Hand.Right, profile.Keys[0].Hand);
        Assert.Equal(Hand.Left, profile.Keys[1].Hand);
    }

    // --- HID matching ---

    [Fact]
    public void NameMatches_UsesHidNameWhenSet()
    {
        var profile = new UserLayoutProfile(Layout(name: "My Board", hidName: "Cosmos 82"));

        Assert.True(profile.NameMatches("USB Cosmos 82 Left"));
        Assert.True(profile.NameMatches("COSMOS 82"));
        Assert.False(profile.NameMatches("My Board"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NameMatches_FallsBackToNameWhenHidNameMissing(string? hidName)
    {
        var profile = new UserLayoutProfile(Layout(name: "Slabwing", hidName: hidName));

        Assert.True(profile.NameMatches("usb slabwing rev2"));
        Assert.False(profile.NameMatches("Glove80"));
    }

    [Fact]
    public void NameMatches_NullProductName_IsFalse()
    {
        Assert.False(new UserLayoutProfile(Layout(name: "Slabwing")).NameMatches(null));
        Assert.False(new UserLayoutProfile(Layout(name: "Slabwing", hidName: "Slab 80")).NameMatches(null));
    }

    [Fact]
    public void MatchesHidDevice_RequiresZmkIdsAndName()
    {
        var profile = new UserLayoutProfile(Layout(name: "Slabwing", hidName: "Slabwing"));

        Assert.True(profile.MatchesHidDevice(ZmkHidIds.VendorId, ZmkHidIds.ProductId, "Slabwing BLE"));
        Assert.False(profile.MatchesHidDevice(ZmkHidIds.VendorId + 1, ZmkHidIds.ProductId, "Slabwing BLE"));
        Assert.False(profile.MatchesHidDevice(ZmkHidIds.VendorId, ZmkHidIds.ProductId, "Glove80"));
    }
}
