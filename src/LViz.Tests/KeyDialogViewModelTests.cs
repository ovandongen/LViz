using LViz.App.Localization;
using LViz.App.ViewModels;
using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The key dialog view-model: seeding a key's binding into the behavior +
/// parameter editors, the round trip (open and confirm unchanged gives back
/// exactly the old binding), behavior switches carrying what still fits,
/// tabs, Custom text, and the result/error/preview surface.
/// </summary>
public class KeyDialogViewModelTests
{
    // Base(0)/Nav(1), #define NAV 1, one hold-tap, and the eight sample
    // bindings on Base.
    private const string Keymap = """
        #define NAV 1

        / {
            behaviors {
                hm: hm {
                    compatible = "zmk,behavior-hold-tap";
                    #binding-cells = <2>;
                    bindings = <&kp>, <&kp>;
                };
            };

            keymap {
                compatible = "zmk,keymap";

                Base { bindings = < &kp N1 &kp LS(A) &mt F1 N1 &lt NAV SPACE &trans &bt BT_SEL 0 &hm LSHFT A &mo NAV >; };

                Nav { bindings = < &trans &trans &trans &trans &trans &trans &trans &trans >; };
            };
        };
        """;

    private static readonly KeyboardConfig Config = ZmkKeymapLoader.LoadFromText(Keymap, "test");

    private static readonly IReadOnlyList<KeyValuePair<string, string>> Defines =
        Preprocessor.CollectDefines(Keymap);

    // One Base layer, six keys: &bt BT_DISC 1, &out OUT_USB, &mkp LCLK,
    // &bootloader, &out OUT_NONE (a keymap extra) and a nested rgb_ug
    // parameter the command editor cannot show.
    private const string CommandKeymap = """
        / {
            keymap {
                compatible = "zmk,keymap";

                Base { bindings = < &bt BT_DISC 1 &out OUT_USB &mkp LCLK &bootloader &out OUT_NONE &rgb_ug RGB_COLOR_HSB(0,100,50) >; };
            };
        };
        """;

    private static readonly KeyboardConfig CommandConfig =
        ZmkKeymapLoader.LoadFromText(CommandKeymap, "test");

    private static readonly IReadOnlyList<KeyValuePair<string, string>> CommandDefines =
        Preprocessor.CollectDefines(CommandKeymap);

    // --- Round trip ---------------------------------------------------------

    [Theory]
    // &kp N1, &kp LS(A), &mt F1 N1, &lt NAV SPACE, &trans, &bt BT_SEL 0,
    // &hm LSHFT A, &mo NAV
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void FreshDialog_ResultEqualsTheOriginalBinding(int keyIndex)
    {
        var original = Config.Layers[0].Bindings[keyIndex];

        var result = Dialog(keyIndex).Result;

        Assert.NotNull(result);
        Assert.Equal(original.Behavior, result.Behavior);
        Assert.True(original.Params.SequenceEqual(result.Params));
    }

    // --- Opening on a binding -------------------------------------------------

    [Fact]
    public void KpKey_OpensSelected_OnItsParamTab_WithHeadersAndPreview()
    {
        var vm = Dialog(0);

        Assert.Equal("&kp", vm.SelectedBehavior.Code);
        Assert.Equal(1, vm.SelectedTabIndex);
        Assert.Equal(new[] { "Behavior: &kp", "Key: N1" }, vm.Tabs.Select(tab => tab.Header));
        Assert.Equal("&kp N1", vm.PreviewText);
        Assert.True(vm.CanConfirm);
        Assert.Equal("", vm.ErrorText);
    }

    [Fact]
    public void LayerTap_SeedsTheLayerEditor_AndPreviewsThroughTheDefine()
    {
        var vm = Dialog(3);
        var layer = Assert.IsType<LayerParamViewModel>(vm.Params[0]);

        Assert.Equal(1, layer.SelectedLayer!.Index);
        Assert.Equal("&lt NAV SPACE", vm.PreviewText);
    }

    [Fact]
    public void UnknownBehavior_OpensAsCustomText_WithoutParams()
    {
        const string text = """
            / {
                keymap {
                    compatible = "zmk,keymap";
                    Base { bindings = < &ext_power EP_ON >; };
                };
            };
            """;
        var config = ZmkKeymapLoader.LoadFromText(text, "test");
        var vm = new KeyDialogViewModel(new KeyDialogRequest(config, Preprocessor.CollectDefines(text), 0, 0));

        Assert.True(vm.IsCustom);
        Assert.Equal("&ext_power EP_ON", vm.CustomText);
        Assert.Empty(vm.Params);
        Assert.Equal(0, vm.SelectedTabIndex);
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public void TitleText_NamesKeyAndLayer()
    {
        Assert.Equal("Key 3 on layer Base", Dialog(3).TitleText);
    }

    // --- Opening on a command binding ---------------------------------------

    [Fact]
    public void BtKey_OpensOnItsCommandTab_WithCommandAndProfile()
    {
        var original = Config.Layers[0].Bindings[5];
        var vm = Dialog(5);
        var command = Assert.IsType<CommandParamViewModel>(Assert.Single(vm.Params));

        Assert.Equal("&bt", vm.SelectedBehavior.Code);
        Assert.False(vm.IsCustom);
        Assert.Equal("BT_SEL", command.SelectedCommand!.Command.Name);
        Assert.Equal(0, command.SelectedProfile!.Number);
        Assert.Equal(1, vm.SelectedTabIndex);
        Assert.Equal("&bt BT_SEL 0", vm.PreviewText);

        Assert.Equal(original.Behavior, vm.Result!.Behavior);
        Assert.True(original.Params.SequenceEqual(vm.Result.Params));
    }

    [Fact]
    public void CommandKeymap_NumberedCommand_SeedsCommandAndProfile()
    {
        var vm = CommandDialog(0);
        var command = Assert.IsType<CommandParamViewModel>(Assert.Single(vm.Params));

        Assert.Equal("&bt", vm.SelectedBehavior.Code);
        Assert.Equal("BT_DISC", command.SelectedCommand!.Command.Name);
        Assert.True(command.NeedsProfile);
        Assert.Equal(1, command.SelectedProfile!.Number);
    }

    [Theory]
    [InlineData(1, "&out", "OUT_USB")]
    [InlineData(2, "&mkp", "LCLK")]
    public void CommandKeymap_PlainCommandKeys_SeedsTheirCommand(int keyIndex, string code, string commandName)
    {
        var vm = CommandDialog(keyIndex);
        var command = Assert.IsType<CommandParamViewModel>(Assert.Single(vm.Params));

        Assert.Equal(code, vm.SelectedBehavior.Code);
        Assert.Equal(commandName, command.SelectedCommand!.Command.Name);
        Assert.False(command.NeedsProfile);
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public void CommandKeymap_ParameterlessKey_OpensOnBehaviorTab()
    {
        var vm = CommandDialog(3);

        Assert.Equal("&bootloader", vm.SelectedBehavior.Code);
        Assert.Empty(vm.Params);
        Assert.Equal(0, vm.SelectedTabIndex);
    }

    [Fact]
    public void CommandKeymap_KeymapExtraCommand_SeedsWithFromKeymapFlag()
    {
        var vm = CommandDialog(4);
        var command = Assert.IsType<CommandParamViewModel>(Assert.Single(vm.Params));

        Assert.Equal("&out", vm.SelectedBehavior.Code);
        Assert.Equal("OUT_NONE", command.SelectedCommand!.Command.Name);
        Assert.True(command.SelectedCommand.Command.FromKeymap);
    }

    [Fact]
    public void CommandKeymap_NestedCommandParam_OpensAsCustom()
    {
        Assert.True(CommandDialog(5).IsCustom);
    }

    // --- Behavior switches ----------------------------------------------------

    [Fact]
    public void SwitchingFromKpToMt_KeepsTheKeyInTap_LandsOnEmptyHold()
    {
        var vm = Dialog(0);
        vm.SelectedBehavior = Choice(vm, "&mt");

        Assert.Equal(1, vm.SelectedTabIndex);
        Assert.Equal("", vm.Params[0].ValueText);
        Assert.Equal("N1", vm.Params[1].ValueText);
        Assert.Null(vm.Result);
        Assert.False(vm.CanConfirm);

        Picker(vm, 0).SelectedKeycode = ZmkKeycodeCatalog.Find("F1")!;

        Assert.Equal("&mt", vm.Result!.Behavior);
        Assert.True(new[] { "F1", "N1" }.SequenceEqual(vm.Result.Params));
    }

    [Fact]
    public void SwitchingFromMtToKp_KeepsTheTap()
    {
        var vm = Dialog(2);
        vm.SelectedBehavior = Choice(vm, "&kp");

        Assert.Equal("&kp", vm.Result!.Behavior);
        Assert.True(new[] { "N1" }.SequenceEqual(vm.Result.Params));
    }

    [Fact]
    public void SwitchingFromLtToMo_KeepsTheLayer()
    {
        var vm = Dialog(3);
        vm.SelectedBehavior = Choice(vm, "&mo");

        Assert.Equal("&mo", vm.Result!.Behavior);
        Assert.True(new[] { "1" }.SequenceEqual(vm.Result.Params));
    }

    [Fact]
    public void SwitchingToTrans_ResultIsTrans_AndStaysOnTabZero()
    {
        var vm = Dialog(0);
        vm.SelectedBehavior = Choice(vm, "&trans");

        Assert.Equal("&trans", vm.Result!.Behavior);
        Assert.Empty(vm.Result.Params);
        Assert.Equal(0, vm.SelectedTabIndex);
    }

    [Fact]
    public void SwitchingToBt_LandsOnEmptyCommandTab_AndCompletesOnProfilePick()
    {
        var vm = Dialog(0);
        vm.SelectedBehavior = Choice(vm, "&bt");
        var command = Assert.IsType<CommandParamViewModel>(Assert.Single(vm.Params));

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Null(command.SelectedCommand);
        Assert.Null(command.SelectedProfile);
        Assert.False(vm.CanConfirm);
        Assert.Equal(1, vm.SelectedTabIndex);

        command.SelectedCommand = command.Commands.First(c => c.Command.Name == "BT_SEL");
        Assert.False(vm.CanConfirm);

        command.SelectedProfile = command.Profiles.First(p => p.Number == 3);

        Assert.Equal("&bt", vm.Result!.Behavior);
        Assert.True(new[] { "BT_SEL", "3" }.SequenceEqual(vm.Result.Params));
        Assert.Equal("&bt BT_SEL 3", vm.PreviewText);
        Assert.Equal("", vm.ErrorText);
        Assert.Contains(command.SelectedCommand.Name, vm.Tabs[1].Header);
    }

    [Fact]
    public void SwitchingToCapsWord_GivesAParameterlessResult_OnTabZero()
    {
        var vm = Dialog(0);
        vm.SelectedBehavior = Choice(vm, "&caps_word");

        Assert.Equal("&caps_word", vm.Result!.Behavior);
        Assert.Empty(vm.Result.Params);
        Assert.True(vm.CanConfirm);
        Assert.Equal(0, vm.SelectedTabIndex);
    }

    // --- Custom text ------------------------------------------------------------

    [Fact]
    public void CustomText_ValidParses_InvalidShowsError()
    {
        var vm = Dialog(0);
        vm.SelectedBehavior = Choice(vm, "");

        // Switching to Custom carries the preview over as text.
        Assert.Equal("&kp N1", vm.CustomText);

        vm.CustomText = "&out OUT_USB";
        Assert.Equal("&out", vm.Result!.Behavior);
        Assert.True(new[] { "OUT_USB" }.SequenceEqual(vm.Result.Params));
        Assert.Equal("", vm.ErrorText);

        vm.CustomText = "&kp";
        Assert.Null(vm.Result);
        Assert.Equal(Loc.Instance["KeyDialog_CustomInvalid"], vm.ErrorText);
    }

    // --- Tabs and lists -----------------------------------------------------------

    [Fact]
    public void TabSteps_StopAtBothEnds()
    {
        var vm = Dialog(2);
        Assert.Equal(2, vm.Params.Count);
        Assert.Equal(2, vm.SelectedTabIndex);

        vm.PreviousTab();
        vm.PreviousTab();
        vm.PreviousTab();
        Assert.Equal(0, vm.SelectedTabIndex);

        vm.NextTab();
        vm.NextTab();
        vm.NextTab();
        vm.NextTab();
        Assert.Equal(2, vm.SelectedTabIndex);
        Assert.Equal("Tap: N1", vm.Tabs[vm.SelectedTabIndex].Header);
    }

    [Fact]
    public void ParamEdit_UpdatesItsHeaderInPlace_AndStaysOnItsTab()
    {
        var vm = Dialog(0);
        var tabs = vm.Tabs;

        ((KeycodeParamViewModel)vm.Params[0]).Picker.SelectedKeycode = ZmkKeycodeCatalog.Find("B");

        Assert.Same(tabs, vm.Tabs);
        Assert.Equal(1, vm.SelectedTabIndex);
        Assert.Equal("Key: B", vm.Tabs[1].Header);
    }

    [Fact]
    public void SelectedTabIndex_IgnoresANegativeValue()
    {
        var vm = Dialog(2);

        vm.SelectedTabIndex = -1;

        Assert.Equal(2, vm.SelectedTabIndex);
    }

    [Fact]
    public void BehaviorSwitch_ReplacesTheTabs()
    {
        var vm = Dialog(0);
        var tabs = vm.Tabs;

        vm.SelectedBehavior = vm.AllOtherBehaviors.First(choice => choice.Code == "&mt");

        Assert.NotSame(tabs, vm.Tabs);
        Assert.Equal(new[] { "Behavior: &mt", "Hold", "Tap: N1" }, vm.Tabs.Select(tab => tab.Header));
    }

    [Fact]
    public void OneParamMacro_WrittenLikeARealKeymap_OpensOnItsParameter()
    {
        const string text = """
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
                    Base { bindings = < &m1 A >; };
                };
            };
            """;
        var config = ZmkKeymapLoader.LoadFromText(text, "test");
        var vm = new KeyDialogViewModel(new KeyDialogRequest(config, Preprocessor.CollectDefines(text), 0, 0));

        Assert.Equal("&m1", vm.SelectedBehavior.Code);
        Assert.Single(vm.Params);
        Assert.Equal("&m1", vm.Result?.Behavior);
        Assert.Equal(new[] { "A" }, vm.Result!.Params);
    }

    [Fact]
    public void BehaviorFilter_NarrowsTheOtherList()
    {
        var vm = Dialog(0);

        vm.BehaviorFilter = "tog";

        Assert.Equal(new[] { "&tog" }, vm.OtherBehaviors.Select(choice => choice.Code));
    }

    [Theory]
    [InlineData("&kp")]
    [InlineData("&trans")]
    [InlineData("&none")]
    public void BehaviorFilter_FindsTheQuickBehaviors(string code)
    {
        var vm = Dialog(0);

        vm.BehaviorFilter = code;

        Assert.Contains(code, vm.OtherBehaviors.Select(choice => choice.Code));
    }

    [Fact]
    public void SelectedOtherBehavior_TracksTheSelection_AndIgnoresNull()
    {
        var vm = Dialog(0);

        // The quick behaviors head the list too, so a &kp key shows &kp there.
        Assert.Equal("&kp", vm.SelectedOtherBehavior!.Code);

        vm.SelectedOtherBehavior = Choice(vm, "&mo");
        Assert.Equal("&mo", vm.SelectedBehavior.Code);

        vm.SelectedOtherBehavior = null;
        Assert.Equal("&mo", vm.SelectedBehavior.Code);
    }

    // --- Ctor validation ------------------------------------------------------------

    [Fact]
    public void Ctor_RejectsNullRequestAndOutOfRangeIndices()
    {
        Assert.Throws<ArgumentNullException>(() => new KeyDialogViewModel(null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new KeyDialogViewModel(new KeyDialogRequest(Config, Defines, 0, 8)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new KeyDialogViewModel(new KeyDialogRequest(Config, Defines, 2, 0)));
    }

    // --- helpers ----------------------------------------------------------------------

    private static KeyDialogViewModel Dialog(int keyIndex, int layerIndex = 0) =>
        new(new KeyDialogRequest(Config, Defines, layerIndex, keyIndex));

    private static KeyDialogViewModel CommandDialog(int keyIndex) =>
        new(new KeyDialogRequest(CommandConfig, CommandDefines, 0, keyIndex));

    private static KeyBehaviorChoice Choice(KeyDialogViewModel vm, string code) =>
        vm.QuickBehaviors.Concat(vm.AllOtherBehaviors).First(choice => choice.Code == code);

    private static KeycodePickerViewModel Picker(KeyDialogViewModel vm, int paramIndex) =>
        ((KeycodeParamViewModel)vm.Params[paramIndex]).Picker;
}
