using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// The pickable behavior list: fixed v1 built-ins in picker order, plus the
/// hold-taps and macros of a loaded keymap mapped to parameter kinds, with
/// built-in-wins de-duplication.
/// </summary>
public class ZmkBehaviorCatalogTests
{
    [Fact]
    public void BuiltIn_CodesInExactOrder()
    {
        Assert.Equal(
            new[] { "&kp", "&mt", "&sk", "&lt", "&mo", "&to", "&tog", "&sl", "&trans", "&none" },
            ZmkBehaviorCatalog.BuiltIn.Select(b => b.Code));
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
        Assert.Equal(ZmkBehaviorGroup.Keys, ZmkBehaviorCatalog.Find(null, "&kp")!.Group);
        Assert.Equal(ZmkBehaviorGroup.Layers, ZmkBehaviorCatalog.Find(null, "&mo")!.Group);
        Assert.Equal(ZmkBehaviorGroup.Special, ZmkBehaviorCatalog.Find(null, "&none")!.Group);
    }

    [Fact]
    public void Find_Glove80Fixture_MagicHoldTap_IsLayerRaw()
    {
        var config = ZmkKeymapLoader.LoadFromText(
            KeymapFixtures.Read("glove80-editor.keymap"), "glove80");

        var magic = ZmkBehaviorCatalog.Find(config, "&magic");

        Assert.NotNull(magic);
        Assert.Equal(ZmkBehaviorGroup.Keymap, magic!.Group);
        // Hold is &mo → Layer; tap &rgb_ug_status_macro is not a known
        // keycode/layer behavior → Raw.
        Assert.Equal(
            new[] { ZmkBehaviorParamKind.Layer, ZmkBehaviorParamKind.Raw },
            magic.Params);
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
            new[] { "&kp", "&mt", "&sk", "&lt", "&mo", "&to", "&tog", "&sl", "&trans", "&none",
                    "&hrm", "&two", "&zero" },
            all.Select(b => b.Code));

        // The user's &lt lost: the built-in entry (Layers group) is what remains.
        var lt = ZmkBehaviorCatalog.Find(config, "&lt")!;
        Assert.Equal(ZmkBehaviorGroup.Layers, lt.Group);
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
            new[] { "&kp", "&mt", "&sk", "&lt", "&mo", "&to", "&tog", "&sl", "&trans", "&none",
                    "&dup" },
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
    public void ArityTable_KnowsStickyKey()
    {
        Assert.Equal(1, BehaviorArityTable.Arity("&sk"));
    }

    private static KeyboardConfig Config(
        HoldTap[]? holdTaps = null, ZmkMacro[]? macros = null) =>
        new(
            "test",
            Array.Empty<Layer>(),
            macros ?? Array.Empty<ZmkMacro>(),
            holdTaps ?? Array.Empty<HoldTap>(),
            Array.Empty<ZmkCombo>());
}
