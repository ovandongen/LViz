using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// Tests the Moergo Layout Editor <c>.json</c> loader against two real exports:
/// a Glove80 (80 keys) and a GO60 (60 keys, the richer of the two — combos,
/// hold-taps, deeply nested modifier params, decorations).
/// </summary>
public class MoergoJsonKeymapLoaderTests
{
    private static KeyboardConfig LoadGlove80() =>
        MoergoJsonKeymapLoader.LoadFromText(KeymapFixtures.Read("moergo-glove80.json"), "Glove80");

    private static KeyboardConfig LoadGo60() =>
        MoergoJsonKeymapLoader.LoadFromText(KeymapFixtures.Read("moergo-go60.json"), "GO60");

    [Fact]
    public void Glove80_EightLayers_EightyKeysEach_WithNames()
    {
        var config = LoadGlove80();
        Assert.Equal("Glove80", config.KeyboardId);
        Assert.Equal(
            new[] { "Base", "Symbol", "Lower", "Magic", "Mouse", "MouseSlow", "MouseFast", "MouseWarp" },
            config.Layers.Select(l => l.Name));
        Assert.All(config.Layers, l => Assert.Equal(80, l.Bindings.Count));
    }

    [Fact]
    public void Glove80_MapsMacros()
    {
        var config = LoadGlove80();
        Assert.Equal(new[] { "&Tolayer", "&Molayer" }, config.Macros.Select(m => m.Name));
    }

    [Fact]
    public void Glove80_FlattensSingleModifierWrap()
    {
        // L1K11: &kp LS(N7) → params ["LS", "N7"].
        var binding = LoadGlove80().Layers[1].Bindings[11];
        Assert.Equal("&kp", binding.Behavior);
        Assert.Equal(new[] { "LS", "N7" }, binding.Params);
    }

    [Fact]
    public void Go60_FiveLayers_SixtyKeysEach()
    {
        var config = LoadGo60();
        Assert.Equal(
            new[] { "Base", "Symbol", "Cursor", "Keypad", "Magic" },
            config.Layers.Select(l => l.Name));
        Assert.All(config.Layers, l => Assert.Equal(60, l.Bindings.Count));
    }

    [Fact]
    public void Go60_MapsMacrosHoldTapsAndCombos()
    {
        var config = LoadGo60();
        Assert.Equal(10, config.Macros.Count);
        Assert.Equal(2, config.HoldTaps.Count);
        Assert.Equal(4, config.Combos.Count);
    }

    [Fact]
    public void Go60_HoldTap_DerivesArityFromInnerBehaviors()
    {
        // &HRM_left_hand_v1_TKZ wraps &kp/&kp → hold/tap arity 1/1.
        var ht = LoadGo60().HoldTaps.Single(h => h.Name == "&HRM_left_hand_v1_TKZ");
        Assert.Equal("&kp", ht.HoldBinding);
        Assert.Equal("&kp", ht.TapBinding);
        Assert.Equal(1, ht.HoldArity);
        Assert.Equal(1, ht.TapArity);
    }

    [Fact]
    public void Go60_Combo_CarriesPositionsAndAllLayers()
    {
        var combo = LoadGo60().Combos.Single(c => c.Name == "F12");
        Assert.Equal(new[] { 1, 2 }, combo.KeyPositions);
        Assert.True(combo.AppliesToAllLayers); // layers: [-1]
        Assert.Equal("&kp", combo.Binding.Behavior);
        Assert.Equal(new[] { "F12" }, combo.Binding.Params);
    }

    [Fact]
    public void Go60_FlattensNestedModifierWrap_AndDecoration()
    {
        // L1K11: &kp LA(LC(N5)) with decoration {label "€", background "#fbe6fe"}.
        var binding = LoadGo60().Layers[1].Bindings[11];
        Assert.Equal("&kp", binding.Behavior);
        Assert.Equal(new[] { "LA", "LC", "N5" }, binding.Params);
        Assert.Equal("€", binding.DecorationLabel);
        Assert.Equal("#fbe6fe", binding.DecorationBackground);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    public void Malformed_Throws(string json)
    {
        Assert.Throws<ZmkKeymapParseException>(
            () => MoergoJsonKeymapLoader.LoadFromText(json, "Glove80"));
    }

    [Theory]
    [InlineData("{}")]                                  // no keyboard field
    [InlineData("{\"keyboard\":\"corne\",\"layers\":[]}")] // non-Moergo board
    public void NonMoergoExport_Rejected(string json)
    {
        var ex = Assert.Throws<ZmkKeymapParseException>(
            () => MoergoJsonKeymapLoader.LoadFromText(json, "Glove80"));
        Assert.Contains("Moergo", ex.Message);
    }
}
