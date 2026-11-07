using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// The pickable behavior list: fixed built-ins in picker order (the v1 set,
/// the command behaviors, the parameter-less key/system behaviors), plus the
/// hold-taps and macros of a loaded keymap mapped to parameter kinds (with
/// 0-cell hold-tap sides <c>Ignored</c>), the pinned layer of a
/// [Layer, Ignored] hold-tap, and built-in-wins de-duplication.
/// </summary>
public class ZmkBehaviorCatalogTests
{
    /// <summary>The 22 built-in codes in picker order; shared by the
    /// For()-append tests.</summary>
    private static readonly string[] BuiltInCodes =
    [
        "&kp", "&mt", "&sk", "&lt", "&mo", "&to", "&tog", "&sl", "&trans", "&none",
        "&bt", "&out", "&rgb_ug", "&mkp", "&msc", "&mmv",
        "&caps_word", "&key_repeat", "&bootloader", "&sys_reset", "&soft_off", "&studio_unlock",
    ];

    [Fact]
    public void BuiltIn_CodesInExactOrder()
    {
        Assert.Equal(BuiltInCodes, ZmkBehaviorCatalog.BuiltIn.Select(b => b.Code));
    }

    [Fact]
    public void BuiltIn_LayerTap_And_Transparent_ParamKinds()
    {
        Assert.Equal(
            new[] { ZmkBehaviorParamKind.Layer, ZmkBehaviorParamKind.Keycode },
            ZmkBehaviorCatalog.Find(null, "&lt")!.Params);
        Assert.Empty(ZmkBehaviorCatalog.Find(null, "&trans")!.Params);
    }

    [Fact]
    public void BuiltIn_Groups()
    {
        Assert.Equal(ZmkBehaviorGroup.Key, ZmkBehaviorCatalog.Find(null, "&kp")!.Group);
        Assert.Equal(ZmkBehaviorGroup.HoldTap, ZmkBehaviorCatalog.Find(null, "&mt")!.Group);
        Assert.Equal(ZmkBehaviorGroup.HoldTap, ZmkBehaviorCatalog.Find(null, "&lt")!.Group);
        Assert.Equal(ZmkBehaviorGroup.Layer, ZmkBehaviorCatalog.Find(null, "&mo")!.Group);
        Assert.Equal(ZmkBehaviorGroup.Clear, ZmkBehaviorCatalog.Find(null, "&none")!.Group);
        Assert.Equal(ZmkBehaviorGroup.Bluetooth, ZmkBehaviorCatalog.Find(null, "&bt")!.Group);
        Assert.Equal(ZmkBehaviorGroup.Output, ZmkBehaviorCatalog.Find(null, "&out")!.Group);
        Assert.Equal(ZmkBehaviorGroup.Lighting, ZmkBehaviorCatalog.Find(null, "&rgb_ug")!.Group);
        Assert.Equal(ZmkBehaviorGroup.Mouse, ZmkBehaviorCatalog.Find(null, "&msc")!.Group);
        Assert.Equal(ZmkBehaviorGroup.System, ZmkBehaviorCatalog.Find(null, "&bootloader")!.Group);
        Assert.Equal(ZmkBehaviorGroup.Key, ZmkBehaviorCatalog.Find(null, "&caps_word")!.Group);
    }

    [Fact]
    public void BuiltIn_CommandParams_AreExactlyTheCommandCatalogCodes()
    {
        var commandBehaviors = ZmkBehaviorCatalog.BuiltIn
            .Where(b => b.Params.Contains(ZmkBehaviorParamKind.Command))
            .ToArray();

        // A Command param is one logical parameter, nothing else alongside it.
        foreach (var behavior in commandBehaviors)
        {
            Assert.Equal(
                new[] { ZmkBehaviorParamKind.Command }, behavior.Params);
        }

        // Same set of codes, ignoring picker order.
        Assert.Equal(
            ZmkCommandCatalog.Behaviors.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            commandBehaviors.Select(b => b.Code).OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("&caps_word")]
    [InlineData("&key_repeat")]
    [InlineData("&bootloader")]
    [InlineData("&sys_reset")]
    [InlineData("&soft_off")]
    [InlineData("&studio_unlock")]
    public void BuiltIn_Parameterless_AgreeWithParserArity(string code)
    {
        Assert.Empty(ZmkBehaviorCatalog.Find(null, code)!.Params);
        Assert.Equal(0, BehaviorArityTable.Arity(code));
    }

    [Fact]
    public void For_Glove80Fixture_MagicIsKeymapAfterAllBuiltIns()
    {
        var config = ZmkKeymapLoader.LoadFromText(
            KeymapFixtures.Read("glove80-editor.keymap"), "glove80");

        var all = ZmkBehaviorCatalog.For(config);

        // The 22 built-ins come first, untouched.
        Assert.Equal(
            BuiltInCodes, all.Take(BuiltInCodes.Length).Select(b => b.Code));

        var index = all.Select(b => b.Code).ToList().IndexOf("&magic");
        Assert.True(
            index >= BuiltInCodes.Length,
            $"&magic at index {index}, expected after all {BuiltInCodes.Length} built-ins");
        Assert.Equal(ZmkBehaviorGroup.Keymap, all[index].Group);
    }

    [Fact]
    public void Find_Glove80Fixture_MagicHoldTap_IsLayerIgnored()
    {
        var config = ZmkKeymapLoader.LoadFromText(
            KeymapFixtures.Read("glove80-editor.keymap"), "glove80");

        var magic = ZmkBehaviorCatalog.Find(config, "&magic");

        Assert.NotNull(magic);
        Assert.Equal(ZmkBehaviorGroup.Keymap, magic!.Group);
        // Hold is &mo → Layer; tap &rgb_ug_status_macro has #binding-cells
        // = <0> → its cell is ignored.
        Assert.Equal(
            new[] { ZmkBehaviorParamKind.Layer, ZmkBehaviorParamKind.Ignored },
            magic.Params);
    }

    [Theory]
    [InlineData("glove80.keymap", 3)]
    [InlineData("go60.keymap", 7)]
    public void Fixture_MagicHoldTap_IsLayerIgnored_AndPinsItsLayer(
        string fixture, int pinnedLayer)
    {
        var config = ZmkKeymapLoader.LoadFromText(
            KeymapFixtures.Read(fixture), "fixture");

        Assert.Equal(
            new[] { ZmkBehaviorParamKind.Layer, ZmkBehaviorParamKind.Ignored },
            ZmkBehaviorCatalog.Find(config, "&magic")!.Params);
        Assert.Equal(pinnedLayer, ZmkBehaviorCatalog.PinnedLayer(config, "&magic"));
    }

    [Fact]
    public void PinnedLayer_Glove80EditorFixture_MatchesTheMagicLayerByName()
    {
        var config = ZmkKeymapLoader.LoadFromText(
            KeymapFixtures.Read("glove80-editor.keymap"), "glove80");

        var magic = config.Layers.Single(l => l.Name == "Magic");

        Assert.Equal(magic.Index, ZmkBehaviorCatalog.PinnedLayer(config, "&magic"));
    }

    [Fact]
    public void For_AppendsHoldTapsAndMacros_SkippingBuiltInCollisions()
    {
        var config = Config(
            holdTaps: new[]
            {
                new HoldTap("&lt", "&mo", "&kp", 1, 1),   // collides with built-in → skipped
                new HoldTap("&hrm", "&kp", "&kp", 1, 1),
            },
            macros: new[]
            {
                new ZmkMacro("&two", new[] { "p1", "p2" }, Array.Empty<KeyBinding>()),
                new ZmkMacro("&zero", Array.Empty<string>(), Array.Empty<KeyBinding>()),
            });

        var all = ZmkBehaviorCatalog.For(config);

        Assert.Equal(
            BuiltInCodes.Concat(new[] { "&hrm", "&two", "&zero" }),
            all.Select(b => b.Code));

        // The user's &lt lost: the built-in entry (HoldTap group) is what remains.
        var lt = ZmkBehaviorCatalog.Find(config, "&lt")!;
        Assert.Equal(ZmkBehaviorGroup.HoldTap, lt.Group);
        Assert.Equal(
            new[] { ZmkBehaviorParamKind.Layer, ZmkBehaviorParamKind.Keycode }, lt.Params);

        Assert.Equal(
            new[] { ZmkBehaviorParamKind.Keycode, ZmkBehaviorParamKind.Keycode },
            ZmkBehaviorCatalog.Find(config, "&hrm")!.Params);
        Assert.Equal(
            new[] { ZmkBehaviorParamKind.Raw, ZmkBehaviorParamKind.Raw },
            ZmkBehaviorCatalog.Find(config, "&two")!.Params);
        Assert.Empty(ZmkBehaviorCatalog.Find(config, "&zero")!.Params);
    }

    [Fact]
    public void For_SkipsLaterDuplicateOfEarlierUserCode()
    {
        var config = Config(
            holdTaps: new[]
            {
                new HoldTap("&dup", "&mo", "&kp", 1, 1),
                new HoldTap("&dup", "&kp", "&kp", 1, 1),
            });

        var all = ZmkBehaviorCatalog.For(config);

        Assert.Equal(
            BuiltInCodes.Append("&dup"),
            all.Select(b => b.Code));
        // First occurrence wins → [Layer, Keycode] (hold &mo, tap &kp).
        Assert.Equal(
            new[] { ZmkBehaviorParamKind.Layer, ZmkBehaviorParamKind.Keycode },
            ZmkBehaviorCatalog.Find(config, "&dup")!.Params);
    }

    [Fact]
    public void For_NullConfig_IsBuiltIn()
    {
        Assert.Same(ZmkBehaviorCatalog.BuiltIn, ZmkBehaviorCatalog.For(null));
    }

    [Fact]
    public void Find_NullConfig_UnknownCode_IsNull()
    {
        Assert.Null(ZmkBehaviorCatalog.Find(null, "&nope"));
    }

    [Fact]
    public void Find_IsCaseSensitive()
    {
        Assert.Null(ZmkBehaviorCatalog.Find(null, "&KP"));
    }

    [Fact]
    public void For_ZeroArityHoldSide_IsIgnored_AndNeverPinned()
    {
        var config = Config(
            holdTaps: new[] { new HoldTap("&sig", "&sig_macro", "&kp", 0, 1) });

        Assert.Equal(
            new[] { ZmkBehaviorParamKind.Ignored, ZmkBehaviorParamKind.Keycode },
            ZmkBehaviorCatalog.Find(config, "&sig")!.Params);
        // [Ignored, Keycode] is not [Layer, Ignored] → never pinnable.
        Assert.Null(ZmkBehaviorCatalog.PinnedLayer(config, "&sig"));
    }

    // A hold-tap whose only visible param is the layer: pinned when every
    // use in the layers/combos targets one and the same in-range layer.
    private static KeyboardConfig MagConfig(
        Layer[]? layers = null, ZmkCombo[]? combos = null) =>
        Config(
            holdTaps: new[] { new HoldTap("&mag", "&mo", "&m0", 1, 0) },
            layers: layers,
            combos: combos);

    private static Layer LayerWith(int index, params KeyBinding[] bindings) =>
        new(index, $"L{index}", bindings);

    private static KeyBinding Mag(params string[] paramStrings) =>
        new("&mag", paramStrings);

    [Fact]
    public void PinnedLayer_MagHoldTap_WithNoUse_IsNull()
    {
        var config = MagConfig(layers: new[]
        {
            LayerWith(0, KeyBinding.Transparent),
            LayerWith(1, KeyBinding.None),
            LayerWith(2, new KeyBinding("&mo", new[] { "1" })),
        });

        Assert.Null(ZmkBehaviorCatalog.PinnedLayer(config, "&mag"));
    }

    [Fact]
    public void PinnedLayer_MagHoldTap_AllUsesSameLayer_ReturnsIt()
    {
        var config = MagConfig(layers: new[]
        {
            LayerWith(0, Mag("2", "0")),
            LayerWith(1, KeyBinding.Transparent, Mag("2", "0")),
            LayerWith(2, KeyBinding.Transparent),
        });

        Assert.Equal(2, ZmkBehaviorCatalog.PinnedLayer(config, "&mag"));
    }

    [Fact]
    public void PinnedLayer_MagHoldTap_UsesDiffer_IsNull()
    {
        var config = MagConfig(layers: new[]
        {
            LayerWith(0, Mag("2", "0")),
            LayerWith(1, Mag("1", "0")),
            LayerWith(2, KeyBinding.Transparent),
        });

        Assert.Null(ZmkBehaviorCatalog.PinnedLayer(config, "&mag"));
    }

    [Fact]
    public void PinnedLayer_MagHoldTap_UseOutsideLayerRange_IsNull()
    {
        var config = MagConfig(layers: new[]
        {
            LayerWith(0, Mag("9", "0")),
            LayerWith(1, KeyBinding.Transparent),
            LayerWith(2, KeyBinding.Transparent),
        });

        Assert.Null(ZmkBehaviorCatalog.PinnedLayer(config, "&mag"));
    }

    [Fact]
    public void PinnedLayer_MagHoldTap_UsedOnlyInComboBinding_ReturnsIt()
    {
        var config = MagConfig(
            layers: new[]
            {
                LayerWith(0, KeyBinding.Transparent),
                LayerWith(1, KeyBinding.None),
            },
            combos: new[]
            {
                new ZmkCombo("c", "", new[] { 0, 1 }, new[] { -1 }, Mag("1", "0")),
            });

        Assert.Equal(1, ZmkBehaviorCatalog.PinnedLayer(config, "&mag"));
    }

    [Fact]
    public void PinnedLayer_MagHoldTap_UseWithoutUsableFirstParam_IsNull()
    {
        var noParams = MagConfig(
            layers: new[] { LayerWith(0, new KeyBinding("&mag", Array.Empty<string>())) });
        var notNumeric = MagConfig(
            layers: new[] { LayerWith(0, Mag("LAYER_Magic", "0")) });

        Assert.Null(ZmkBehaviorCatalog.PinnedLayer(noParams, "&mag"));
        Assert.Null(ZmkBehaviorCatalog.PinnedLayer(notNumeric, "&mag"));
    }

    [Fact]
    public void PinnedLayer_MacrosInnerBindingsDoNotCount()
    {
        var config = Config(
            holdTaps: new[] { new HoldTap("&mag", "&mo", "&m0", 1, 0) },
            macros: new[]
            {
                new ZmkMacro("&wrap", Array.Empty<string>(), new[] { Mag("2", "0") }),
            },
            layers: new[]
            {
                LayerWith(0, KeyBinding.Transparent),
                LayerWith(1, KeyBinding.None),
                LayerWith(2, KeyBinding.None),
            });

        Assert.Null(ZmkBehaviorCatalog.PinnedLayer(config, "&mag"));
    }

    [Fact]
    public void PinnedLayer_BuiltInCode_AndNullConfig_AreNull()
    {
        Assert.Null(ZmkBehaviorCatalog.PinnedLayer(Config(), "&lt"));
        Assert.Null(ZmkBehaviorCatalog.PinnedLayer(null, "&magic"));
    }

    [Fact]
    public void ArityTable_KnowsStickyKey()
    {
        Assert.Equal(1, BehaviorArityTable.Arity("&sk"));
    }

    private static KeyboardConfig Config(
        HoldTap[]? holdTaps = null,
        ZmkMacro[]? macros = null,
        Layer[]? layers = null,
        ZmkCombo[]? combos = null) =>
        new(
            "test",
            layers ?? Array.Empty<Layer>(),
            macros ?? Array.Empty<ZmkMacro>(),
            holdTaps ?? Array.Empty<HoldTap>(),
            combos ?? Array.Empty<ZmkCombo>());
}
