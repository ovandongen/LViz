using LViz.App.ViewModels;
using LViz.Core.Layout;
using LViz.Core.Models;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Exercises the override hook in <see cref="KeyViewModel.ApplyBinding"/>.
/// The static <see cref="KeyLabelOverrides"/> facade is process-wide, so
/// every test clears it before and after to keep the suite order-independent.
/// </summary>
public class KeyViewModelOverrideTests : IDisposable
{
    private const string ProfileId = "TestBoard";

    public KeyViewModelOverrideTests()
        => KeyLabelOverrides.SetOverrides(new());

    public void Dispose()
        => KeyLabelOverrides.SetOverrides(new());

    private static KeyViewModel NewKey(int positionIndex)
        => new(new KeyPosition(positionIndex, X: 0, Y: 0));

    private static KeyBinding KpA() => new("&kp", new[] { "A" });

    [Fact]
    public void NoOverride_FormatterDefaults_Render()
    {
        var key = NewKey(positionIndex: 7);

        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, profileId: ProfileId);

        Assert.Equal("A", key.Label);
        Assert.Null(key.LabelFontSizeOverride);
        Assert.False(key.IsLabelBold);
    }

    [Fact]
    public void Override_ReplacesAllSlots()
    {
        var key = NewKey(positionIndex: 7);
        var ov = new KeyLabelOverride
        {
            MainLabel = "nav",
            Subscript = "space",
            TopLeftBadge = "L1",
            Icon = "fa-house",
            FontSize = 24,
            Bold = true,
        };
        KeyLabelOverrides.Set(ProfileId, layerIndex: 0, keyIndex: 7, ov);

        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, profileId: ProfileId);

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
        KeyLabelOverrides.Set(ProfileId, layerIndex: 0, keyIndex: 7,
            new KeyLabelOverride { MainLabel = "L0only" });

        // Active layer 1 → no override for that slot → formatter wins.
        key.ApplyBinding(KpA(), activeLayerIndex: 1, targetLayer: null,
            targetLayerName: null, profileId: ProfileId);

        Assert.Equal("A", key.Label);
    }

    [Fact]
    public void Override_ScopedByKeyIndex()
    {
        var key = NewKey(positionIndex: 7);
        KeyLabelOverrides.Set(ProfileId, layerIndex: 0, keyIndex: 8,
            new KeyLabelOverride { MainLabel = "wrong" });

        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, profileId: ProfileId);

        Assert.Equal("A", key.Label);
    }

    [Fact]
    public void ClearingOverride_RestoresDefaults()
    {
        var key = NewKey(positionIndex: 7);
        KeyLabelOverrides.Set(ProfileId, 0, 7,
            new KeyLabelOverride { MainLabel = "x", FontSize = 30, Bold = true });
        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, profileId: ProfileId);
        Assert.Equal("x", key.Label);
        Assert.True(key.IsLabelBold);

        KeyLabelOverrides.Set(ProfileId, 0, 7, null);
        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, profileId: ProfileId);

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
        KeyLabelOverrides.Set(ProfileId, 0, 7,
            new KeyLabelOverride { MainLabel = "x" });

        key.ApplyBinding(KpA(), activeLayerIndex: 0, targetLayer: null,
            targetLayerName: null, profileId: ProfileId);

        Assert.Equal("", key.IconName);
    }
}
