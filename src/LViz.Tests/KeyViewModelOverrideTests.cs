using LViz.App.ViewModels;
using LViz.Core.Layout;
using LViz.Core.Models;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Exercises the override and layer-colour hooks in
/// <see cref="KeyViewModel.ApplyBinding"/>. Overrides reach the key through
/// the <see cref="KeyPaintContext"/>' settings, so every test carries its
/// own — nothing is process-wide.
/// </summary>
public class KeyViewModelOverrideTests
{
    private const string ProfileId = "TestBoard";

    private static KeyViewModel NewKey(int positionIndex)
        => new(new KeyPosition(positionIndex, X: 0, Y: 0));

    private static KeyBinding KpA() => new("&kp", new[] { "A" });

    /// <summary>Settings carrying one label override for a (layer, key) slot.</summary>
    private static UserSettings SettingsWith(int layerIndex, int keyIndex, KeyLabelOverride ov) => new()
    {
        KeyLabelOverrides = new() { [ProfileId] = new() { [layerIndex] = new() { [keyIndex] = ov } } },
    };

    [Fact]
    public void NoOverride_FormatterDefaults_Render()
    {
        var key = NewKey(positionIndex: 7);

        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, paint: new KeyPaintContext(ProfileId, new UserSettings()));

        Assert.Equal("A", key.Label);
        Assert.Null(key.LabelFontSizeOverride);
        Assert.False(key.IsLabelBold);
    }

    [Fact]
    public void Override_ReplacesAllSlots()
    {
        var key = NewKey(positionIndex: 7);
        var settings = SettingsWith(layerIndex: 0, keyIndex: 7, new KeyLabelOverride
        {
            MainLabel = "nav",
            Subscript = "space",
            TopLeftBadge = "L1",
            Icon = "fa-house",
            FontSize = 24,
            Bold = true,
        });

        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, paint: new KeyPaintContext(ProfileId, settings));

        Assert.Equal("nav", key.Label);
        Assert.Equal("space", key.Subscript);
        Assert.Equal("L1", key.TopLeftLabel);
        Assert.Equal("fa-house", key.IconName);
        Assert.Equal(24, key.LabelFontSizeOverride);
        Assert.Equal(24, key.LabelFontSize);
        Assert.True(key.IsLabelBold);
    }

    [Fact]
    public void Override_ScopedByLayer_DoesNotLeakToOtherLayers()
    {
        var key = NewKey(positionIndex: 7);
        var settings = SettingsWith(layerIndex: 0, keyIndex: 7,
            new KeyLabelOverride { MainLabel = "L0only" });

        // Active layer 1 → no override for that slot → formatter wins.
        key.ApplyBinding(KpA(), activeLayerIndex: 1, targetLayer: null,
            targetLayerName: null, paint: new KeyPaintContext(ProfileId, settings));

        Assert.Equal("A", key.Label);
    }

    [Fact]
    public void Override_ScopedByKeyIndex()
    {
        var key = NewKey(positionIndex: 7);
        var settings = SettingsWith(layerIndex: 0, keyIndex: 8,
            new KeyLabelOverride { MainLabel = "wrong" });

        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, paint: new KeyPaintContext(ProfileId, settings));

        Assert.Equal("A", key.Label);
    }

    [Fact]
    public void ClearingOverride_RestoresDefaults()
    {
        var key = NewKey(positionIndex: 7);
        var withOverride = SettingsWith(0, 7,
            new KeyLabelOverride { MainLabel = "x", FontSize = 30, Bold = true });
        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, paint: new KeyPaintContext(ProfileId, withOverride));
        Assert.Equal("x", key.Label);
        Assert.True(key.IsLabelBold);

        var cleared = withOverride with
        {
            KeyLabelOverrides = KeyLabelOverrides.With(withOverride.KeyLabelOverrides, ProfileId, 0, 7, null),
        };
        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, paint: new KeyPaintContext(ProfileId, cleared));

        Assert.Equal("A", key.Label);
        Assert.Null(key.LabelFontSizeOverride);
        Assert.False(key.IsLabelBold);
    }

    [Fact]
    public void IgnoreLabelOverride_SkipsTheStoredOverride()
    {
        // Edit mode passes ignoreLabelOverride: the key shows what the file
        // will hold, exactly as when no override is stored.
        var key = NewKey(positionIndex: 7);
        var settings = SettingsWith(0, 7,
            new KeyLabelOverride { MainLabel = "x", FontSize = 30, Bold = true });

        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, paint: new KeyPaintContext(ProfileId, settings),
            ignoreLabelOverride: true);

        Assert.Equal("A", key.Label);
        Assert.Null(key.LabelFontSizeOverride);
        Assert.False(key.IsLabelBold);
    }

    [Fact]
    public void Override_WithEmptyIcon_KeepsBindingDecorationIcon()
    {
        // Binding ships with no DecorationIcon, override leaves Icon = "" —
        // IconName should remain empty (not flicker on or off).
        var key = NewKey(positionIndex: 7);
        var settings = SettingsWith(0, 7, new KeyLabelOverride { MainLabel = "x" });

        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, paint: new KeyPaintContext(ProfileId, settings));

        Assert.Equal("", key.IconName);
    }

    [Fact]
    public void PaintContext_TargetLayerOverride_PaintsThatColour()
    {
        var key = NewKey(positionIndex: 7);
        var settings = new UserSettings
        {
            LayerColors = new() { [ProfileId] = new() { [2] = "#FF00AA" } },
        };

        key.ApplyBinding(new KeyBinding("&lt", new[] { "2", "K1" }), activeLayerIndex: 0,
            targetLayer: 2, targetLayerName: "Symbol",
            paint: new KeyPaintContext(ProfileId, settings));

        Assert.Equal("#FF00AA", key.KeyFillColor);
    }

    [Fact]
    public void PaintContext_WithoutOverride_PaintsTheDefaultPaletteColour()
    {
        var key = NewKey(positionIndex: 7);

        key.ApplyBinding(new KeyBinding("&mo", new[] { "2" }), activeLayerIndex: 0,
            targetLayer: 2, targetLayerName: "Symbol",
            paint: new KeyPaintContext(ProfileId, new UserSettings()));

        Assert.Equal(LayerColorPalette.GetDefaultColor(2), key.KeyFillColor);
    }

    [Theory]
    [InlineData("Windows", "⊞")]
    [InlineData("Mac", "⌘")]
    public void PaintContext_ModifierStyleSetting_PicksTheGlyphSet(string style, string expected)
    {
        var key = NewKey(positionIndex: 7);

        key.ApplyBinding(new KeyBinding("&kp", new[] { "LGUI" }), activeLayerIndex: 0,
            targetLayer: null, targetLayerName: null,
            paint: new KeyPaintContext(ProfileId, new UserSettings { ModifierStyle = style }));

        Assert.Equal(expected, key.Label);
    }
}
