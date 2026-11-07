using LViz.App.Localization;
using LViz.App.ViewModels;
using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using LViz.Tests.Keymap;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The key dialog's building blocks: the behavior list in its fixed order
/// with translated names, the behavior filter, the per-kind parameter
/// editors (keycode, layer, raw, command) and the parameter captions.
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

    // The catalog behaviors in the fixed list after the plain layer behaviors.
    private static readonly string[] NewCodes =
    [
        "&bt", "&out", "&rgb_ug", "&mkp", "&msc", "&mmv",
        "&caps_word", "&key_repeat", "&bootloader", "&sys_reset", "&soft_off", "&studio_unlock",
    ];

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
            new[]
            {
                "&kp", "&trans", "&none",
                "&mt", "&lt", "&hm", "&m1", "&to", "&mo", "&tog", "&sl", "&sk",
                "&bt", "&out", "&rgb_ug", "&mkp", "&msc", "&mmv",
                "&caps_word", "&key_repeat", "&bootloader", "&sys_reset", "&soft_off", "&studio_unlock",
                "",
            },
            others.Select(c => c.Code));
        Assert.True(others[^1].IsCustom);
        Assert.All(others.Take(others.Count - 1), c => Assert.False(c.IsCustom));
    }

    [Fact]
    public void Others_NullConfig_NoOwnBehaviors()
    {
        var others = KeyBehaviorChoices.Others(null);

        Assert.Equal(
            new[]
            {
                "&kp", "&trans", "&none",
                "&mt", "&lt", "&to", "&mo", "&tog", "&sl", "&sk",
                "&bt", "&out", "&rgb_ug", "&mkp", "&msc", "&mmv",
                "&caps_word", "&key_repeat", "&bootloader", "&sys_reset", "&soft_off", "&studio_unlock",
                "",
            },
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
    public void CommandStrings_EveryBundledCommandAndFixedKeyResolves()
    {
        foreach (var code in ZmkCommandCatalog.Behaviors)
        {
            foreach (var command in ZmkCommandCatalog.Bundled(code))
            {
                Assert.DoesNotContain("[", Loc.Instance[$"Command_{command.Name}_Name"]);
                Assert.DoesNotContain("[", Loc.Instance[$"Command_{command.Name}_Help"]);
            }
        }

        foreach (var key in new[]
                 {
                     "KeyDialog_ParamCommand",
                     "KeyDialog_ParamProfile",
                     "KeyDialog_CommandProfileFormat",
                     "Command_FromKeymap_Help",
                 })
        {
            Assert.DoesNotContain("[", Loc.Instance[key]);
        }
    }

    [Fact]
    public void NewChoices_BehaviorIsTheCatalogEntry()
    {
        var others = KeyBehaviorChoices.Others(null);

        foreach (var code in NewCodes)
        {
            var choice = others.Single(c => c.Code == code);
            Assert.NotNull(choice.Behavior);
            Assert.Same(ZmkBehaviorCatalog.Find(null, code), choice.Behavior);
        }

        Assert.Equal(
            new[] { ZmkBehaviorParamKind.Command },
            others.Single(c => c.Code == "&bt").Behavior!.Params);
        Assert.Empty(others.Single(c => c.Code == "&bootloader").Behavior!.Params);
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

    [Fact]
    public void Matches_NewChoices_FilterOnCodeIsCaseInsensitive()
    {
        var all = All();
        var rgbUg = all.Single(c => c.Code == "&rgb_ug");
        var sysReset = all.Single(c => c.Code == "&sys_reset");
        var bt = all.Single(c => c.Code == "&bt");

        // Assert on the code only: the name is translated text and the test
        // culture may not be English.
        Assert.True(KeyBehaviorChoices.Matches(rgbUg, "RGB_UG"));
        Assert.True(KeyBehaviorChoices.Matches(sysReset, "sys_"));
        Assert.False(KeyBehaviorChoices.Matches(bt, "rgb"));
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

    [Fact]
    public void Labels_CommandParameter_IsNamedCommand()
    {
        Assert.Equal(
            new[] { Loc.Instance["KeyDialog_ParamCommand"] },
            KeyParamLabels.For(ZmkBehaviorCatalog.Find(null, "&bt")!, null));
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

    // --- Command parameter ------------------------------------------------------

    [Fact]
    public void CommandParam_EveryBundledCommand_NameAndHelpResolve()
    {
        foreach (var code in ZmkCommandCatalog.Behaviors)
        {
            var vm = new CommandParamViewModel("Command", code, null);

            foreach (var choice in vm.Commands)
            {
                // A missing resx key renders as "[Key]".
                Assert.DoesNotContain("[", choice.Name);
                Assert.DoesNotContain("[", choice.Help);
            }
        }
    }

    [Fact]
    public void CommandParam_BtNoConfig_CatalogOrder_ProfileButtons_NoValueYet()
    {
        var vm = new CommandParamViewModel("Command", "&bt", null);

        Assert.Equal(ZmkBehaviorParamKind.Command, vm.Kind);
        Assert.Equal("Command", vm.Label);
        Assert.Equal(Loc.Instance["KeyDialog_ParamProfile"], vm.ProfileLabel);
        Assert.Equal(
            ZmkCommandCatalog.Bundled("&bt").Select(c => c.Name),
            vm.Commands.Select(c => c.Command.Name));
        Assert.Equal(new[] { "0", "1", "2", "3", "4" }, vm.Profiles.Select(p => p.Text));
        Assert.Null(vm.Values);
        Assert.False(vm.HasValue);
        Assert.Equal("", vm.ValueText);
    }

    [Fact]
    public void CommandParam_Out_PickPlainCommand_WritesItsName()
    {
        var vm = new CommandParamViewModel("Command", "&out", null);
        var usb = vm.Commands.Single(c => c.Command.Name == "OUT_USB");

        vm.SelectedCommand = usb;

        Assert.Equal(new[] { "OUT_USB" }, vm.Values);
        Assert.True(vm.HasValue);
        Assert.Equal(usb.Name, vm.ValueText);
        Assert.False(vm.NeedsProfile);
    }

    [Fact]
    public void CommandParam_Bt_ProfileCompletesTheValue_AndSurvivesCommandChange()
    {
        var vm = new CommandParamViewModel("Command", "&bt", null);
        var sel = vm.Commands.Single(c => c.Command.Name == "BT_SEL");
        var nxt = vm.Commands.Single(c => c.Command.Name == "BT_NXT");

        vm.SelectedCommand = sel;
        Assert.True(vm.NeedsProfile);
        Assert.Null(vm.Values);

        vm.SelectedProfile = vm.Profiles.Single(p => p.Number == 2);
        Assert.Equal(new[] { "BT_SEL", "2" }, vm.Values);
        Assert.Equal(Loc.Instance.Format("KeyDialog_CommandProfileFormat", sel.Name, 2), vm.ValueText);

        vm.SelectedCommand = nxt;
        Assert.Equal(new[] { "BT_NXT" }, vm.Values);
        Assert.False(vm.NeedsProfile);

        vm.SelectedCommand = sel;
        Assert.Equal(new[] { "BT_SEL", "2" }, vm.Values);
    }

    [Fact]
    public void CommandParam_ChangedFiresOncePerPick()
    {
        var vm = new CommandParamViewModel("Command", "&bt", null);
        int changes = 0;
        vm.Changed += (_, _) => changes++;

        vm.SelectedCommand = vm.Commands.Single(c => c.Command.Name == "BT_SEL");
        Assert.Equal(1, changes);

        vm.SelectedProfile = vm.Profiles.Single(p => p.Number == 2);
        Assert.Equal(2, changes);

        // Re-picking the same command or profile raises nothing.
        vm.SelectedCommand = vm.Commands.Single(c => c.Command.Name == "BT_SEL");
        vm.SelectedProfile = vm.Profiles.Single(p => p.Number == 2);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void CommandParam_Seed_TakesArityAndNumber_AndAddsHighNumbers()
    {
        var vm = new CommandParamViewModel("Command", "&bt", null);

        Assert.True(vm.Seed(new[] { new[] { "BT_DISC" }, new[] { "3" } }));
        Assert.Equal("BT_DISC", vm.SelectedCommand!.Command.Name);
        Assert.Equal(3, vm.SelectedProfile!.Number);
        Assert.Equal(new[] { "BT_DISC", "3" }, vm.Values);

        Assert.True(vm.Seed(new[] { new[] { "BT_SEL" }, new[] { "7" } }));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 7 }, vm.Profiles.Select(p => p.Number));
        Assert.Equal(7, vm.SelectedProfile!.Number);
        Assert.Equal(new[] { "BT_SEL", "7" }, vm.Values);
    }

    [Fact]
    public void CommandParam_Seed_RejectsWrongShapeAndUnknownNames()
    {
        IReadOnlyList<IReadOnlyList<string>>[] rejected =
        [
            new[] { new[] { "BT_SEL" } }, // needs its profile number
            new[] { new[] { "BT_CLR" }, new[] { "0" } }, // command takes no number
            new[] { new[] { "NOPE" } }, // not a listed command
            new[] { new[] { "BT_SEL" }, new[] { "x" } }, // not a number
            new[] { new[] { "BT_SEL" }, new[] { "-1" } }, // negative number
            new[] { new[] { "BT_SEL" }, new[] { "00" } }, // not written back the same way
            new[] { new[] { "BT_DISC" }, new[] { "01" } }, // not written back the same way
            Array.Empty<IReadOnlyList<string>>(), // no groups
            new[] { new[] { "BT_SEL" }, new[] { "1" }, new[] { "2" } }, // three groups
            new[] { new[] { "LS", "A" } }, // a group of two tokens
        ];

        foreach (var groups in rejected)
        {
            var vm = new CommandParamViewModel("Command", "&bt", null);

            Assert.False(vm.Seed(groups));
            Assert.Null(vm.SelectedCommand);
            Assert.Null(vm.SelectedProfile);
            Assert.Null(vm.Values);
        }
    }

    [Fact]
    public void CommandParam_KeymapExtras_ShowTheirOwnName_AndSeed()
    {
        var config = ZmkKeymapLoader.LoadFromText(
            KeymapFixtures.Read("glove80-editor.keymap"), "glove80");
        var vm = new CommandParamViewModel("Command", "&rgb_ug", config);

        var extra = vm.Commands[^1];
        Assert.Equal("RGB_STATUS", extra.Command.Name);
        Assert.True(extra.Command.FromKeymap);
        Assert.Equal("RGB_STATUS", extra.Name);
        Assert.Equal(Loc.Instance["Command_FromKeymap_Help"], extra.Help);

        Assert.True(vm.Seed(new[] { new[] { "RGB_STATUS" } }));
        Assert.Equal("RGB_STATUS", vm.SelectedCommand!.Command.Name);
        Assert.Equal(new[] { "RGB_STATUS" }, vm.Values);
    }

    // --- helpers ------------------------------------------------------------

    private static List<KeyBehaviorChoice> All() =>
        [.. KeyBehaviorChoices.Quick(Config), .. KeyBehaviorChoices.Others(Config)];

    private static ZmkBehavior Behavior(string code) =>
        ZmkBehaviorCatalog.Find(Config, code)!;
}
