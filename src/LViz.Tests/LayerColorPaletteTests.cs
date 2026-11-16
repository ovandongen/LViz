using LViz.App.ViewModels;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The stateless palette lookup: a non-blank override in the settings beats
/// the built-in palette for its own profile and layer, everything else falls
/// through, and the default palette wraps after its ten colors.
/// </summary>
public class LayerColorPaletteTests
{
    private const string ProfileId = "TestBoard";

    private static UserSettings SettingsWith(string profileId, int layer, string hex) =>
        new() { LayerColors = new() { [profileId] = new() { [layer] = hex } } };

    [Fact]
    public void Override_WinsOverTheDefault() =>
        Assert.Equal("#FF00AA", LayerColorPalette.GetColor(SettingsWith(ProfileId, 2, "#FF00AA"), ProfileId, 2));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOverride_FallsBackToTheDefault(string blank) =>
        Assert.Equal(
            LayerColorPalette.GetDefaultColor(2),
            LayerColorPalette.GetColor(SettingsWith(ProfileId, 2, blank), ProfileId, 2));

    [Fact]
    public void OtherProfile_OverrideIsIgnored() =>
        Assert.Equal(
            LayerColorPalette.GetDefaultColor(2),
            LayerColorPalette.GetColor(SettingsWith("OtherBoard", 2, "#FF00AA"), ProfileId, 2));

    [Fact]
    public void OtherLayer_OverrideIsIgnored() =>
        Assert.Equal(
            LayerColorPalette.GetDefaultColor(1),
            LayerColorPalette.GetColor(SettingsWith(ProfileId, 2, "#FF00AA"), ProfileId, 1));

    [Fact]
    public void MissingProfile_FallsBackToTheDefault() =>
        Assert.Equal(
            LayerColorPalette.GetDefaultColor(0),
            LayerColorPalette.GetColor(new UserSettings(), ProfileId, 0));

    [Fact]
    public void Default_WrapsAroundAfterTenColors()
    {
        Assert.Equal(LayerColorPalette.GetDefaultColor(0), LayerColorPalette.GetDefaultColor(10));
        Assert.Equal(LayerColorPalette.GetDefaultColor(9), LayerColorPalette.GetDefaultColor(-1));
    }
}
