using LViz.App.Localization;
using LViz.App.ViewModels;
using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using LViz.Tests.Keymap;
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

    // --- Opening on a carried binding (a combo's) -------------------

    [Fact]
    public void CarriedKpBinding_EditsIt_AndCaptionIsTheTitle()
    {
        var vm = CarriedDialog(new KeyBinding("&kp", ["ESC"]), "Combo combo_esc");

        Assert.Equal("&kp", vm.SelectedBehavior.Code);
        // "ESC" is the alias of the catalog's ESCAPE entry — the picker
        // resolves, and Result keeps the binding's own spelling.
        Assert.Same(ZmkKeycodeCatalog.Find("ESC"), Picker(vm, 0).SelectedKeycode);
        Assert.Equal("&kp", vm.Result!.Behavior);
        Assert.Equal(new[] { "ESC" }, vm.Result.Params);
        Assert.Equal("Combo combo_esc", vm.TitleText);
    }

    [Fact]
    public void CarriedNone_WithoutCaption_ResultsNone_WithLayerTitle()
    {
        var vm = CarriedDialog(KeyBinding.None);

        Assert.Equal("&none", vm.Result!.Behavior);
        Assert.Empty(vm.Result.Params);
        Assert.Equal(Loc.Instance.Format("KeyDialog_TitleCustomFormat", "Base"), vm.TitleText);
    }

    [Fact]
    public void CarriedMoBinding_SeedsTheLayerEditorOnLayerOne()
    {
        var vm = CarriedDialog(new KeyBinding("&mo", ["1"]));

        var layer = Assert.IsType<LayerParamViewModel>(vm.Params[0]);
        Assert.Equal(1, layer.SelectedLayer!.Index);
    }

    [Fact]
    public void CarriedBinding_StillRejectsOutOfRangeLayer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeyDialogViewModel(
            new KeyDialogRequest(Config, Defines, 2, -1) { Binding = KeyBinding.None }));
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

    // --- Ignored params and the pinned layer (glove80 / go60 fixtures) ------

    [Fact]
    public void MagicKey_OpensWithOneLayerEditor_AndPreviewsThroughTheLayerDefine()
    {
        var vm = GloveDialog(64);   // &magic LAYER_Magic 0

        Assert.Equal("&magic", vm.SelectedBehavior.Code);
        var layer = Assert.IsType<LayerParamViewModel>(Assert.Single(vm.Params));
        Assert.Equal(ZmkBehaviorParamKind.Layer, layer.Kind);
        // The editor of original position 0 carries that position's label.
        Assert.Equal(Loc.Instance["KeyDialog_ParamHold"], layer.Label);
        Assert.Equal(3, layer.SelectedLayer!.Index);

        Assert.Equal("&magic", vm.Result!.Behavior);
        Assert.Equal(new[] { "3", "0" }, vm.Result.Params);
        Assert.Equal("&magic LAYER_Magic 0", vm.PreviewText);
    }

    [Fact]
    public void MagicKey_ConfirmedUnchanged_GivesBackExactlyTheOldBinding()
    {
        var original = GloveConfig.Layers[0].Bindings[64];
        var vm = GloveDialog(64);

        Assert.Equal(original.Behavior, vm.Result!.Behavior);
        Assert.Equal(original.Params, vm.Result.Params);
    }

    [Fact]
    public void PickingMagic_LandsOnTheBehaviorTab_WithThePinnedLayerFilledIn()
    {
        var vm = GloveDialog(0);    // &mt F11 F1

        // Pick a layer on another behavior first: the switch carries it over,
        // and the pinned layer still has to win.
        vm.SelectedBehavior = Choice(vm, "&lt");
        var carry = Assert.IsType<LayerParamViewModel>(vm.Params[0]);
        carry.SelectedLayer = carry.Layers.First(choice => choice.Index == 2);

        vm.SelectedBehavior = Choice(vm, "&magic");

        Assert.Single(vm.Params);
        Assert.Equal(0, vm.SelectedTabIndex);
        Assert.True(vm.CanConfirm);
        Assert.Equal("&magic", vm.Result!.Behavior);
        Assert.Equal(new[] { "3", "0" }, vm.Result.Params);
    }

    [Fact]
    public void PickingMagic_ThenAnotherLayer_WritesThatLayer_AndTheZeroCell()
    {
        var vm = GloveDialog(0);
        vm.SelectedBehavior = Choice(vm, "&magic");
        var layer = Assert.IsType<LayerParamViewModel>(vm.Params[0]);

        layer.SelectedLayer = layer.Layers.First(choice => choice.Index == 1);

        Assert.Equal("&magic", vm.Result!.Behavior);
        Assert.Equal(new[] { "1", "0" }, vm.Result.Params);
    }

    [Fact]
    public void Go60_MagicKey_PreviewsThroughTheLayerDefine_AndAMoKeyNeverNamesAPosition()
    {
        var vm = Go60Dialog(47);    // &magic LAYER_Magic 0
        Assert.Equal("&magic LAYER_Magic 0", vm.PreviewText);

        // Layer 7 carries over to a plain &mo. Formatting with every define
        // would name it POS_RH_C2R1 (same value, defined earlier); a layer
        // number may only be spelled with a define the file uses as a layer
        // reference, which for 7 is LAYER_Magic.
        vm.SelectedBehavior = Choice(vm, "&mo");
        Assert.Equal(7, Assert.IsType<LayerParamViewModel>(Assert.Single(vm.Params)).SelectedLayer!.Index);
        Assert.Equal("&mo LAYER_Magic", vm.PreviewText);
        Assert.DoesNotContain("POS_", vm.PreviewText);
    }

    // An inline &magic: hold &mo (a layer param), tap a 0-cell macro (an
    // Ignored cell). Four layers so "&magic 3 5" addresses an existing one.
    private const string MagicKeymap = """
        / {
            macros {
                m0: m0 {
                    compatible = "zmk,behavior-macro";
                    #binding-cells = <0>;
                    bindings = <&kp A>;
                };
            };

            behaviors {
                magic: magic {
                    compatible = "zmk,behavior-hold-tap";
                    #binding-cells = <2>;
                    bindings = <&mo>, <&m0>;
                };
            };

            keymap {
                compatible = "zmk,keymap";

                Base { bindings = < &magic 3 5 >; } ;
                L1 { bindings = < &trans >; } ;
                L2 { bindings = < &trans >; } ;
                L3 { bindings = < &trans >; } ;
            };
        };
        """;

    [Fact]
    public void MagicBinding_Unchanged_IgnoredCellKeepsTheBindingsValue()
    {
        var config = ZmkKeymapLoader.LoadFromText(MagicKeymap, "test");
        var vm = new KeyDialogViewModel(
            new KeyDialogRequest(config, Preprocessor.CollectDefines(MagicKeymap), 0, 0));

        Assert.Equal("&magic", vm.SelectedBehavior.Code);
        var layer = Assert.IsType<LayerParamViewModel>(Assert.Single(vm.Params));
        Assert.Equal(3, layer.SelectedLayer!.Index);
        Assert.Equal(new[] { "3", "5" }, vm.Result!.Params);
    }

    // A [Ignored, Keycode] hold-tap: a 0-cell macro holds, &kp taps — the
    // hold cell has no editor and a fresh pick writes 0 into it.
    private const string TapOnlyKeymap = """
        / {
            macros {
                m0: m0 {
                    compatible = "zmk,behavior-macro";
                    #binding-cells = <0>;
                    bindings = <&kp A>;
                };
            };

            behaviors {
                hm: hm {
                    compatible = "zmk,behavior-hold-tap";
                    #binding-cells = <2>;
                    bindings = <&m0>, <&kp>;
                };
            };

            keymap {
                compatible = "zmk,keymap";

                Base { bindings = < &trans >; } ;
            };
        };
        """;

    [Fact]
    public void IgnoredHoldSide_EditsOnlyTheTap_AndFreshPickWritesZero()
    {
        var config = ZmkKeymapLoader.LoadFromText(TapOnlyKeymap, "test");
        var vm = new KeyDialogViewModel(
            new KeyDialogRequest(config, Preprocessor.CollectDefines(TapOnlyKeymap), 0, 0));

        vm.SelectedBehavior = Choice(vm, "&hm");

        var tap = Assert.IsType<KeycodeParamViewModel>(Assert.Single(vm.Params));
        Assert.Equal(Loc.Instance["KeyDialog_ParamTap"], tap.Label);
        Assert.Equal(1, vm.SelectedTabIndex);  // the tap still needs a value

        tap.Picker.SelectedKeycode = ZmkKeycodeCatalog.Find("A")!;

        Assert.Equal("&hm", vm.Result!.Behavior);
        Assert.Equal(new[] { "0", "A" }, vm.Result.Params);
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

    // The shipped fixtures, with both define lists the dialog takes: every
    // define for parsing, and the file's layer-reference defines for display.
    private static readonly string GloveSource = KeymapFixtures.Read("glove80.keymap");

    private static readonly KeyboardConfig GloveConfig =
        ZmkKeymapLoader.LoadFromText(GloveSource, "glove80");

    private static readonly IReadOnlyList<KeyValuePair<string, string>> GloveDefines =
        Preprocessor.CollectDefines(GloveSource);

    private static readonly IReadOnlyList<KeyValuePair<string, string>> GloveLayerDefines =
        KeymapWriter.LayerReferenceDefines(GloveSource, GloveConfig);

    private static readonly string Go60Source = KeymapFixtures.Read("go60.keymap");

    private static readonly KeyboardConfig Go60Config =
        ZmkKeymapLoader.LoadFromText(Go60Source, "go60");

    private static readonly IReadOnlyList<KeyValuePair<string, string>> Go60Defines =
        Preprocessor.CollectDefines(Go60Source);

    private static readonly IReadOnlyList<KeyValuePair<string, string>> Go60LayerDefines =
        KeymapWriter.LayerReferenceDefines(Go60Source, Go60Config);

    private static KeyDialogViewModel GloveDialog(int keyIndex, int layerIndex = 0) =>
        new(new KeyDialogRequest(GloveConfig, GloveDefines, layerIndex, keyIndex)
        {
            LayerDefines = GloveLayerDefines,
        });

    private static KeyDialogViewModel Go60Dialog(int keyIndex, int layerIndex = 0) =>
        new(new KeyDialogRequest(Go60Config, Go60Defines, layerIndex, keyIndex)
        {
            LayerDefines = Go60LayerDefines,
        });

    private static KeyDialogViewModel Dialog(int keyIndex, int layerIndex = 0) =>
        new(new KeyDialogRequest(Config, Defines, layerIndex, keyIndex));

    private static KeyDialogViewModel CommandDialog(int keyIndex) =>
        new(new KeyDialogRequest(CommandConfig, CommandDefines, 0, keyIndex));

    // A carried binding on layer 0: KeyIndex -1 must be accepted once a
    // binding rides along.
    private static KeyDialogViewModel CarriedDialog(KeyBinding binding, string? caption = null) =>
        new(new KeyDialogRequest(Config, Defines, 0, -1)
        {
            Binding = binding,
            Caption = caption,
        });

    private static KeyBehaviorChoice Choice(KeyDialogViewModel vm, string code) =>
        vm.QuickBehaviors.Concat(vm.AllOtherBehaviors).First(choice => choice.Code == code);

    private static KeycodePickerViewModel Picker(KeyDialogViewModel vm, int paramIndex) =>
        ((KeycodeParamViewModel)vm.Params[paramIndex]).Picker;
}
