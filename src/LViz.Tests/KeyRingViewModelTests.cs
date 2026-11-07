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
/// The radial key editor's view-model over the key dialog: which step it is on
/// (summary, category ring, parameter step, free text), the category ring and
/// its behavior bands, applying a choice, crumbs and back, the text step, and
/// what it hands back to write.
/// </summary>
public class KeyRingViewModelTests
{
    // Base(0)/Nav(1) of ten keys, #define NAV 1, one own hold-tap, and on Base
    // &kp A, &lt NAV ESC, &trans, &none, &mo NAV, &bt BT_SEL 0, &hm LSHFT A,
    // a nested rgb_ug parameter the editors cannot show, &kp LC(LS(T)) and
    // &mt LSHFT F1.
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

                Base { bindings = < &kp A &lt NAV ESC &trans &none &mo NAV &bt BT_SEL 0 &hm LSHFT A &rgb_ug RGB_COLOR_HSB(0,100,50) &kp LC(LS(T)) &mt LSHFT F1 >; };

                Nav { bindings = < &trans &trans &trans &trans &trans &trans &trans &trans &trans &trans >; };
            };
        };
        """;

    private static readonly KeyboardConfig Config = ZmkKeymapLoader.LoadFromText(Keymap, "test");

    private static readonly IReadOnlyList<KeyValuePair<string, string>> Defines =
        Preprocessor.CollectDefines(Keymap);

    // Six own hold-taps: the keymap category then holds seven entries, more
    // than a band fits.
    private const string WideKeymap = """
        / {
            behaviors {
                m1: m1 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
                m2: m2 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
                m3: m3 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
                m4: m4 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
                m5: m5 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
                m6: m6 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
            };

            keymap {
                compatible = "zmk,keymap";

                Base { bindings = < &trans &trans &trans >; };
            };
        };
        """;

    private static readonly KeyboardConfig WideConfig =
        ZmkKeymapLoader.LoadFromText(WideKeymap, "test");

    // One one-param macro: its parameter is a raw token, edited as text.
    private const string MacroKeymap = """
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

                Base { bindings = < &m1 ABC >; };
            };
        };
        """;

    private static readonly KeyboardConfig MacroConfig =
        ZmkKeymapLoader.LoadFromText(MacroKeymap, "test");

    // --- The step model -----------------------------------------------------

    [Fact]
    public void ExistingBinding_OpensOnTheSummaryOfCategories()
    {
        var ring = Ring(0);

        Assert.True(ring.IsSummary);
        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);

        var expected = Enum.GetValues<ZmkBehaviorGroup>()
            .Select(group => $"cat:{group}")
            .ToArray();
        Assert.Equal(expected, ring.Segments.Select(segment => segment.Id));

        var key = Segment(ring.Segments, "cat:Key");
        Assert.True(key.IsCurrent);
        Assert.Equal("fa-solid fa-keyboard", key.Icon);
        Assert.Equal(Loc.Instance["Ring_Category_Key"], key.Label);
        Assert.False(key.LabelIsCode);
        Assert.True(key.SubIsCode);

        Assert.Equal(Loc.Instance["Ring_Category_KeymapSub"], Segment(ring.Segments, "cat:Keymap").Sub);
        Assert.False(Segment(ring.Segments, "cat:Keymap").SubIsCode);
        Assert.Empty(ring.InnerSegments);
    }

    [Fact]
    public void CategorySubs_ShowTheFirstTwoCodes_AndEllipsisWhenWider()
    {
        var ring = Ring(0);

        Assert.Equal("&kp &sk …", Segment(ring.Segments, "cat:Key").Sub);
        Assert.Equal("&mt &lt", Segment(ring.Segments, "cat:HoldTap").Sub);
        Assert.Equal("&mo &to …", Segment(ring.Segments, "cat:Layer").Sub);
        Assert.Equal("&bt", Segment(ring.Segments, "cat:Bluetooth").Sub);
        Assert.Equal("&trans &none", Segment(ring.Segments, "cat:Clear").Sub);
    }

    [Theory]
    // &trans, &none
    [InlineData(2)]
    [InlineData(3)]
    public void ClearBinding_OpensOnTheCategoryRing(int keyIndex)
    {
        var ring = Ring(keyIndex);

        Assert.False(ring.IsSummary);
        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);
        Assert.True(Segment(ring.Segments, "cat:Clear").IsCurrent);
    }

    [Fact]
    public void BindingTheEditorsCannotShow_OpensOnTheTextStep()
    {
        var ring = Ring(7);

        Assert.False(ring.IsSummary);
        Assert.Equal(KeyRingStepKind.Text, ring.StepKind);
        Assert.Contains("RGB_COLOR_HSB", ring.Text);
        Assert.Equal(ring.Dialog.ErrorText, ring.TextError);
    }

    [Theory]
    // Every binding on Base: &kp A, &lt NAV ESC, &trans, &none, &mo NAV,
    // &bt BT_SEL 0, &hm LSHFT A, the nested rgb, &kp LC(LS(T)), &mt LSHFT F1.
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void OpenedUnchanged_ThereIsNothingToWrite(int keyIndex)
    {
        Assert.Null(Ring(keyIndex).CloseResult);
    }

    [Fact]
    public void LayerCommandAndKeycodeParams_DriveTheStepKind()
    {
        // &mo NAV -> the layer step; &bt BT_SEL 0 -> the command step;
        // &kp LC(LS(T)) -> the keycode step. Each opens on the summary, so
        // crumb 2 is what puts the ring on its only parameter.
        var layer = Ring(4);
        layer.GoToCrumb(2);
        Assert.Equal(KeyRingStepKind.Layer, layer.StepKind);

        // Off the behavior step the category ring is gone: the layer step
        // shows its own ring, the layers.
        Assert.DoesNotContain(layer.Segments, segment => segment.Id.StartsWith("cat:"));
        Assert.Equal(new[] { "layer:0", "layer:1" }, layer.Segments.Select(segment => segment.Id));

        var command = Ring(5);
        command.GoToCrumb(2);
        Assert.Equal(KeyRingStepKind.Command, command.StepKind);

        var keycode = Ring(8);
        keycode.GoToCrumb(2);
        Assert.Equal(KeyRingStepKind.Keycode, keycode.StepKind);
    }

    // --- Behavior bands -----------------------------------------------------

    [Fact]
    public void BandFor_ListsTheCategorysBehaviorsInBandOrder()
    {
        var ring = Ring(0);

        Assert.Equal(
            new[] { "beh:&mo", "beh:&to", "beh:&tog", "beh:&sl" },
            ring.BandFor("cat:Layer").Select(segment => segment.Id));
        Assert.Equal(
            Loc.Instance["Ring_Short_Mo"],
            ring.BandFor("cat:Layer").First(segment => segment.Id == "beh:&mo").Sub);

        var keyBand = ring.BandFor("cat:Key");
        Assert.Equal(
            new[] { "beh:&kp", "beh:&sk", "beh:&caps_word", "beh:&key_repeat" },
            keyBand.Select(segment => segment.Id));
        Assert.Equal(
            new[] { true, false, false, false },
            keyBand.Select(segment => segment.IsCurrent));
        Assert.All(keyBand, segment => Assert.True(segment.LabelIsCode));
        Assert.All(keyBand, segment => Assert.True(segment.IsEnabled));
    }

    [Fact]
    public void BandFor_SingleBehaviorAndUnknownCategories_HaveNoBand()
    {
        var ring = Ring(0);

        Assert.Empty(ring.BandFor("cat:Bluetooth"));
        Assert.Empty(ring.BandFor("nope"));
        Assert.Empty(ring.BandFor("cat:0"));
        Assert.Empty(ring.BandFor("beh:&kp"));
    }

    [Fact]
    public void BandFor_KeymapCategory_ListsOwnBehaviorsThenCustom()
    {
        var ring = Ring(0);

        Assert.Equal(
            new[] { "beh:&hm", "beh:custom" },
            ring.BandFor("cat:Keymap").Select(segment => segment.Id));
        Assert.Equal(Loc.Instance["Ring_Short_Own"], Segment(ring.BandFor("cat:Keymap"), "beh:&hm").Sub);

        var custom = Segment(ring.BandFor("cat:Keymap"), "beh:custom");
        Assert.Equal(Loc.Instance["Ring_Custom_Name"], custom.Label);
        Assert.False(custom.LabelIsCode);
        Assert.Equal(Loc.Instance["Ring_Short_Custom"], custom.Sub);
    }

    [Fact]
    public void BandFor_CurrentBehavior_IsMarkedCurrent()
    {
        var ring = Ring(7);

        Assert.True(Segment(ring.BandFor("cat:Keymap"), "beh:custom").IsCurrent);

        // Custom counts as the keymap's own category; crumb 0 shows the ring
        // with the text step hidden.
        ring.GoToCrumb(0);
        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);
        Assert.True(Segment(ring.Segments, "cat:Keymap").IsCurrent);
    }

    [Fact]
    public void BandFor_MoreBehaviorsThanABandFits_IsLeftToTheListPanel()
    {
        var ring = Ring(WideConfig, Preprocessor.CollectDefines(WideKeymap), 0);

        // Six own hold-taps plus Custom: too wide for the band, so the list
        // panel takes it.
        var own = ring.Dialog.AllOtherBehaviors
            .Count(choice => choice.Behavior?.Group == ZmkBehaviorGroup.Keymap);
        Assert.True(own >= KeyRingViewModel.MaxBandItems);

        Assert.Empty(ring.BandFor("cat:Keymap"));
        Assert.True(ring.Choose("cat:Keymap"));
        Assert.Equal(own + 1, ring.Panel!.Rows.Count);
        Assert.Equal("beh:custom", ring.Panel.Rows[^1].Id);
    }

    // --- Choosing -----------------------------------------------------------

    [Fact]
    public void Choose_AnotherBehavior_LandsOnItsMissingParam()
    {
        var ring = Ring(0);

        Assert.True(ring.Choose("beh:&mo"));

        Assert.Equal("&mo", ring.Dialog.SelectedBehavior.Code);
        Assert.Equal(KeyRingStepKind.Layer, ring.StepKind);
        Assert.False(ring.IsSummary);
        Assert.False(ring.CanClose);
        Assert.Equal(new[] { "layer:0", "layer:1" }, ring.Segments.Select(segment => segment.Id));
    }

    [Fact]
    public void Choose_AParameterlessBehavior_StaysOnTheBehaviorRing()
    {
        var ring = Ring(0);

        Assert.True(ring.Choose("beh:&trans"));

        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);
        Assert.False(ring.IsSummary);
        Assert.Equal("&trans", ring.CloseResult!.Behavior);
    }

    [Fact]
    public void Choose_SingleBehaviorCategory_ChosesThatBehavior()
    {
        var ring = Ring(0);

        Assert.True(ring.Choose("cat:Bluetooth"));
        Assert.Equal("&bt", ring.Dialog.SelectedBehavior.Code);
        Assert.Equal(KeyRingStepKind.Command, ring.StepKind);
    }

    [Fact]
    public void Choose_WiderCategory_IsLeftToTheBand()
    {
        var ring = Ring(0);

        Assert.False(ring.Choose("cat:Layer"));
        Assert.True(ring.IsSummary);
        Assert.Equal("&kp", ring.Dialog.SelectedBehavior.Code);
        Assert.Equal(1, ring.Dialog.SelectedTabIndex);
        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);
    }

    [Fact]
    public void Choose_Custom_OpensTheTextStepAndWritesWhatIsTyped()
    {
        var ring = Ring(0);

        Assert.True(ring.Choose("beh:custom"));
        Assert.Equal(KeyRingStepKind.Text, ring.StepKind);
        Assert.True(ring.Dialog.IsCustom);

        ring.Text = "&kp B";

        Assert.Equal("&kp B", ring.Text);
        Assert.Equal("&kp", ring.CloseResult!.Behavior);
        Assert.Equal(new[] { "B" }, ring.CloseResult.Params);
    }

    [Fact]
    public void Choose_TheSameBehavior_StepsOnToTheParams()
    {
        var ring = Ring(0);

        Assert.True(ring.Choose("beh:&kp"));

        Assert.Equal(1, ring.Dialog.SelectedTabIndex);
        Assert.Equal(KeyRingStepKind.Keycode, ring.StepKind);
        Assert.False(ring.IsSummary);
    }

    [Fact]
    public void Choose_CarryOverKeepsWhatFits_AndStopsAtTheMissingParam()
    {
        // &lt NAV ESC -> &mt: ESC is carried to the tap key, the hold key is
        // still missing, so the ring lands on parameter 1.
        var ring = Ring(1);

        Assert.True(ring.Choose("beh:&mt"));

        Assert.Equal(1, ring.Dialog.SelectedTabIndex);
        Assert.Equal(KeyRingStepKind.Keycode, ring.StepKind);
    }

    [Fact]
    public void Choose_UnknownId_DoesNothing()
    {
        var ring = Ring(0);
        var raised = 0;
        ring.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == string.Empty) raised++;
        };

        Assert.False(ring.Choose("zzz"));
        Assert.False(ring.Choose("beh:"));
        Assert.False(ring.Choose("cat:Nope"));
        Assert.True(ring.IsSummary);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Choose_RaisesTheEverythingChangedNotification()
    {
        var ring = Ring(0);
        var raised = 0;
        ring.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == string.Empty) raised++;
        };

        Assert.True(ring.Choose("beh:&mo"));
        Assert.True(raised >= 1);
    }

    // --- One-pick behavior on the shipped fixture ----------------------------

    /// <summary>glove80's <c>&amp;magic</c> holds a layer and taps a 0-cell
    /// macro: hiding the Ignored cell and prefilling the layer every use
    /// names makes picking it on the behavior ring a single click that
    /// already offers to save.</summary>
    [Fact]
    public void Glove80_ChoosingMagic_IsOnePick_AndClosesWithThePinnedLayer()
    {
        var request = new KeyDialogRequest(GloveConfig, GloveDefines, 0, 0)   // &mt F11 F1
        {
            LayerDefines = GloveLayerDefines,
        };
        var ring = new KeyRingViewModel(new KeyDialogViewModel(request), request);

        Assert.True(ring.Choose("beh:&magic"));

        Assert.True(ring.CanClose);
        Assert.Equal("&magic", ring.CloseResult!.Behavior);
        Assert.Equal(new[] { "3", "0" }, ring.CloseResult.Params);
        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);
    }

    // --- Crumbs and back ----------------------------------------------------

    [Fact]
    public void GoToCrumb_And_Back_StepThroughTheParams()
    {
        var ring = Ring(1);

        ring.GoToCrumb(2);
        Assert.False(ring.IsSummary);
        Assert.Equal(KeyRingStepKind.Layer, ring.StepKind);

        ring.Back();
        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);

        ring.Back();
        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);
    }

    [Fact]
    public void GoToCrumb_FirstCrumbs_LeaveTheSummaryForTheBehaviorStep()
    {
        var ring = Ring(0);

        ring.GoToCrumb(0);
        Assert.False(ring.IsSummary);
        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);

        var custom = Ring(7);
        custom.GoToCrumb(1);
        Assert.False(custom.IsSummary);
        Assert.Equal(KeyRingStepKind.Behavior, custom.StepKind);
    }

    [Fact]
    public void GoToCrumb_PastTheLastParam_IsIgnored()
    {
        var ring = Ring(1);

        ring.GoToCrumb(9);

        Assert.True(ring.IsSummary);
        Assert.Equal(2, ring.Dialog.SelectedTabIndex);
    }

    [Fact]
    public void Back_OnTheSummary_DoesNothing()
    {
        var ring = Ring(1);

        ring.Back();

        Assert.True(ring.IsSummary);
        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);
    }

    [Fact]
    public void Back_FromTheCustomText_ReturnsToTheCategoryRing()
    {
        var ring = Ring(7);

        ring.Back();

        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);
        Assert.True(ring.Segments.Count > 0);
    }

    [Fact]
    public void Text_OnAStepWithoutOne_IsEmptyAndIgnored()
    {
        var ring = Ring(0);

        Assert.Equal("", ring.Text);

        ring.Text = "&kp Z";

        Assert.Equal("", ring.Text);
        Assert.Equal("&kp", ring.Dialog.SelectedBehavior.Code);
    }

    [Fact]
    public void Text_OnARawParamStep_EditsThatParam()
    {
        // &m1 ABC: the macro's one parameter is a raw token.
        var ring = Ring(MacroConfig, Preprocessor.CollectDefines(MacroKeymap), 0);
        ring.GoToCrumb(2);

        Assert.Equal(KeyRingStepKind.Text, ring.StepKind);
        Assert.Equal("ABC", ring.Text);

        ring.Text = "DEF";

        Assert.Equal("DEF", ring.Text);
        Assert.Equal("&m1", ring.CloseResult!.Behavior);
        Assert.Equal(new[] { "DEF" }, ring.CloseResult.Params);
    }

    // --- Centre, crumbs and hint --------------------------------------------

    [Fact]
    public void UnchangedSummary_DescribesTheBindingWithDoneCrumbs()
    {
        var ring = Ring(0);

        Assert.Equal(KeyRingCenterState.Unchanged, ring.CenterState);
        Assert.Equal("&kp A", ring.CenterBinding);
        Assert.Equal("", ring.CenterBefore);
        Assert.Equal(
            Loc.Instance.Format("Ring_DescriptionItemFormat", Loc.Instance["KeyDialog_ParamKey"], "A"),
            ring.CenterLine);
        Assert.Equal(Loc.Instance["Ring_Hint_Summary"], ring.Hint);

        var crumbs = ring.Crumbs;
        Assert.Equal(
            new[]
            {
                Loc.Instance["Ring_Category_Key"],
                "&kp",
                Loc.Instance.Format("Ring_DescriptionItemFormat", Loc.Instance["KeyDialog_ParamKey"], "A"),
            },
            crumbs.Select(crumb => crumb.Text));
        Assert.All(crumbs, crumb => Assert.Equal(KeyRingCrumbState.Done, crumb.State));
        Assert.All(crumbs, crumb => Assert.False(crumb.IsChanged));
        Assert.False(crumbs[0].IsCode);
        Assert.True(crumbs[1].IsCode);
        Assert.False(crumbs[2].IsCode);
    }

    [Fact]
    public void CenterCaption_NamesTheKeyAndItsLayer()
    {
        Assert.Equal(
            Loc.Instance.Format("Ring_CaptionFormat", 0, "Base"),
            Ring(0).CenterCaption);
    }

    // --- Editing a binding that isn't on a key (part 14) --------------------

    [Fact]
    public void CarriedBinding_CaptionReplacesCenter_AndChoiceWritesThrough()
    {
        var request = new KeyDialogRequest(Config, Defines, 0, -1)
        {
            Binding = new KeyBinding("&kp", ["ESC"]),
            Caption = "Combo combo_esc",
        };
        var ring = new KeyRingViewModel(new KeyDialogViewModel(request), request);

        Assert.Equal("Combo combo_esc", ring.CenterCaption);
        Assert.True(ring.IsSummary);
        Assert.Equal(KeyRingCenterState.Unchanged, ring.CenterState);

        Assert.True(ring.Choose("beh:&trans"));

        Assert.Equal("&trans", ring.CloseResult!.Behavior);
        Assert.Empty(ring.CloseResult.Params);
    }

    [Fact]
    public void CarriedNone_OpensOnTheCategoryRing()
    {
        var request = new KeyDialogRequest(Config, Defines, 0, -1) { Binding = KeyBinding.None };
        var ring = new KeyRingViewModel(new KeyDialogViewModel(request), request);

        Assert.False(ring.IsSummary);
        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);
        Assert.True(Segment(ring.Segments, "cat:Clear").IsCurrent);
    }

    [Fact]
    public void UnchangedAwayFromTheSummary_PointsToChoose()
    {
        // &trans opens on the category ring: the binding is intact, but the
        // ring is on a step, so the centre is Choosing, not Unchanged.
        var ring = Ring(2);

        Assert.Equal(KeyRingCenterState.Choosing, ring.CenterState);
        Assert.Equal(Loc.Instance["Ring_Center_Choose"], ring.CenterLine);
        Assert.Equal(Loc.Instance["Ring_Hint_Choose"], ring.Hint);
        Assert.Equal(Loc.Instance["Ring_Category_Clear"], ring.Crumbs[0].Text);
        Assert.Equal(KeyRingCrumbState.Current, ring.Crumbs[0].State);
    }

    [Fact]
    public void Choose_BehaviorStillNeedingAValue_PromptsAndShowsTheGap()
    {
        var ring = Ring(0);
        ring.Choose("beh:&mo");

        Assert.Equal(KeyRingCenterState.Incomplete, ring.CenterState);
        Assert.Equal("&mo ___", ring.CenterBinding);
        Assert.Equal("&kp A", ring.CenterBefore);
        Assert.Equal(
            Loc.Instance.Format("Ring_Center_PickFormat", Loc.Instance["KeyDialog_ParamLayer"]),
            ring.CenterLine);

        var crumbs = ring.Crumbs;
        Assert.Equal("&mo", crumbs[1].Text);
        Assert.True(crumbs[1].IsChanged);
        Assert.Equal(KeyRingCrumbState.Current, crumbs[2].State);
        Assert.Equal(
            Loc.Instance.Format("Ring_CrumbOpenFormat", Loc.Instance["KeyDialog_ParamLayer"]),
            crumbs[2].Text);
    }

    [Fact]
    public void Choose_CompleteOtherBehavior_OffersToSave()
    {
        var ring = Ring(0);
        ring.Choose("beh:&trans");

        Assert.Equal(KeyRingCenterState.Changed, ring.CenterState);
        Assert.Equal(Loc.Instance["Ring_Center_Save"], ring.CenterLine);
        Assert.Equal("&trans", ring.CenterBinding);
    }

    [Fact]
    public void HoldTap_CrumbNamesTheLayer_AndCentreWritesBackTheDefine()
    {
        var ring = Ring(1);

        Assert.Equal(
            Loc.Instance.Format("Ring_DescriptionItemFormat", Loc.Instance["KeyDialog_ParamHold"], "Nav"),
            ring.Crumbs[2].Text);
        Assert.Equal("&lt NAV ESC", ring.CenterBinding);
    }

    [Fact]
    public void HoldTap_ChoosingModTap_DropsTheLayerAndMarksEveryParamChanged()
    {
        var ring = Ring(1);
        ring.Choose("beh:&mt");

        Assert.Equal("&mt ___ ESC", ring.CenterBinding);
        Assert.Equal(KeyRingCrumbState.Current, ring.Crumbs[2].State);
        Assert.Equal(KeyRingCrumbState.Done, ring.Crumbs[3].State);
        Assert.True(ring.Crumbs[3].IsChanged);
    }

    [Fact]
    public void CommandCrumb_JoinsCommandAndProfileNumber()
    {
        var ring = Ring(5);

        Assert.Equal(
            Loc.Instance.Format("Ring_DescriptionItemFormat", Loc.Instance["KeyDialog_ParamCommand"], "BT_SEL 0"),
            ring.Crumbs[2].Text);
    }

    [Fact]
    public void CustomText_CentreShowsTheErrorThenPromptsForTyping()
    {
        var ring = Ring(7);

        Assert.Equal(Loc.Instance["Ring_Hint_Text"], ring.Hint);
        Assert.Equal(KeyRingCrumbState.Current, ring.Crumbs[1].State);

        // "&kp" alone: too few params, so the centre shows why.
        ring.Text = "&kp";
        Assert.Equal(KeyRingCenterState.Incomplete, ring.CenterState);
        Assert.Equal(ring.Dialog.ErrorText, ring.CenterLine);
        Assert.NotEqual("", ring.CenterLine);

        ring.Text = "";
        Assert.Equal(Loc.Instance["Ring_Center_TypeBinding"], ring.CenterLine);
        Assert.Equal("___", ring.CenterBinding);
    }

    // --- Parameter rings ----------------------------------------------------

    [Fact]
    public void LayerStep_RingShowsTheLayers_AndChoosingOneEditsTheBinding()
    {
        var ring = Ring(4);
        ring.GoToCrumb(2);

        Assert.Equal(KeyRingStepKind.Layer, ring.StepKind);
        Assert.Equal(new[] { "layer:0", "layer:1" }, ring.Segments.Select(s => s.Id));
        Assert.Equal(new[] { "Base", "Nav" }, ring.Segments.Select(s => s.Label));
        Assert.True(Segment(ring.Segments, "layer:1").IsCurrent);
        Assert.Null(ring.Panel);

        Assert.True(ring.Choose("layer:0"));

        Assert.Equal("&mo 0", ring.CenterBinding);
        Assert.Equal(KeyRingCenterState.Changed, ring.CenterState);
        Assert.True(ring.Crumbs[2].IsChanged);
        Assert.Equal(KeyRingStepKind.Layer, ring.StepKind);
    }

    [Fact]
    public void CommandStep_RingShowsTheCommands_AndTheBandItsProfiles()
    {
        var ring = Ring(5);
        ring.GoToCrumb(2);

        Assert.Equal(KeyRingStepKind.Command, ring.StepKind);
        Assert.Equal(
            new[]
            {
                "cmd:BT_SEL", "cmd:BT_NXT", "cmd:BT_PRV",
                "cmd:BT_CLR", "cmd:BT_CLR_ALL", "cmd:BT_DISC",
            },
            ring.Segments.Select(s => s.Id).Take(6).ToArray());
        Assert.True(Segment(ring.Segments, "cmd:BT_SEL").IsCurrent);

        var band = ring.BandFor("cmd:BT_SEL");
        Assert.Equal(
            new[] { "prof:BT_SEL:0", "prof:BT_SEL:1", "prof:BT_SEL:2", "prof:BT_SEL:3", "prof:BT_SEL:4" },
            band.Select(s => s.Id));
        Assert.True(Segment(band, "prof:BT_SEL:0").IsCurrent);
        Assert.Empty(ring.BandFor("cmd:BT_CLR"));

        // A command that eats a profile number is not a choice on its own:
        // the view pins the profile band instead.
        Assert.False(ring.Choose("cmd:BT_SEL"));

        Assert.True(ring.Choose("prof:BT_SEL:2"));
        Assert.Equal("&bt BT_SEL 2", ring.CenterBinding);

        Assert.True(ring.Choose("cmd:BT_CLR"));
        Assert.Equal("&bt BT_CLR", ring.CenterBinding);
    }

    [Fact]
    public void KeycodeStep_GroupRingInnerModifierRingAndPanel()
    {
        var ring = Ring(8); // &kp LC(LS(T))
        ring.GoToCrumb(2);

        Assert.Equal(KeyRingStepKind.Keycode, ring.StepKind);
        Assert.Equal(
            new[]
            {
                "keys:Used", "keys:Modifiers", "keys:Letters", "keys:Digits", "keys:Punctuation",
                "keys:Symbols", "keys:Navigation", "keys:Editing", "keys:Function", "keys:More",
            },
            ring.Segments.Select(s => s.Id));
        Assert.True(Segment(ring.Segments, "keys:Letters").IsCurrent);

        // Clockwise from the top, right hand first, so the hands fill halves.
        Assert.Equal(
            new[]
            {
                "mod:RightCtrl", "mod:RightShift", "mod:RightAlt", "mod:RightGui",
                "mod:LeftGui", "mod:LeftAlt", "mod:LeftShift", "mod:LeftCtrl",
            },
            ring.InnerSegments.Select(s => s.Id));
        Assert.True(Segment(ring.InnerSegments, "mod:LeftCtrl").IsOn);
        Assert.True(Segment(ring.InnerSegments, "mod:LeftShift").IsOn);
        Assert.False(Segment(ring.InnerSegments, "mod:LeftAlt").IsOn);

        Assert.Contains(ring.Panel!.Rows, row => row.Id == "keycode:T" && row.IsSelected);

        Assert.True(ring.Choose("mod:LeftShift"));
        Assert.Equal("&kp LC(T)", ring.CenterBinding);

        Assert.True(ring.Choose("mod:RightAlt"));
        Assert.Contains("RA", ring.Dialog.Result!.Params);
    }

    [Fact]
    public void ModifierParamKeycodeStep_HasNoInnerRingButKeepsThePanel()
    {
        var ring = Ring(9); // &mt LSHFT F1: its first param is a modifier
        ring.GoToCrumb(2);

        Assert.Equal(KeyRingStepKind.Keycode, ring.StepKind);
        Assert.Empty(ring.InnerSegments);
        Assert.NotNull(ring.Panel);
    }

    [Fact]
    public void KeycodeRing_PicksAGroupAndAKeycode_AndThePanelSearchesThroughThePicker()
    {
        var ring = Ring(0); // &kp A

        Assert.True(ring.Choose("beh:&kp"));
        Assert.True(ring.Choose("keys:Letters"));
        Assert.Equal(Loc.Instance["Ring_Keys_Letters"], ring.Panel!.Title);

        Assert.True(ring.Choose("keycode:B"));
        Assert.Equal("&kp B", ring.CenterBinding);

        ring.Panel.SearchText = "tab";
        Assert.Contains(ring.Panel.Rows, row => row.Id == "keycode:TAB");

        Assert.False(ring.Choose("keycode:NOPE"));
    }

    [Fact]
    public void PanelHighlight_StartsOnTheSelectedRowAndClampsToTheRows()
    {
        var ring = Ring(0);
        ring.Choose("beh:&kp");
        ring.Choose("keys:Letters");

        var panel = ring.Panel!;
        Assert.Equal(
            panel.Rows.Select((row, index) => (row, index)).First(entry => entry.row.IsSelected).index,
            panel.HighlightIndex);

        panel.MoveHighlight(-1000);
        Assert.Equal(0, panel.HighlightIndex);
        Assert.True(panel.Rows[0].IsHighlighted);
        Assert.Equal(1, panel.Rows.Count(row => row.IsHighlighted));

        panel.MoveHighlight(1);
        Assert.Equal(1, panel.HighlightIndex);
        Assert.Equal(panel.Rows[1].Id, panel.HighlightedId);
    }

    [Fact]
    public void TooManyLayersForTheRing_GoesToThePanel()
    {
        var ring = Ring(ManyLayerConfig, ManyLayerDefines, 0);
        ring.GoToCrumb(2);

        Assert.Empty(ring.Segments);
        Assert.Equal(Loc.Instance["Ring_Panel_Layers"], ring.Panel!.Title);
        Assert.Equal(15, ring.Panel.Rows.Count);

        ring.Panel.SearchText = "L1";
        Assert.Equal(6, ring.Panel.Rows.Count);

        Assert.True(ring.Choose("layer:14"));
        Assert.Equal("&mo 14", ring.CenterBinding);
    }

    [Fact]
    public void PanelKeepsItsSearchTextThroughAnOrdinaryChange_AndIsRebuiltPerSource()
    {
        var ring = Ring(8);
        ring.GoToCrumb(2);

        var panel = ring.Panel!;
        panel.SearchText = "t";

        // A change that leaves the source alone reuses the instance, so the
        // search box (and its rows) survive it.
        Assert.True(ring.Choose("mod:LeftAlt"));

        Assert.Same(panel, ring.Panel);
        Assert.Equal("t", ring.Panel!.SearchText);
        Assert.NotEmpty(ring.Panel.Rows);

        // Another source — here another keycode group — is another panel.
        Assert.True(ring.Choose("keys:Digits"));
        Assert.NotSame(panel, ring.Panel);
    }

    [Fact]
    public void TooManyCommandsForTheRing_ListsThemWithTheirProfiles()
    {
        // Ten keymap-only names on top of the six bundled &bt commands: the
        // command ring no longer fits, so the panel lists them.
        var ring = Ring(ManyCommandConfig, ManyCommandDefines, 0);
        ring.GoToCrumb(2);

        Assert.Equal(KeyRingStepKind.Command, ring.StepKind);
        Assert.Empty(ring.Segments);
        Assert.Equal(
            ((CommandParamViewModel)ring.Dialog.Params[0]).Label,
            ring.Panel!.Title);

        // A command that eats a profile number is listed one row per profile.
        Assert.Contains(ring.Panel.Rows, row => row.Id == "prof:BT_SEL:3");
        Assert.Contains(ring.Panel.Rows, row => row.Id == "cmd:QA");

        ring.Panel.SearchText = "QA";
        Assert.Equal(new[] { "cmd:QA" }, ring.Panel.Rows.Select(row => row.Id));

        ring.Panel.SearchText = "";
        Assert.True(ring.Choose("prof:BT_SEL:3"));
        Assert.Equal("&bt BT_SEL 3", ring.CenterBinding);
    }

    [Fact]
    public void CategoryWiderThanABand_OpensThePanelOnItsBehaviors()
    {
        var ring = Ring(ManyLayerConfig, ManyLayerDefines, 0);
        ring.GoToCrumb(2);

        // On the layer step the panel lists the fifteen layers…
        Assert.Equal(Loc.Instance["Ring_Panel_Layers"], ring.Panel!.Title);

        // …and a category too wide for a band takes the panel over.
        Assert.Empty(ring.BandFor("cat:Keymap"));

        Assert.True(ring.Choose("cat:Keymap"));

        Assert.Equal(Loc.Instance["Ring_Category_Keymap"], ring.Panel!.Title);
        Assert.Equal(8, ring.Panel.Rows.Count);
        Assert.Equal("beh:custom", ring.Panel.Rows[^1].Id);

        // Any choice that isn't a category closes it again.
        Assert.True(ring.Choose("layer:3"));
        Assert.Equal(Loc.Instance["Ring_Panel_Layers"], ring.Panel!.Title);
    }

    // --- Localization keys --------------------------------------------------

    /// <summary>Browsing a keycode group or flipping a modifier doesn't set a
    /// value, so a double-click on it never saves; every other sector does.</summary>
    [Theory]
    [InlineData("beh:&kp", true)]
    [InlineData("cat:Bluetooth", true)]
    [InlineData("layer:1", true)]
    [InlineData("cmd:BT_CLR", true)]
    [InlineData("prof:BT_SEL:2", true)]
    [InlineData("keycode:A", true)]
    [InlineData("keys:Letters", false)]
    [InlineData("mod:LeftShift", false)]
    public void PicksAValue_OnlyForSectorsThatSetPartOfTheBinding(string id, bool expected) =>
        Assert.Equal(expected, KeyRingViewModel.PicksAValue(id));

    [Fact]
    public void EveryRingLabelAndSub_BuildsToAnExistingResxKey()
    {
        var ring = Ring(0);

        foreach (var segment in ring.Segments)
        {
            AssertResolves(segment.Label, segment.Id);
            AssertResolves(segment.Sub ?? "", segment.Id);
        }

        foreach (var category in ring.Segments.Select(segment => segment.Id))
        {
            foreach (var segment in ring.BandFor(category))
            {
                AssertResolves(segment.Label, segment.Id);
                AssertResolves(segment.Sub ?? "", segment.Id);
            }
        }

        // The keycode step's group ring, its inner modifier ring and both
        // panel kinds build their text from runtime keys.
        var keycode = Ring(8);
        keycode.GoToCrumb(2);

        AssertResolves(keycode.Panel!.Title, "keycode panel");

        foreach (var segment in keycode.Segments.Concat(keycode.InnerSegments))
        {
            AssertResolves(segment.Label, segment.Id);
            AssertResolves(segment.Sub ?? "", segment.Id);
        }

        var crowded = Ring(ManyLayerConfig, ManyLayerDefines, 0);
        crowded.GoToCrumb(2);
        AssertResolves(crowded.Panel!.Title, "layer panel");

        foreach (var segment in crowded.BandFor("cat:ModTap"))
        {
            AssertResolves(segment.Label, segment.Id);
            AssertResolves(segment.Sub ?? "", segment.Id);
        }

        crowded.Choose("cat:Keymap");
        AssertResolves(crowded.Panel!.Title, "behavior panel");
    }

    // A ring cannot fit fifteen layers, and seven own hold-taps are too wide
    // for a behavior band: both go to the list panel.
    private const string ManyLayerKeymap = """
        / {
            behaviors {
                h1: h1 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
                h2: h2 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
                h3: h3 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
                h4: h4 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
                h5: h5 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
                h6: h6 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
                h7: h7 { compatible = "zmk,behavior-hold-tap"; #binding-cells = <2>; bindings = <&kp>, <&kp>; };
            };

            keymap {
                compatible = "zmk,keymap";

                L0 { bindings = < &mo 1 >; };
                L1 { bindings = < &trans >; };
                L2 { bindings = < &trans >; };
                L3 { bindings = < &trans >; };
                L4 { bindings = < &trans >; };
                L5 { bindings = < &trans >; };
                L6 { bindings = < &trans >; };
                L7 { bindings = < &trans >; };
                L8 { bindings = < &trans >; };
                L9 { bindings = < &trans >; };
                L10 { bindings = < &trans >; };
                L11 { bindings = < &trans >; };
                L12 { bindings = < &trans >; };
                L13 { bindings = < &trans >; };
                L14 { bindings = < &trans >; };
            };
        };
        """;

    private static readonly KeyboardConfig ManyLayerConfig =
        ZmkKeymapLoader.LoadFromText(ManyLayerKeymap, "test");

    private static readonly IReadOnlyList<KeyValuePair<string, string>> ManyLayerDefines =
        Preprocessor.CollectDefines(ManyLayerKeymap);

    // Six bundled &bt commands plus the ten names this keymap uses: sixteen
    // commands, more than the command ring fits.
    private const string ManyCommandKeymap = """
        / {
            keymap {
                compatible = "zmk,keymap";

                Base { bindings = < &bt BT_SEL 0 &bt QA &bt QB &bt QC &bt QD &bt QE &bt QF &bt QG &bt QH &bt QI &bt QJ >; };

                Nav { bindings = < &trans &trans &trans &trans &trans &trans &trans &trans &trans &trans &trans >; };
            };
        };
        """;

    private static readonly KeyboardConfig ManyCommandConfig =
        ZmkKeymapLoader.LoadFromText(ManyCommandKeymap, "test");

    private static readonly IReadOnlyList<KeyValuePair<string, string>> ManyCommandDefines =
        Preprocessor.CollectDefines(ManyCommandKeymap);

    // The shipped fixture: its every-define list plus the layer-reference
    // subset the ring shows binding text with.
    private static readonly string GloveSource = KeymapFixtures.Read("glove80.keymap");

    private static readonly KeyboardConfig GloveConfig =
        ZmkKeymapLoader.LoadFromText(GloveSource, "glove80");

    private static readonly IReadOnlyList<KeyValuePair<string, string>> GloveDefines =
        Preprocessor.CollectDefines(GloveSource);

    private static readonly IReadOnlyList<KeyValuePair<string, string>> GloveLayerDefines =
        KeymapWriter.LayerReferenceDefines(GloveSource, GloveConfig);

    // --- helpers ------------------------------------------------------------

    private static KeyRingViewModel Ring(int keyIndex, int layerIndex = 0) =>
        Ring(Config, Defines, keyIndex, layerIndex);

    private static KeyRingViewModel Ring(
        KeyboardConfig config,
        IReadOnlyList<KeyValuePair<string, string>> defines,
        int keyIndex,
        int layerIndex = 0)
    {
        var request = new KeyDialogRequest(config, defines, layerIndex, keyIndex);
        return new KeyRingViewModel(new KeyDialogViewModel(request), request);
    }

    private static KeyRingSegment Segment(IReadOnlyList<KeyRingSegment> segments, string id) =>
        segments.First(segment => segment.Id == id);

    /// <summary>A key the code builds at runtime is invisible to
    /// <see cref="LocalizationKeyTests"/>' literal scan; a missing one shows up
    /// here as the <c>[Key]</c> placeholder.</summary>
    private static void AssertResolves(string text, string what) =>
        Assert.True(
            text.Length > 0 && !text.StartsWith("[", StringComparison.Ordinal),
            $"{what} built a missing resx key: '{text}'.");
}
