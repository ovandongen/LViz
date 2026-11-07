using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using LViz.Tests.Keymap;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The combo dialog view-model: picking trigger keys on the board, the label
/// layer they read from, the auto-name that follows the binding until the user
/// types, the fields of the combo, the checks (as translated messages) and the
/// combo the dialog hands back.
/// </summary>
public class ComboDialogViewModelTests
{
    // Slabwing: 56 keys, 7 layers (Base, Symbols, Adjust, Nav, Nav-alt,
    // Nav-alt-shift, Numbers) and 4 combos — combo_bootloader(0, timeout 80,
    // keys 1 2 12, &bootloader), mouse-mid(1), f12(2, keys 0 1, &kp F12) and
    // syslayer(3).
    private static readonly string Source = KeymapFixtures.Read("slabwing.keymap");

    private static readonly KeyboardConfig Config =
        ZmkKeymapLoader.LoadFromText(Source, "slabwing");

    private static readonly IReadOnlyList<KeyValuePair<string, string>> Defines =
        Preprocessor.CollectDefines(Source);

    private static readonly IReadOnlyList<KeyValuePair<string, string>> LayerDefines =
        KeymapWriter.LayerReferenceDefines(Source, Config);

    private static KeyBinding Kp(string keycode) =>
        new("&kp", new[] { keycode });

    private static ComboDialogRequest Request(int? comboIndex = null, int shownLayerIndex = 0) =>
        new(new FakeKeyboardProfile(keyCount: 56), Config, Defines, LayerDefines, comboIndex, shownLayerIndex);

    /// <summary>A dialog whose binding editor answers <paramref name="answer"/>
    /// (and records the request it was given).</summary>
    private static ComboDialogViewModel WithEditor(
        ComboDialogRequest request, List<KeyDialogRequest>? captured = null, KeyBinding? answer = null)
    {
        var vm = new ComboDialogViewModel(request);
        vm.BindingEditorRequested = given =>
        {
            captured?.Add(given);
            return Task.FromResult<KeyBinding?>(answer);
        };
        return vm;
    }

    private static void Tap(ComboDialogViewModel vm, params int[] indices)
    {
        var tap = vm.Board.OnKeyTapped!;
        foreach (var index in indices) tap(index);
    }

    private static bool HasBinding(KeyBinding? binding, string behavior, params string[] param) =>
        binding is not null
        && binding.Behavior == behavior
        && binding.Params.SequenceEqual(param);

    // --- A new combo --------------------------------------------------------

    [Fact]
    public void NewCombo_StartsUnnamedAndIncomplete()
    {
        var vm = new ComboDialogViewModel(Request());

        Assert.Equal(Loc.Instance["Combo_TitleNew"], vm.TitleText);
        Assert.Equal("combo", vm.NodeName);
        Assert.True(vm.IsNameAuto);
        Assert.Empty(vm.SelectedKeys);
        Assert.Null(vm.Binding);
        Assert.Equal(Loc.Instance["Combo_BindingNone"], vm.BindingText);
        Assert.False(vm.CanConfirm);
        Assert.Contains(Loc.Instance["Combo_ErrorNoBinding"], vm.Errors);
        Assert.Contains(Loc.Instance["Combo_ErrorTooFewKeys"], vm.Errors);
        Assert.True(vm.AllLayers);
        Assert.Equal(7, vm.LabelLayers.Count);
        Assert.Equal(0, vm.LabelLayer.Index);
    }

    [Fact]
    public void NewCombo_TappingKeysSelectsThemGold()
    {
        var vm = new ComboDialogViewModel(Request());

        Tap(vm, 10, 11);

        Assert.Equal(new[] { 10, 11 }, vm.SelectedKeys);
        Assert.Equal(AppTheme.AccentGoldHex, vm.Board.Keys[10].KeyFillColor);
        Assert.Equal(AppTheme.AccentGoldHex, vm.Board.Keys[11].KeyFillColor);
        Assert.Equal(AppTheme.KeyDefaultFillHex, vm.Board.Keys[0].KeyFillColor);
    }

    [Fact]
    public void NewCombo_TapsOutsideTheBoardAreIgnored()
    {
        var vm = new ComboDialogViewModel(Request());

        Tap(vm, -1, 56, 500);

        Assert.Empty(vm.SelectedKeys);
    }

    [Fact]
    public async Task NewCombo_PickedBindingNamesItAndOpensTheResult()
    {
        var vm = WithEditor(Request(), answer: Kp("ESC"));
        Tap(vm, 10, 11);

        await vm.EditBindingCommand.ExecuteAsync(null);

        Assert.Equal("combo_ESC", vm.NodeName);
        Assert.True(vm.CanConfirm);
        Assert.True(vm.IsNameAuto);

        var result = vm.Result()!;
        Assert.Equal("combo_ESC", result.NodeName);
        Assert.Equal(new[] { 10, 11 }, result.KeyPositions);
        Assert.Equal(new[] { -1 }, result.Layers);
        Assert.Null(result.TimeoutMs);
        Assert.Null(result.RequirePriorIdleMs);
        Assert.False(result.SlowRelease);
        Assert.Equal("", result.Description);
        Assert.True(HasBinding(result.Binding, "&kp", "ESC"));
    }

    [Fact]
    public async Task EditBinding_AsksForACarriedBindingOnTheLabelLayer()
    {
        var captured = new List<KeyDialogRequest>();
        var vm = WithEditor(Request(), captured);

        await vm.EditBindingCommand.ExecuteAsync(null);

        var given = Assert.Single(captured);
        Assert.Equal(-1, given.KeyIndex);
        Assert.Same(KeyBinding.None, given.Binding);
        Assert.Equal(vm.TitleText, given.Caption);
        Assert.Equal(vm.LabelLayer.Index, given.LayerIndex);
        Assert.Same(LayerDefines, given.LayerDefines);
    }

    [Fact]
    public async Task EditBinding_CarriesTheCurrentBindingAndKeepsItOnCancel()
    {
        var captured = new List<KeyDialogRequest>();
        var vm = WithEditor(Request(comboIndex: 2), captured, answer: null);

        await vm.EditBindingCommand.ExecuteAsync(null);

        Assert.True(HasBinding(captured[0].Binding, "&kp", "F12"));
        Assert.True(HasBinding(vm.Binding, "&kp", "F12"));
    }

    [Fact]
    public async Task EditBinding_AnsweredNoneMeansNoBinding()
    {
        var vm = WithEditor(Request(comboIndex: 2), answer: KeyBinding.None);

        await vm.EditBindingCommand.ExecuteAsync(null);

        Assert.Null(vm.Binding);
        Assert.Equal(Loc.Instance["Combo_BindingNone"], vm.BindingText);
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public async Task TypedName_StopsFollowingTheBinding()
    {
        var vm = WithEditor(Request(), answer: Kp("ESC"));

        vm.NodeName = "my_combo";
        await vm.EditBindingCommand.ExecuteAsync(null);

        Assert.Equal("my_combo", vm.NodeName);
        Assert.False(vm.IsNameAuto);
    }

    // --- Editing an existing combo -----------------------------------------

    [Fact]
    public void EditF12_SeedsKeysNameAndBinding()
    {
        var vm = new ComboDialogViewModel(Request(comboIndex: 2));

        Assert.Equal(Loc.Instance.Format("Combo_TitleEditFormat", "f12"), vm.TitleText);
        Assert.Equal("f12", vm.NodeName);
        Assert.False(vm.IsNameAuto);
        Assert.Equal(new[] { 0, 1 }, vm.SelectedKeys);
        Assert.True(HasBinding(vm.Binding, "&kp", "F12"));
        Assert.Equal("&kp F12", vm.BindingText);
        Assert.True(vm.CanConfirm);
        Assert.Equal(AppTheme.AccentGoldHex, vm.Board.Keys[0].KeyFillColor);
    }

    [Fact]
    public void EditF12_TappingASelectedKeyDropsItAndTheChecksFollow()
    {
        var vm = new ComboDialogViewModel(Request(comboIndex: 2));

        Tap(vm, 1);

        Assert.Equal(new[] { 0 }, vm.SelectedKeys);
        Assert.False(vm.CanConfirm);
        Assert.Contains(Loc.Instance["Combo_ErrorTooFewKeys"], vm.Errors);
        Assert.Equal(AppTheme.KeyDefaultFillHex, vm.Board.Keys[1].KeyFillColor);

        Tap(vm, 1);

        Assert.Equal(new[] { 0, 1 }, vm.SelectedKeys);
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public void EditF12_NameOfAnotherComboIsAnErrorNamingIt()
    {
        var vm = new ComboDialogViewModel(Request(comboIndex: 2));

        vm.NodeName = "syslayer";

        Assert.False(vm.CanConfirm);
        Assert.Contains(Loc.Instance.Format("Combo_ErrorDuplicateNameFormat", "syslayer"), vm.Errors);
    }

    [Fact]
    public void EditF12_NameThatIsNotANodeNameIsAnError()
    {
        var vm = new ComboDialogViewModel(Request(comboIndex: 2));

        vm.NodeName = "1x";

        Assert.False(vm.CanConfirm);
        Assert.Contains(Loc.Instance["Combo_ErrorInvalidName"], vm.Errors);
        Assert.DoesNotContain(Loc.Instance.Format("Combo_ErrorDuplicateNameFormat", "syslayer"), vm.Errors);
    }

    [Fact]
    public void EditF12_RenamingAnUnlabelledNode_MovesItsNameAlong()
    {
        // f12 has no label, so its Name is its node name and follows a rename.
        var vm = new ComboDialogViewModel(Request(comboIndex: 2));

        vm.NodeName = "f12_again";

        var result = vm.Result()!;
        Assert.Equal("f12_again", result.NodeName);
        Assert.Equal("f12_again", result.Name);
    }

    [Fact]
    public void RenamingALabelledNode_KeepsItsLabel()
    {
        // A label (`esc_lbl: combo_esc { … }`) is the parsed Name; a rename
        // touches only the node name.
        var labelled = Config with
        {
            Combos = [new ZmkCombo("esc_lbl", "", [10, 11], [-1], Kp("ESC")) { NodeName = "combo_esc" }],
        };
        var vm = new ComboDialogViewModel(new ComboDialogRequest(
            new FakeKeyboardProfile(keyCount: 56), labelled, Defines, LayerDefines, 0, 0));

        vm.NodeName = "combo_escape";

        var result = vm.Result()!;
        Assert.Equal("combo_escape", result.NodeName);
        Assert.Equal("esc_lbl", result.Name);
    }

    [Fact]
    public void Timeout_MustBeAPositiveNumberOrEmpty()
    {
        var vm = new ComboDialogViewModel(Request(comboIndex: 2));

        vm.TimeoutText = "abc";
        Assert.False(vm.CanConfirm);
        Assert.Contains(Loc.Instance["Combo_ErrorTimeout"], vm.Errors);

        vm.TimeoutText = "30";
        Assert.True(vm.CanConfirm);
        Assert.Equal(30, vm.Result()!.TimeoutMs);

        vm.TimeoutText = "";
        Assert.True(vm.CanConfirm);
        Assert.Null(vm.Result()!.TimeoutMs);
    }

    [Fact]
    public void Idle_MayBeZeroButNotNegative()
    {
        var vm = new ComboDialogViewModel(Request(comboIndex: 2));

        vm.IdleText = "0";
        Assert.True(vm.CanConfirm);
        Assert.Equal(0, vm.Result()!.RequirePriorIdleMs);

        vm.IdleText = "-1";
        Assert.False(vm.CanConfirm);
        Assert.Contains(Loc.Instance["Combo_ErrorIdle"], vm.Errors);
    }

    [Fact]
    public void SlowRelease_WritesThroughToTheResult()
    {
        var vm = new ComboDialogViewModel(Request(comboIndex: 2));

        vm.SlowRelease = true;

        Assert.True(vm.Result()!.SlowRelease);
    }

    [Fact]
    public void Layers_PerLayerScopeReplacesAllLayers()
    {
        var vm = new ComboDialogViewModel(Request(comboIndex: 2));

        vm.AllLayers = false;
        Assert.False(vm.CanConfirm);
        Assert.Contains(Loc.Instance["Combo_ErrorNoLayers"], vm.Errors);

        vm.LayerToggles[1].IsOn = true;
        Assert.True(vm.CanConfirm);
        Assert.Equal(new[] { 1 }, vm.Result()!.Layers);
    }

    [Fact]
    public void EditComboBootloader_ReadsItsTimeoutBack()
    {
        var vm = new ComboDialogViewModel(Request(comboIndex: 0));

        Assert.Equal("80", vm.TimeoutText);
        Assert.Equal("combo_bootloader", vm.NodeName);
        Assert.Equal(new[] { 1, 2, 12 }, vm.SelectedKeys);
        Assert.True(vm.AllLayers);
    }

    [Fact]
    public async Task FiveKeys_WarnAboutTheFirmwareLimitButStayConfirmable()
    {
        var vm = WithEditor(Request(), answer: Kp("ESC"));
        await vm.EditBindingCommand.ExecuteAsync(null);

        Tap(vm, 0, 1, 3, 4, 5);

        var warning = Assert.Single(vm.Warnings);
        Assert.Equal(Loc.Instance.Format("Combo_WarnTooManyKeysFormat", 5), warning);
        Assert.True(vm.CanConfirm);
        Assert.Equal(new[] { 0, 1, 3, 4, 5 }, vm.Result()!.KeyPositions);
    }

    // --- Labels on the board -----------------------------------------------

    [Fact]
    public void LabelLayer_DecidesTheKeyLabelsAndTheSummary()
    {
        var vm = new ComboDialogViewModel(Request());
        Tap(vm, 0, 1);

        // Base: &mt F1 NUMBER_1 and &mt F2 NUMBER_2 show their tap keycode.
        Assert.Equal("1 + 2", vm.KeysSummary);

        vm.LabelLayer = vm.LabelLayers[1];

        // Symbols: &kp LS(AMPS) and &kp LBKT.
        Assert.Equal("& + [", vm.KeysSummary);
        Assert.Equal(vm.Board.Keys[0].Label + " + " + vm.Board.Keys[1].Label, vm.KeysSummary);
    }

    [Fact]
    public void KeysSummary_FallsBackToTheIndexWhenTheKeyShowsNothing()
    {
        // Layer 2 (Adjust) binds keys 5 and 6 to &none, which shows no label.
        var vm = new ComboDialogViewModel(Request(shownLayerIndex: 2));

        Tap(vm, 5, 6);

        Assert.Equal("#5 + #6", vm.KeysSummary);
    }

    [Fact]
    public void ShownLayerIndex_OutsideTheKeymapClampsToTheNearestLayer()
    {
        var vm = new ComboDialogViewModel(Request(shownLayerIndex: 99));

        Assert.Equal(vm.LabelLayers[^1], vm.LabelLayer);
    }

    // --- The other combos' badges ------------------------------------------

    [Fact]
    public void Badges_NumberTheOtherCombosByTheirPlaceInTheKeymap()
    {
        var vm = new ComboDialogViewModel(Request());

        // Key 0 sits in f12 alone — the third combo of the keymap.
        Assert.Equal(new[] { 3 }, vm.Board.Keys[0].ComboBadges.Select(badge => badge.Number).ToArray());
        Assert.True(vm.Board.ComboLegend.IsOverlayVisible);
    }

    [Fact]
    public void Badges_LeaveOutTheComboBeingEdited()
    {
        var vm = new ComboDialogViewModel(Request(comboIndex: 2));

        Assert.Empty(vm.Board.Keys[0].ComboBadges);
        // syslayer (index 3) is untouched by editing f12.
        Assert.Equal(new[] { 4 }, vm.Board.Keys[52].ComboBadges.Select(badge => badge.Number).ToArray());
    }

    [Fact]
    public void KeyLabels_ComeFromTheLabelLayerAndSayWhatTheyFire()
    {
        var vm = new ComboDialogViewModel(Request());

        Assert.Equal("&kp ESCAPE", vm.Board.Keys[10].Tooltip.Split('\n', 3)[2]);
        Assert.Equal("ESCAPE", vm.Board.Keys[10].Label);
    }
}
