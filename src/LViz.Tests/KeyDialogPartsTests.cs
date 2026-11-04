using LViz.App.ViewModels;
using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The key dialog's building blocks: the behavior list in its fixed order
/// with translated names, the behavior filter, the per-kind parameter
/// editors (keycode, layer, raw) and the parameter captions.
/// </summary>
public class KeyDialogPartsTests
{
    // Base/Nav layers, a hold-tap over &kp/&kp and a one-param macro, both
    // defined under the behaviors node (hold-tap as in
    // KeymapLayerTextTests.Parse_HoldTapOverALayerBehaviorCountsItsFirstParam),
    // with "#binding-cells" on its own line as real keymaps write it.
    private const string Keymap = """
        / {
            behaviors {
                hm: hm {
                    compatible = "zmk,behavior-hold-tap";
                    #binding-cells = <2>;
                    bindings = <&kp>, <&kp>;
                };

                m1: m1 {
                    compatible = "zmk,behavior-macro-one-param";
                    #binding-cells = <1>;
                    bindings = <&macro_param_1to1>, <&kp MACRO_PLACEHOLDER>;
                };
            };

            keymap {
                compatible = "zmk,keymap";

                Base {
                    bindings = < &kp N1 >;
                };

                Nav {
                    bindings = < &mo 1 >;
                };
            };
        };
        """;

    private static readonly KeyboardConfig Config =
        ZmkKeymapLoader.LoadFromText(Keymap, "test");

    private static readonly ZmkKeycodeUsage Usage = ZmkKeycodeUsage.Count(Config);

    // --- Behavior choices ---------------------------------------------------

    [Fact]
    public void Quick_AreTheThreeQuickButtons()
    {
        var quick = KeyBehaviorChoices.Quick(Config);

        Assert.Equal(new[] { "&kp", "&trans", "&none" }, quick.Select(c => c.Code));
        Assert.All(quick, c => Assert.NotNull(c.Behavior));
    }

    [Fact]
    public void Others_OwnBehaviorsAfterModTap_AndCustomLast()
    {
        var others = KeyBehaviorChoices.Others(Config);

        Assert.Equal(
            new[] { "&mt", "&lt", "&hm", "&m1", "&to", "&mo", "&tog", "&sl", "&sk", "" },
            others.Select(c => c.Code));
        Assert.True(others[^1].IsCustom);
        Assert.All(others.Take(others.Count - 1), c => Assert.False(c.IsCustom));
    }

    [Fact]
    public void Others_NullConfig_NoOwnBehaviors()
    {
        var others = KeyBehaviorChoices.Others(null);

        Assert.Equal(
            new[] { "&mt", "&lt", "&to", "&mo", "&tog", "&sl", "&sk", "" },
            others.Select(c => c.Code));
    }

    [Fact]
    public void NamesAndHelp_EveryChoiceResolves()
    {
        foreach (var choice in All())
        {
            // A missing resx key renders as "[Key]".
            Assert.DoesNotContain("[", choice.Name);
            Assert.DoesNotContain("[", choice.Help);
        }
    }

    [Fact]
    public void OwnBehavior_ShowsItsCodeAsName_AndKeymapHelp()
    {
        var hm = KeyBehaviorChoices.Others(Config).Single(c => c.Code == "&hm");

        Assert.Equal("&hm", hm.Name);
        Assert.Equal("Defined in this keymap.", hm.Help);
        Assert.False(hm.IsCustom);
    }

    [Fact]
    public void Matches_BlankMatchesAll_FiltersCodeAndNameCaseInsensitively()
    {
        var all = All();
        var lt = all.Single(c => c.Code == "&lt");
        var to = all.Single(c => c.Code == "&to");

        Assert.True(KeyBehaviorChoices.Matches(lt, "lay"));
        Assert.True(KeyBehaviorChoices.Matches(to, "&to"));

        foreach (var blank in new string?[] { null, "", "   " })
        {
            Assert.All(all, c => Assert.True(KeyBehaviorChoices.Matches(c, blank)));
        }

        Assert.DoesNotContain(all, c => KeyBehaviorChoices.Matches(c, "zzz"));
    }

    // --- Parameter labels -----------------------------------------------------

    [Fact]
    public void Labels_MacroNumbered_PairsHoldTap_SingleByKind()
    {
        Assert.Equal(new[] { "Hold", "Tap" }, KeyParamLabels.For(Behavior("&mt"), Config));
        Assert.Equal(new[] { "Hold", "Tap" }, KeyParamLabels.For(Behavior("&lt"), Config));
        Assert.Equal(new[] { "Key" }, KeyParamLabels.For(Behavior("&kp"), Config));
        Assert.Equal(new[] { "Layer" }, KeyParamLabels.For(Behavior("&mo"), Config));
        Assert.Equal(new[] { "Hold", "Tap" }, KeyParamLabels.For(Behavior("&hm"), Config));
        Assert.Equal(new[] { "Parameter 1" }, KeyParamLabels.For(Behavior("&m1"), Config));
        Assert.Empty(KeyParamLabels.For(Behavior("&trans"), Config));

        // Without a config the macro is not one: its single Raw parameter is
        // "Parameter 1" through the kind rule instead.
        Assert.Equal(new[] { "Parameter 1" }, KeyParamLabels.For(Behavior("&m1"), null));
    }

    // --- Layer parameter --------------------------------------------------------

    [Fact]
    public void LayerParam_ListsLayersAndSeedsAnInRangeIndex()
    {
        var vm = new LayerParamViewModel("Layer", Config);

        Assert.Equal(new[] { "0: Base", "1: Nav" }, vm.Layers.Select(l => l.Text));
        Assert.Null(vm.Values);
        Assert.False(vm.HasValue);
        Assert.Equal("", vm.ValueText);

        int changes = 0;
        vm.Changed += (_, _) => changes++;

        Assert.True(vm.Seed(new[] { "1" }));
        Assert.Equal(new[] { "1" }, vm.Values);
        Assert.True(vm.HasValue);
        Assert.Equal("1: Nav", vm.ValueText);
        Assert.Equal(1, changes);

        Assert.False(vm.Seed(new[] { "5" }));
        Assert.False(vm.Seed(new[] { "x" }));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void LayerParam_SelectingRaisesChangedOnce()
    {
        var vm = new LayerParamViewModel("Layer", Config);
        int changes = 0;
        vm.Changed += (_, _) => changes++;

        vm.SelectedLayer = vm.Layers[1];

        Assert.Equal(1, changes);
        Assert.Equal(new[] { "1" }, vm.Values);

        vm.SelectedLayer = null;

        Assert.Equal(2, changes);
        Assert.Null(vm.Values);
        Assert.Equal("", vm.ValueText);
    }

    // --- Raw parameter ----------------------------------------------------------

    [Fact]
    public void RawParam_OnlyOneWhitespaceFreeTokenIsAValue()
    {
        var vm = new RawParamViewModel("Parameter 1");

        Assert.Equal(ZmkBehaviorParamKind.Raw, vm.Kind);
        Assert.Null(vm.Values);
        Assert.Equal("", vm.ValueText);

        vm.Text = "BT_SEL";
        Assert.Equal(new[] { "BT_SEL" }, vm.Values);
        Assert.True(vm.HasValue);
        Assert.Equal("BT_SEL", vm.ValueText);

        vm.Text = "  BT_SEL  ";
        Assert.Equal(new[] { "BT_SEL" }, vm.Values);

        vm.Text = "  ";
        Assert.Null(vm.Values);

        vm.Text = "a b";
        Assert.Null(vm.Values);
        Assert.Equal("a b", vm.ValueText);
    }

    [Fact]
    public void RawParam_ChangedFiresOncePerRealEdit()
    {
        var vm = new RawParamViewModel("Parameter 1");
        int changes = 0;
        vm.Changed += (_, _) => changes++;

        vm.Text = "BT_SEL";
        vm.Text = "BT_SEL";

        Assert.Equal(1, changes);
    }

    // --- Keycode parameter --------------------------------------------------------

    [Fact]
    public void KeycodeParam_ModifierKind_PickerFirst_AndPickFlowsThrough()
    {
        var vm = new KeycodeParamViewModel("Hold", ZmkBehaviorParamKind.Modifier, Usage);

        Assert.Equal("Hold", vm.Label);
        Assert.Equal(ZmkBehaviorParamKind.Modifier, vm.Kind);
        Assert.Equal(ZmkKeycodeCategory.Modifiers, vm.Picker.Categories[0].Category);

        int changes = 0;
        vm.Changed += (_, _) => changes++;

        vm.Picker.SelectedKeycode = ZmkKeycodeCatalog.Find("A")!;

        Assert.Equal(new[] { "A" }, vm.Values);
        Assert.True(vm.HasValue);
        Assert.Equal("A", vm.ValueText);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void KeycodeParam_KeycodeKind_KeepsUsedCategoryFirst()
    {
        var vm = new KeycodeParamViewModel("Key", ZmkBehaviorParamKind.Keycode, Usage);

        Assert.True(vm.Picker.Categories[0].IsUsed);
    }

    // --- helpers ------------------------------------------------------------

    private static List<KeyBehaviorChoice> All() =>
        [.. KeyBehaviorChoices.Quick(Config), .. KeyBehaviorChoices.Others(Config)];

    private static ZmkBehavior Behavior(string code) =>
        ZmkBehaviorCatalog.Find(Config, code)!;
}
