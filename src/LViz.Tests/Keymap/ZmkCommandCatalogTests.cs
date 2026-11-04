using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// The named-command catalog: the bundled ZMK command names per behavior in
/// picker order, plus the commands an open keymap uses that the bundled list
/// doesn't know (offered as FromKeymap extras, first-seen order, deduped).
/// </summary>
public class ZmkCommandCatalogTests
{
    private static readonly string[] BtNames =
        { "BT_SEL", "BT_NXT", "BT_PRV", "BT_CLR", "BT_CLR_ALL", "BT_DISC" };

    private static readonly string[] RgbUgNames =
    {
        "RGB_TOG", "RGB_ON", "RGB_OFF", "RGB_BRI", "RGB_BRD",
        "RGB_HUI", "RGB_HUD", "RGB_SAI", "RGB_SAD",
        "RGB_SPI", "RGB_SPD", "RGB_EFF", "RGB_EFR",
    };

    [Fact]
    public void Behaviors_CodesInExactOrder()
    {
        Assert.Equal(
            new[] { "&bt", "&out", "&rgb_ug", "&mkp", "&msc", "&mmv" },
            ZmkCommandCatalog.Behaviors);
    }

    [Fact]
    public void IsCommandBehavior_IsOrdinal()
    {
        Assert.True(ZmkCommandCatalog.IsCommandBehavior("&bt"));
        Assert.False(ZmkCommandCatalog.IsCommandBehavior("&kp"));
        Assert.False(ZmkCommandCatalog.IsCommandBehavior("&BT"));
        Assert.False(ZmkCommandCatalog.IsCommandBehavior(""));
        Assert.False(ZmkCommandCatalog.IsCommandBehavior(null));
    }

    [Fact]
    public void Bundled_Bt_NamesInOrder_NumbersFromArityTable()
    {
        var bt = ZmkCommandCatalog.Bundled("&bt");

        Assert.Equal(BtNames, bt.Select(c => c.Name));
        // TakesNumber is true exactly for the profile-number commands.
        Assert.Equal(
            new[] { true, false, false, false, false, true },
            bt.Select(c => c.TakesNumber));
    }

    [Fact]
    public void Bundled_UnknownBehavior_IsEmpty()
    {
        Assert.Empty(ZmkCommandCatalog.Bundled("&kp"));
    }

    [Theory]
    [InlineData("&bt", 6)]
    [InlineData("&out", 3)]
    [InlineData("&rgb_ug", 13)]
    [InlineData("&mkp", 5)]
    [InlineData("&msc", 4)]
    [InlineData("&mmv", 4)]
    public void Bundled_CountPerBehavior(string code, int count)
    {
        Assert.Equal(count, ZmkCommandCatalog.Bundled(code).Count);
    }

    [Fact]
    public void Bundled_NoEntryIsFromKeymap()
    {
        var all = ZmkCommandCatalog.Behaviors
            .SelectMany(ZmkCommandCatalog.Bundled)
            .ToList();
        Assert.NotEmpty(all);
        Assert.All(all, c => Assert.False(c.FromKeymap));
    }

    [Fact]
    public void ProfileNumbers_DefaultFiveProfiles()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, ZmkCommandCatalog.ProfileNumbers);
    }

    [Fact]
    public void Glove80_RgbUg_AddsKeymapExtraRgbStatus()
    {
        var config = Glove80();

        var rgb = ZmkCommandCatalog.For("&rgb_ug", config);

        // The 13 bundled names, then RGB_STATUS (from the macro
        // rgb_ug_status_macro) as the only extra.
        Assert.Equal(
            new[] { "RGB_TOG", "RGB_ON", "RGB_OFF", "RGB_BRI", "RGB_BRD",
                    "RGB_HUI", "RGB_HUD", "RGB_SAI", "RGB_SAD",
                    "RGB_SPI", "RGB_SPD", "RGB_EFF", "RGB_EFR", "RGB_STATUS" },
            rgb.Select(c => c.Name));
        var extra = rgb[^1];
        Assert.True(extra.FromKeymap);
        Assert.False(extra.TakesNumber);
    }

    [Theory]
    [InlineData("&bt")]
    [InlineData("&out")]
    [InlineData("&mkp")]
    [InlineData("&msc")]
    [InlineData("&mmv")]
    public void Glove80_OtherCommandBehaviors_HaveNoExtras(string code)
    {
        var config = Glove80();
        Assert.DoesNotContain(ZmkCommandCatalog.For(code, config), c => c.FromKeymap);
    }

    [Theory]
    [InlineData("cosmos82.keymap")]
    [InlineData("slabwing.keymap")]
    public void RealFixtures_NoBehaviorHasAnExtra(string fixture)
    {
        var config = ZmkKeymapLoader.LoadFromText(KeymapFixtures.Read(fixture), "test");
        var withExtras = ZmkCommandCatalog.Behaviors
            .Where(code => ZmkCommandCatalog.For(code, config).Any(c => c.FromKeymap))
            .ToList();
        // On failure this lists the behaviors that gained an extra.
        Assert.Empty(withExtras);
    }

    [Fact]
    public void For_KeymapExtras_AreDedupedPerBehavior()
    {
        var config = Keymap(
            "&rgb_ug RGB_STATUS &rgb_ug RGB_STATUS &out OUT_NONE &rgb_ug RGB_TOG");

        // RGB_STATUS twice → once; RGB_TOG is bundled → no extra;
        // OUT_NONE belongs to &out only.
        Assert.Equal(new[] { "RGB_STATUS" }, Extras("&rgb_ug", config).Select(c => c.Name));
        Assert.Equal(new[] { "OUT_NONE" }, Extras("&out", config).Select(c => c.Name));
    }

    [Fact]
    public void For_NestedParamAndProfileNumber_AreNotExtras()
    {
        // RGB_COLOR_HSB(0,100,50) flattens to several params; &bt BT_SEL 0
        // takes its profile number — neither is a one-param command.
        var config = Keymap("&rgb_ug RGB_COLOR_HSB(0,100,50) &bt BT_SEL 0");

        Assert.Empty(Extras("&rgb_ug", config));
        Assert.Empty(Extras("&bt", config));
    }

    [Fact]
    public void For_NonCommandBehavior_IsEmptyEvenWithConfig()
    {
        var config = Keymap("&rgb_ug RGB_STATUS");
        Assert.Empty(ZmkCommandCatalog.For("&kp", config));
    }

    [Fact]
    public void Find_BundledTakesNumber_IsVisibleWithoutConfig()
    {
        var btSel = ZmkCommandCatalog.Find("&bt", "BT_SEL", null);
        Assert.NotNull(btSel);
        Assert.True(btSel!.TakesNumber);
    }

    [Fact]
    public void Find_KeymapExtra_IsVisibleOnlyWithConfig()
    {
        var config = Glove80();

        Assert.NotNull(config);
        Assert.Null(ZmkCommandCatalog.Find("&rgb_ug", "RGB_STATUS", null));
        Assert.NotNull(ZmkCommandCatalog.Find("&rgb_ug", "RGB_STATUS", config));
    }

    [Fact]
    public void Find_NonCommandBehavior_IsNull()
    {
        var config = Glove80();
        Assert.Null(ZmkCommandCatalog.Find("&kp", "A", config));
    }

    [Fact]
    public void For_NullConfig_IsBundled()
    {
        Assert.Equal(
            ZmkCommandCatalog.Bundled("&rgb_ug"),
            ZmkCommandCatalog.For("&rgb_ug", null));
    }

    private static KeyboardConfig Glove80() =>
        ZmkKeymapLoader.LoadFromText(KeymapFixtures.Read("glove80-editor.keymap"), "glove80");

    private static List<ZmkCommand> Extras(string code, KeyboardConfig config) =>
        ZmkCommandCatalog.For(code, config).Where(c => c.FromKeymap).ToList();

    private static KeyboardConfig Keymap(string bindingsCellArray)
    {
        var src = $@"
            / {{
                keymap {{
                    compatible = ""zmk,keymap"";
                    default_layer {{
                        bindings = <{bindingsCellArray}>;
                    }};
                }};
            }};";
        return ZmkKeymapLoader.LoadFromText(src, "test");
    }
}
