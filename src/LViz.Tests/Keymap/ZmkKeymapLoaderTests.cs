using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

public class ZmkKeymapLoaderTests
{
    private static string LoadFixture(string name) => KeymapFixtures.Read(name);

    // ---- minimal.keymap ----------------------------------------------------

    [Fact]
    public void MinimalKeymap_HasOneLayerNamedBase()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("minimal.keymap"), "test");
        Assert.Single(config.Layers);
        Assert.Equal("Base", config.Layers[0].Name);
    }

    [Fact]
    public void MinimalKeymap_FirstBindingsAreKpABC()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("minimal.keymap"), "test");
        var bindings = config.Layers[0].Bindings;
        Assert.Equal("&kp", bindings[0].Behavior);
        Assert.Equal("A", bindings[0].Params[0]);
        Assert.Equal("&kp", bindings[1].Behavior);
        Assert.Equal("B", bindings[1].Params[0]);
        Assert.Equal("&kp", bindings[2].Behavior);
        Assert.Equal("C", bindings[2].Params[0]);
    }

    [Fact]
    public void MinimalKeymap_LayerSwitchBehaviorsParseWithCorrectArity()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("minimal.keymap"), "test");
        var bindings = config.Layers[0].Bindings;
        var mo = bindings.First(b => b.Behavior == "&mo");
        Assert.Single(mo.Params);
        Assert.Equal("1", mo.Params[0]);

        var lt = bindings.First(b => b.Behavior == "&lt");
        Assert.Equal(2, lt.Params.Count);
        Assert.Equal("1", lt.Params[0]);
        Assert.Equal("SPACE", lt.Params[1]);

        var mt = bindings.First(b => b.Behavior == "&mt");
        Assert.Equal(new[] { "LSHFT", "D" }, mt.Params);
    }

    [Fact]
    public void MinimalKeymap_BtSelAndBtClrSliceCorrectly()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("minimal.keymap"), "test");
        var bts = config.Layers[0].Bindings.Where(b => b.Behavior == "&bt").ToList();
        Assert.Equal(2, bts.Count);
        Assert.Equal(new[] { "BT_SEL", "0" }, bts[0].Params);
        Assert.Equal(new[] { "BT_CLR" }, bts[1].Params);
    }

    [Fact]
    public void MinimalKeymap_ZeroArityBehaviorsHaveNoParams()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("minimal.keymap"), "test");
        var bindings = config.Layers[0].Bindings;
        Assert.Empty(bindings.First(b => b.Behavior == "&trans").Params);
        Assert.Empty(bindings.First(b => b.Behavior == "&none").Params);
        Assert.Empty(bindings.First(b => b.Behavior == "&sys_reset").Params);
        Assert.Empty(bindings.First(b => b.Behavior == "&bootloader").Params);
    }

    // ---- with_combos_macros.keymap ----------------------------------------

    [Fact]
    public void RichKeymap_HasTwoLayers()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("with_combos_macros.keymap"), "test");
        Assert.Equal(2, config.Layers.Count);
        Assert.Equal("Base", config.Layers[0].Name);
        Assert.Equal("lower", config.Layers[1].Name);
    }

    [Fact]
    public void RichKeymap_SingleComboWithCorrectKeyPositions()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("with_combos_macros.keymap"), "test");
        var combo = Assert.Single(config.Combos);
        Assert.Equal(new[] { 10, 11 }, combo.KeyPositions);
        Assert.Equal("&kp", combo.Binding.Behavior);
        Assert.Equal("ESC", combo.Binding.Params[0]);
        // Default layer scope when 'layers' property is absent.
        Assert.True(combo.AppliesToAllLayers);
    }

    [Fact]
    public void RichKeymap_HasOneMacro()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("with_combos_macros.keymap"), "test");
        var macro = Assert.Single(config.Macros);
        Assert.Equal("&my_macro", macro.Name);
        Assert.Equal(3, macro.Bindings.Count);
    }

    [Fact]
    public void RichKeymap_HoldTapRegisteredWithArity2()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("with_combos_macros.keymap"), "test");
        var ht = Assert.Single(config.HoldTaps);
        Assert.Equal("&ht", ht.Name);
        Assert.Equal("&mo", ht.HoldBinding);
        Assert.Equal("&kp", ht.TapBinding);
    }

    [Fact]
    public void RichKeymap_UsageSiteOfUserHoldTapSlicesWithRegisteredArity()
    {
        // &ht LSHFT A — proves the two-pass interpretation uses the
        // hold-tap's #binding-cells (= 2) when slicing layer bindings.
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("with_combos_macros.keymap"), "test");
        var ht = config.Layers[0].Bindings.First(b => b.Behavior == "&ht");
        Assert.Equal(new[] { "LSHFT", "A" }, ht.Params);
    }

    [Fact]
    public void RichKeymap_MoLowerResolvesToLayerIndex1ViaDefine()
    {
        // #define LOWER 1 must be substituted so &mo LOWER becomes &mo 1.
        // Without this, LayerBindingResolver can't build the predecessor graph.
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("with_combos_macros.keymap"), "test");
        var mo = config.Layers[0].Bindings.First(b => b.Behavior == "&mo");
        Assert.Equal("1", mo.Params[0]);
    }

    // ---- error path -------------------------------------------------------

    [Fact]
    public void EmptyOrUnrecognizedSource_ThrowsParseException()
    {
        var ex = Assert.Throws<ZmkKeymapParseException>(
            () => ZmkKeymapLoader.LoadFromText("// just a comment", "test"));
        Assert.Contains("zmk,keymap", ex.Message);
    }

    [Fact]
    public void MissingFile_ThrowsParseExceptionWithPath()
    {
        var ex = Assert.Throws<ZmkKeymapParseException>(
            () => ZmkKeymapLoader.Load("/no/such/file.keymap", "test"));
        Assert.Equal("/no/such/file.keymap", ex.FilePath);
    }

    [Fact]
    public void LayersWithDifferentBindingCounts_ThrowsParseException()
    {
        // Second layer is short by one key — would render a half-empty board
        // without this check.
        var src = @"
            / {
                keymap {
                    compatible = ""zmk,keymap"";
                    default_layer { bindings = <&kp A &kp B &kp C>; };
                    other_layer   { bindings = <&kp X &kp Y>; };
                };
            };";
        var ex = Assert.Throws<ZmkKeymapParseException>(
            () => ZmkKeymapLoader.LoadFromText(src, "test"));
        Assert.Contains("inconsistent", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default_layer", ex.Message);
        Assert.Contains("other_layer", ex.Message);
    }

    // ---- devicetree "#..." properties at line start --------------------------

    [Fact]
    public void BindingCellsOnItsOwnLine_IsAPropertyNotADirective()
    {
        // Real keymaps put "#binding-cells" on its own line; the preprocessor
        // must not drop it as an unknown directive.
        const string text = """
            #define NAV 1
            / {
                macros {
                    m1: m1 {
                        compatible = "zmk,behavior-macro-one-param";
                        #binding-cells = <1>;
                        bindings = <&macro_param_1to1>, <&kp MACRO_PLACEHOLDER>;
                    };
                };
                keymap {
                    compatible = "zmk,keymap";
                    base { bindings = <&m1 A &mo NAV>; };
                };
            };
            """;

        var config = ZmkKeymapLoader.LoadFromText(text, "test");

        Assert.Single(config.Macros[0].Params);
        Assert.Equal(new[] { "A" }, config.Layers[0].Bindings[0].Params);
        Assert.Equal(new[] { "1" }, config.Layers[0].Bindings[1].Params);
    }

    // ---- combo properties -------------------------------------------------

    [Fact]
    public void SlabwingComboBootloader_HasNodeNameTimeoutAndDefaults()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("slabwing.keymap"), "test");
        var combo = config.Combos.First(c => c.Name == "combo_bootloader");
        Assert.Equal("combo_bootloader", combo.NodeName);
        Assert.Equal(80, combo.TimeoutMs);
        Assert.Null(combo.RequirePriorIdleMs);
        Assert.False(combo.SlowRelease);
    }

    [Fact]
    public void SlabwingCombosWithoutTimeout_HaveNullTimeoutMs()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("slabwing.keymap"), "test");
        foreach (var name in new[] { "mouse-mid", "f12", "syslayer" })
            Assert.Null(config.Combos.First(c => c.Name == name).TimeoutMs);
    }

    [Fact]
    public void SlabwingSyslayerCombo_KeepsKeyPositionsInFileOrder()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("slabwing.keymap"), "test");
        var syslayer = config.Combos.First(c => c.Name == "syslayer");
        Assert.Equal(new[] { 53, 52 }, syslayer.KeyPositions);
    }

    [Fact]
    public void ComboNodeWithAllProperties_ParsesEveryField()
    {
        const string text = """
            / {
                combos {
                    compatible = "zmk,combos";
                    lbl: combo_x {
                        timeout-ms = <40>;
                        require-prior-idle-ms = <150>;
                        slow-release;
                        key-positions = <0 1>;
                        layers = <1 2>;
                        bindings = <&kp A>;
                    };
                };
                keymap {
                    compatible = "zmk,keymap";
                    base { bindings = <&kp A &kp B>; };
                };
            };
            """;

        var config = ZmkKeymapLoader.LoadFromText(text, "test");
        var combo = Assert.Single(config.Combos);
        Assert.Equal("lbl", combo.Name);
        Assert.Equal("combo_x", combo.NodeName);
        Assert.Equal(40, combo.TimeoutMs);
        Assert.Equal(150, combo.RequirePriorIdleMs);
        Assert.True(combo.SlowRelease);
        Assert.Equal(new[] { 1, 2 }, combo.Layers);
    }

    [Fact]
    public void Go60Combos_HaveNodeNames()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("go60.keymap"), "test");
        Assert.Equal(
            new[] { "combo_Space", "combo_snipe3" },
            config.Combos.Select(c => c.NodeName).ToList());
    }
}
