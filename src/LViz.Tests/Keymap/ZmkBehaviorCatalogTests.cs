using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// The pickable behavior list: fixed built-ins in picker order (the v1 set,
/// the command behaviors, the parameter-less key/system behaviors), plus the
/// hold-taps and macros of a loaded keymap mapped to parameter kinds, with
/// built-in-wins de-duplication.
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
