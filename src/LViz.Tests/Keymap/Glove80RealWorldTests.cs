using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// End-to-end tests against a real Glove80 Layout Editor export. The header
/// carries zmk-helpers-style multi-line function-like <c>#define</c>s (with
/// backslash continuations), per-layer-name defines, conditional behavior
/// blocks, and pointing-device layers — the constructs a hand-edited Corne
/// keymap doesn't exercise.
/// </summary>
public class Glove80RealWorldTests
{
    private static KeyboardConfig Load() =>
        ZmkKeymapLoader.LoadFromText(KeymapFixtures.Read("glove80-editor.keymap"), "glove80");

    [Fact]
    public void LoadsEightLayers_EightyBindingsEach()
    {
        var config = Load();
        Assert.Equal(
            new[] { "Base", "Symbol", "Lower", "Magic", "Mouse", "MouseSlow", "MouseFast", "MouseWarp" },
            config.Layers.Select(l => l.Name));
        Assert.All(config.Layers, l => Assert.Equal(80, l.Bindings.Count));
    }

    [Fact]
    public void HelperMacroHeader_DoesNotLeakBehaviorNodes()
    {
        // The multi-line ZMK_BEHAVIOR/ZMK_TD_LAYER #defines must not leak a
        // bogus "name" hold-tap/macro into the config. Only the file's real
        // definitions survive: 1 hold-tap (&magic) and 7 macros.
        var config = Load();
        var holdTap = Assert.Single(config.HoldTaps);
        Assert.Equal("&magic", holdTap.Name);
        Assert.Equal(7, config.Macros.Count);
        Assert.DoesNotContain(config.Macros, m => m.Name == "&name");
    }

    [Fact]
    public void MagicKey_RendersTapMacroLabelAndHoldLayer()
    {
        // &magic LAYER_Magic 0 — hold is &mo → Magic, tap is the
        // single-binding rgb_ug_status_macro (&rgb_ug RGB_STATUS).
        var km = new LoadedKeymap(Load());
        var idx = km.Layers[0].Bindings.ToList().FindIndex(b => b.Behavior == "&magic");
        Assert.True(idx >= 0);
        var r = km.Resolve(0, idx);

        var (label, sub, top) = KeyLabelFormatter.FormatBinding(
            r.Binding, r.TargetLayerName, r.HoldTap, km.MacrosByName);

        Assert.Equal("Status", label);
        Assert.Equal("Magic", sub);
        Assert.Equal("Hold-Tap", top);
    }

    [Fact]
    public void MouseLayer_RendersPointingLabels()
    {
        var config = Load();
        var mouse = config.Layers.Single(l => l.Name == "Mouse").Bindings;

        var scroll = mouse.First(b => b.Behavior == "&msc" && b.Params.SequenceEqual(new[] { "SCRL_UP" }));
        var move = mouse.First(b => b.Behavior == "&mmv" && b.Params.SequenceEqual(new[] { "MOVE_LEFT" }));
        var click = mouse.First(b => b.Behavior == "&mkp" && b.Params.SequenceEqual(new[] { "LCLK" }));

        Assert.Equal(("↑", "", "Scroll"), KeyLabelFormatter.FormatBinding(scroll, null));
        Assert.Equal(("←", "", "Mouse"), KeyLabelFormatter.FormatBinding(move, null));
        Assert.Equal(("L Clk", "", "Click"), KeyLabelFormatter.FormatBinding(click, null));
    }

    [Fact]
    public void LayerNameDefines_ResolveToIndices()
    {
        // &lt LAYER_Symbol RET — the per-layer #defines must substitute so
        // the layer-tap targets layer 1 (Symbol).
        var config = Load();
        var lt = config.Layers[0].Bindings.First(b => b.Behavior == "&lt");
        Assert.Equal(new[] { "1", "RET" }, lt.Params);
    }
}
