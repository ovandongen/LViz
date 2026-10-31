using LViz.App.Localization;
using LViz.App.ViewModels;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Layout.Import;
using LViz.Tests.Layout;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// <see cref="LayoutWizardViewModel"/> source step and navigation: scanning a
/// temp zmk-config tree built from the layout fixtures, keymap choice (loaded
/// vs. scanned vs. unreadable), candidate ranking and labelling, the info
/// lines, name/device-name prefills, and step can-execute rules — plus the
/// "build from questions" grid source (answers, clamping, generated editor,
/// save), the adjust step (editor build/keep/rebuild, labels, warnings) and
/// saving (edited normalised keys, layout files, <c>.dtsi</c> export, error
/// handling, close result).
/// </summary>
public sealed class LayoutWizardViewModelTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
    }

    // --- no loaded keymap: the scanned config keymap takes over ---

    [Fact]
    public async Task LoadSource_Folder_NoLoadedKeymap_RanksScansOwnKeymapAndPrefills()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm();

        await vm.LoadSourceAsync(root, isFolder: true);

        Assert.Equal(
            [LayoutSource.InfoJson, LayoutSource.MatrixTransform],
            vm.Candidates.Select(c => c.Candidate.Layout.Source));
        Assert.All(vm.Candidates, c => Assert.True(c.MatchesKeymap));
        Assert.True(vm.Candidates.All(c => c.Candidate.Layout.Keys.Count == 56));
        Assert.Same(vm.Candidates[0], vm.SelectedCandidate);

        Assert.Equal("Slabwing", vm.Name);
        Assert.Equal(Path.Combine(root, "config", "slabwing.keymap"), vm.KeymapPathToLoad);
        Assert.NotNull(vm.Scan);
        Assert.True(vm.CanSaveToConfig);
        Assert.False(vm.AlsoSaveToConfig);
        Assert.Equal(root, vm.SourcePath);

        // Titles name the source format, the layout and the key count; details
        // are the path relative to the scan root.
        var expected = vm.Candidates.Select(c => Loc.Instance.Format(
            "Wizard_CandidateFormat",
            SourceLabel(c.Candidate.Layout.Source),
            c.Candidate.Layout.Name,
            c.Candidate.Layout.Keys.Count)).ToList();
        Assert.Equal(expected, vm.Candidates.Select(c => c.Title));
        Assert.Equal(
            [Path.Combine("config", "info.json"), Path.Combine("boards", "shields", "slabwing", "slabwing.overlay")],
            vm.Candidates.Select(c => c.Detail));

        Assert.Contains(
            Loc.Instance.Format("Wizard_KeymapUsedFormat", "slabwing.keymap", 56),
            vm.SourceInfo);
    }

    [Fact]
    public async Task LoadSource_Folder_SavedLayoutBecomesTopCandidate()
    {
        var root = CreateSlabwingTree();
        UserLayoutStore.Save(SavedSlabwingLayout(), Path.Combine(root, "config"));
        var vm = CreateVm();

        await vm.LoadSourceAsync(root, isFolder: true);

        var top = vm.Candidates[0];
        Assert.Equal(LayoutSource.UserLayout, top.Candidate.Layout.Source);
        Assert.Equal("Slabwing", top.Candidate.Layout.Name);
        Assert.Equal(56, top.Candidate.Layout.Keys.Count);
        Assert.Equal(
            Loc.Instance.Format(
                "Wizard_CandidateFormat",
                Loc.Instance["Wizard_Source_UserLayout"],
                "Slabwing",
                56),
            top.Title);
        Assert.Same(top, vm.SelectedCandidate);
    }

    [Fact]
    public async Task LoadSource_Folder_LoadedKeymapStaysInCharge_NothingToLoadAfterwards()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm(loadedKeymap: KeymapWithBindings(56));

        await vm.LoadSourceAsync(root, isFolder: true);

        Assert.Null(vm.KeymapPathToLoad);
        Assert.All(vm.Candidates, c => Assert.True(c.MatchesKeymap));
        Assert.Contains(Loc.Instance["Wizard_KeymapCurrent"], vm.SourceInfo);
    }

    [Fact]
    public async Task LoadedKeymapNotice_ShowsBeforeTheFirstScanOnly()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm(loadedKeymap: KeymapWithBindings(56));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal(Loc.Instance.Format("Wizard_UsingLoadedKeymapFormat", 56), vm.LoadedKeymapNotice);

        await vm.LoadSourceAsync(root, isFolder: true);

        Assert.Null(vm.LoadedKeymapNotice);
        Assert.Contains(nameof(LayoutWizardViewModel.LoadedKeymapNotice), raised);
        Assert.Null(CreateVm().LoadedKeymapNotice);
    }

    // --- unreadable keymap ---

    [Fact]
    public async Task LoadSource_UnreadableKeymap_ContinuesWithoutKeymapInSourceOrder()
    {
        var root = CreateSlabwingTree();
        File.WriteAllText(Path.Combine(root, "config", "slabwing.keymap"), "this is /// not a keymap )(");
        var vm = CreateVm();

        await vm.LoadSourceAsync(root, isFolder: true);

        Assert.Null(vm.Keymap);
        Assert.Null(vm.KeymapPathToLoad);
        Assert.Contains("slabwing.keymap", vm.SourceInfo);
        var unreadablePrefix = Loc.Instance["Wizard_KeymapUnreadableFormat"].Split('{')[0];
        Assert.Contains(unreadablePrefix + "slabwing.keymap", vm.SourceInfo);
        // Still both candidates, source order (no keymap count to boost).
        Assert.Equal(
            [LayoutSource.InfoJson, LayoutSource.MatrixTransform],
            vm.Candidates.Select(c => c.Candidate.Layout.Source));
        Assert.All(vm.Candidates, c => Assert.False(c.MatchesKeymap));
        Assert.NotNull(vm.SelectedCandidate);
    }

    // --- empty folder ---

    [Fact]
    public async Task LoadSource_FolderWithoutLayouts_ReportsNoneAndBlocksNext()
    {
        var root = CreateTempRoot();
        WriteFile(root, "README.md", "nothing board-shaped here");
        var vm = CreateVm();

        await vm.LoadSourceAsync(root, isFolder: true);

        Assert.Empty(vm.Candidates);
        Assert.Null(vm.SelectedCandidate);
        Assert.Contains(Loc.Instance["Wizard_NoCandidates"], vm.SourceInfo);
        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.False(vm.CanSaveToConfig);
    }

    // --- device-name prefill ---

    [Fact]
    public async Task HidName_PrefersConnectedDeviceMatchingKeyboardName()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm(connected: ["Ploopy Bean", "Slabwing"]);

        await vm.LoadSourceAsync(root, isFolder: true);

        Assert.Equal("Slabwing", vm.HidName);
    }

    [Fact]
    public async Task HidName_FallsBackToKeyboardName_WhenNoDeviceMatches()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm(connected: ["Ploopy Bean"]);

        await vm.LoadSourceAsync(root, isFolder: true);

        Assert.Equal("Slabwing", vm.HidName);

        vm.UseDeviceNameCommand.Execute("Ploopy Bean");
        Assert.Equal("Ploopy Bean", vm.HidName);
    }

    // --- navigation ---

    [Fact]
    public async Task Navigation_CanExecuteFollowsStepAndSelection()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm();

        // Step 0, nothing scanned yet: no selection, no Next; no Back.
        Assert.True(vm.IsSourceStep);
        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.False(vm.BackCommand.CanExecute(null));

        await vm.LoadSourceAsync(root, isFolder: true);
        Assert.True(vm.NextCommand.CanExecute(null));
        Assert.False(vm.BackCommand.CanExecute(null));

        vm.NextCommand.Execute(null);
        Assert.Equal(1, vm.CurrentStep);
        Assert.True(vm.IsPreviewStep);
        Assert.True(vm.NextCommand.CanExecute(null));
        Assert.True(vm.BackCommand.CanExecute(null));

        vm.NextCommand.Execute(null);
        Assert.Equal(2, vm.CurrentStep);
        Assert.True(vm.IsSaveStep);
        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.True(vm.BackCommand.CanExecute(null));

        vm.BackCommand.Execute(null);
        Assert.Equal(1, vm.CurrentStep);
        Assert.True(vm.IsPreviewStep);
    }

    [Fact]
    public async Task Navigation_DroppingSelection_DisablesNextAgain()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm();
        await vm.LoadSourceAsync(root, isFolder: true);

        vm.SelectedCandidate = null;

        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Cancel_RaisesCloseRequestedWithNull()
    {
        var vm = CreateVm();
        var calls = new List<LayoutWizardResult?>();
        vm.CloseRequested += calls.Add;

        vm.CancelCommand.Execute(null);

        var received = Assert.Single(calls);
        Assert.Null(received);
    }

    // --- browse commands ---

    [Fact]
    public async Task BrowseFolder_And_BrowseFile_UseThePickers_AndSkipOnCancel()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm();

        vm.PickFolder = () => Task.FromResult<string?>(root);
        await vm.BrowseFolderCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Candidates.Count);

        vm.PickFolder = () => Task.FromResult<string?>(null);
        await vm.BrowseFolderCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Candidates.Count); // nothing loaded, nothing cleared

        var overlay = Path.Combine(root, "boards", "shields", "slabwing", "slabwing.overlay");
        vm.PickFile = () => Task.FromResult<string?>(overlay);
        await vm.BrowseFileCommand.ExecuteAsync(null);
        Assert.Equal(overlay, vm.SourcePath);
        Assert.Equal(
            [LayoutSource.MatrixTransform],
            vm.Candidates.Select(c => c.Candidate.Layout.Source));
    }

    [Fact]
    public async Task LoadSource_WhileScanning_BlocksBrowseAndNext()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm();
        var duringScan = new List<(bool Folder, bool File, bool Next, string? Info)>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LayoutWizardViewModel.IsScanning) && vm.IsScanning)
                duringScan.Add((vm.BrowseFolderCommand.CanExecute(null), vm.BrowseFileCommand.CanExecute(null),
                    vm.NextCommand.CanExecute(null), vm.SourceInfo));
        };

        await vm.LoadSourceAsync(root, isFolder: true);

        var state = Assert.Single(duringScan);
        Assert.False(state.Folder);
        Assert.False(state.File);
        Assert.False(state.Next);
        Assert.Equal(Loc.Instance["Wizard_Scanning"], state.Info);
        Assert.False(vm.IsScanning);
        Assert.True(vm.BrowseFolderCommand.CanExecute(null));
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    // --- adjust step ---

    [Fact]
    public async Task Preview_SlabwingKeymap_SplitsTwentyEightTwentyEightAndLabelsBindings()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm();

        await vm.LoadSourceAsync(root, isFolder: true);
        vm.NextCommand.Execute(null);

        var editor = vm.Editor!;
        Assert.Equal(28, editor.Keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(28, editor.Keys.Count(k => k.Hand == Hand.Right));
        Assert.All(editor.Labels, label => Assert.Equal("A", label));
        Assert.Null(vm.PreviewWarning);
    }

    [Fact]
    public async Task Preview_KeymapBiggerThanLayout_WarnsWithBothCounts()
    {
        var root = CreateSlabwingTree(keymapBindings: 58);
        var vm = CreateVm();

        await vm.LoadSourceAsync(root, isFolder: true);
        vm.NextCommand.Execute(null);

        Assert.Contains(
            Loc.Instance.Format("Wizard_KeyCountMismatchFormat", 56, 58),
            vm.PreviewWarning!);
        // Bindings 56-57 have no cap to sit on; every board key still gets one.
        Assert.Equal(56, vm.Editor!.Keys.Count);
        Assert.All(vm.Editor.Labels, label => Assert.Equal("A", label));
    }

    [Fact]
    public async Task Preview_NoKeymap_LabelsAreKeyIndices()
    {
        var root = CreateSlabwingTree();
        File.Delete(Path.Combine(root, "config", "slabwing.keymap"));
        var vm = CreateVm();

        await vm.LoadSourceAsync(root, isFolder: true);
        vm.NextCommand.Execute(null);

        Assert.Null(vm.Keymap);
        Assert.Equal(
            Enumerable.Range(0, 56).Select(i => i.ToString()),
            vm.Editor!.Labels);
    }

    [Fact]
    public async Task Preview_MatrixTransformCandidate_KeepsImporterWarning()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm();
        await vm.LoadSourceAsync(root, isFolder: true);

        var rough = vm.Candidates
            .First(c => c.Candidate.Layout.Source == LayoutSource.MatrixTransform);
        Assert.NotEmpty(rough.Candidate.Layout.Warnings);
        vm.SelectedCandidate = rough;
        vm.NextCommand.Execute(null);

        Assert.Contains(Loc.Instance["Wizard_Warning_MatrixTransformRough"], vm.PreviewWarning!);
    }

    [Theory]
    [InlineData(LayoutWarningCode.MatrixTransformRough, "Wizard_Warning_MatrixTransformRough", null)]
    [InlineData(LayoutWarningCode.MatrixTransformGrid, "Wizard_Warning_MatrixTransformGrid", null)]
    [InlineData(LayoutWarningCode.RotationWithoutPivot, "Wizard_Warning_RotationWithoutPivotFormat", 7)]
    public void FormatWarning_UsesTheResourceText(
        LayoutWarningCode code, string resourceKey, int? keyIndex)
    {
        var text = LayoutWizardViewModel.FormatWarning(new LayoutImportWarning(code, keyIndex));

        Assert.Equal(string.Format(Loc.Instance[resourceKey], keyIndex ?? 0), text);
        if (keyIndex is not null) Assert.Contains("7", text);
    }

    [Fact]
    public async Task Preview_DroppingCandidate_ClearsEditorAndWarning()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm();
        await vm.LoadSourceAsync(root, isFolder: true);
        vm.NextCommand.Execute(null);
        Assert.NotNull(vm.Editor);

        vm.SelectedCandidate = null;

        Assert.Null(vm.Editor);
        Assert.Null(vm.PreviewWarning);
    }

    [Fact]
    public async Task Adjust_BackThenNext_KeepsEditorAndEdits_OtherCandidateRebuilds()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm();
        await vm.LoadSourceAsync(root, isFolder: true);
        vm.NextCommand.Execute(null);

        var editor = vm.Editor!;
        editor.SelectAll();
        editor.Nudge(1, 0, free: false);
        Assert.True(editor.IsModified);

        vm.BackCommand.Execute(null);
        vm.NextCommand.Execute(null);

        // Same editor, edits intact — Back/Next never rebuilds the same source.
        Assert.Same(editor, vm.Editor);
        Assert.True(vm.Editor!.IsModified);

        // A different candidate is a new source: Next builds a fresh editor.
        vm.BackCommand.Execute(null);
        vm.SelectedCandidate = vm.Candidates[1];
        vm.NextCommand.Execute(null);

        Assert.NotNull(vm.Editor);
        Assert.NotSame(editor, vm.Editor);
        Assert.False(vm.Editor!.IsModified);
    }

    // --- build from questions (grid source) ---

    [Fact]
    public void GridDefaults_AreACorneLikeBoard()
    {
        var vm = CreateVm();

        Assert.False(vm.IsGridSource);
        Assert.Equal(42, vm.GridKeyCount);
        Assert.Equal(Loc.Instance.Format("Wizard_GridKeyCountFormat", 42), vm.GridKeyCountText);
    }

    [Fact]
    public void GridSource_NothingScanned_EnablesNext_BuildsSplitGridWithIndexLabels()
    {
        var vm = CreateVm();

        vm.UseGridSourceCommand.Execute(null);
        Assert.True(vm.IsGridSource);
        Assert.True(vm.NextCommand.CanExecute(null));

        vm.NextCommand.Execute(null);

        var editor = vm.Editor!;
        Assert.Equal(42, editor.Keys.Count);
        Assert.Equal(21, editor.Keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(21, editor.Keys.Count(k => k.Hand == Hand.Right));
        Assert.Equal(Enumerable.Range(0, 42).Select(i => i.ToString()), editor.Labels);
        Assert.Null(vm.PreviewWarning);
    }

    [Fact]
    public void GridSource_MatchingLoadedKeymap_LabelsWithoutWarning_ChangedColumnsWarnWithBothCounts()
    {
        var vm = CreateVm(loadedKeymap: KeymapWithBindings(42));
        Assert.True(vm.GridMatchesKeymap);

        vm.UseGridSourceCommand.Execute(null);
        vm.NextCommand.Execute(null);

        Assert.All(vm.Editor!.Labels, label => Assert.Equal("A", label));
        Assert.Null(vm.PreviewWarning);

        vm.BackCommand.Execute(null);
        vm.GridColumns = 5;
        Assert.Equal(36, vm.GridKeyCount);
        Assert.False(vm.GridMatchesKeymap);

        vm.NextCommand.Execute(null);

        Assert.Equal(36, vm.Editor!.Keys.Count);
        Assert.Contains(
            Loc.Instance.Format("Wizard_KeyCountMismatchFormat", 36, 42), vm.PreviewWarning!);
    }

    [Fact]
    public void GridInputs_ClampToGeneratorLimits_AndRaiseTheSummary()
    {
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.GridRows = 0;
        Assert.Equal(1, vm.GridRows);
        vm.GridRows = 20;
        Assert.Equal(8, vm.GridRows);
        vm.GridColumns = 0;
        Assert.Equal(1, vm.GridColumns);
        vm.GridThumbKeys = 99;
        Assert.Equal(8, vm.GridThumbKeys);

        Assert.Contains(nameof(LayoutWizardViewModel.GridKeyCount), raised);
        Assert.Contains(nameof(LayoutWizardViewModel.GridMatchesKeymap), raised);
        Assert.Contains(nameof(LayoutWizardViewModel.GridKeyCountText), raised);
    }

    [Fact]
    public void GridSource_BackThenNext_KeepsEditor_ChangedAnswerRebuilds()
    {
        var vm = CreateVm();
        vm.UseGridSourceCommand.Execute(null);
        vm.NextCommand.Execute(null);

        var editor = vm.Editor!;
        editor.SelectAll();
        editor.Nudge(1, 0, free: false);
        Assert.True(editor.IsModified);

        vm.BackCommand.Execute(null);
        vm.NextCommand.Execute(null);

        // Same answers → the same editor, edits intact.
        Assert.Same(editor, vm.Editor);
        Assert.True(vm.Editor!.IsModified);

        vm.BackCommand.Execute(null);
        vm.GridRows = 4;
        vm.NextCommand.Execute(null);

        Assert.NotNull(vm.Editor);
        Assert.NotSame(editor, vm.Editor);
        Assert.Equal(54, vm.Editor!.Keys.Count);
        Assert.False(vm.Editor.IsModified);
    }

    [Fact]
    public void UseFileSource_RestoresTheCandidateRuleForNext()
    {
        var vm = CreateVm();
        vm.UseGridSourceCommand.Execute(null);
        Assert.True(vm.NextCommand.CanExecute(null));

        vm.UseFileSourceCommand.Execute(null);

        Assert.False(vm.IsGridSource);
        Assert.False(vm.NextCommand.CanExecute(null)); // nothing scanned
    }

    [Fact]
    public async Task Scan_SwapsInItsKeymap_RaisesTheGridSummary()
    {
        var root = CreateSlabwingTree(keymapBindings: 42);
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(vm.GridMatchesKeymap); // no keymap yet

        await vm.LoadSourceAsync(root, isFolder: true);

        Assert.True(vm.GridMatchesKeymap); // 42 bindings match the default answers
        Assert.Contains(nameof(LayoutWizardViewModel.GridMatchesKeymap), raised);
    }

    [Fact]
    public void GridSave_WritesLayoutAndDtsi_AndClosesWithNoKeymapToLoad()
    {
        var root = CreateTempRoot();
        var layouts = Path.Combine(root, "layouts");
        var vm = CreateVm(layoutsDirectory: layouts);
        vm.UseGridSourceCommand.Execute(null);
        vm.Name = "Grid Board";
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.True(vm.SaveCommand.CanExecute(null));

        var calls = new List<LayoutWizardResult?>();
        vm.CloseRequested += calls.Add;
        vm.SaveCommand.Execute(null);

        Assert.True(File.Exists(Path.Combine(layouts, "grid-board.layout.json")));
        var dtsi = Path.Combine(layouts, "grid-board.dtsi");
        Assert.True(File.Exists(dtsi));
        Assert.StartsWith(ZmkPhysicalLayoutWriter.GeneratedMarker, File.ReadAllText(dtsi));

        var result = Assert.Single(calls);
        Assert.NotNull(result);
        Assert.Equal("grid-board", result!.Layout.Id);
        Assert.Equal(42, result.Layout.Keys.Count);
        // Nothing was scanned, so there is no keymap to load afterwards.
        Assert.Null(result.KeymapPathToLoad);
    }

    [Fact]
    public void GridSave_BlankName_DisablesSave()
    {
        var vm = CreateVm();
        vm.UseGridSourceCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);

        Assert.True(vm.IsSaveStep);
        Assert.False(vm.SaveCommand.CanExecute(null)); // Name still blank — questions fill none in

        vm.Name = "Grid Board";
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    // --- save step ---

    [Fact]
    public async Task Save_WritesLayoutFileAndClosesWithResult()
    {
        var root = CreateSlabwingTree();
        var layouts = Path.Combine(root, "layouts");
        var vm = CreateVm(layoutsDirectory: layouts);
        await vm.LoadSourceAsync(root, isFolder: true);
        GoToSaveStep(vm);

        var calls = new List<LayoutWizardResult?>();
        vm.CloseRequested += calls.Add;

        vm.SaveCommand.Execute(null);

        Assert.True(File.Exists(Path.Combine(layouts, "slabwing.layout.json")));
        var result = Assert.Single(calls);
        Assert.NotNull(result);
        Assert.Equal("slabwing", result!.Layout.Id);
        Assert.Equal(Path.Combine(root, "config", "slabwing.keymap"), result.KeymapPathToLoad);
    }

    [Fact]
    public async Task Save_AlsoSaveToConfig_WritesSecondCopyIntoScanConfig()
    {
        var root = CreateSlabwingTree();
        var layouts = Path.Combine(root, "layouts");
        var vm = CreateVm(layoutsDirectory: layouts);
        await vm.LoadSourceAsync(root, isFolder: true);
        GoToSaveStep(vm);
        vm.AlsoSaveToConfig = true;

        vm.SaveCommand.Execute(null);

        Assert.True(File.Exists(Path.Combine(layouts, "slabwing.layout.json")));
        Assert.True(File.Exists(Path.Combine(root, "config", "slabwing.layout.json")));
    }

    [Fact]
    public async Task Save_WritesEditedNormalisedKeys()
    {
        var root = CreateSlabwingTree();
        var layouts = Path.Combine(root, "layouts");
        var vm = CreateVm(layoutsDirectory: layouts);
        await vm.LoadSourceAsync(root, isFolder: true);
        vm.NextCommand.Execute(null);

        var editor = vm.Editor!;
        var unedited = editor.Keys;
        // Select key 0 on its rotated centre, then nudge it 4 snap steps right.
        var (cx, cy) = UserLayoutGeometry.RotatedCenter(unedited[0]);
        editor.PointerPressed(cx, cy, additive: false, tolerance: 1);
        editor.PointerReleased();
        Assert.Single(editor.Selection);
        editor.Nudge(4, 0, free: false);

        GoToSaveStep(vm);
        var calls = new List<LayoutWizardResult?>();
        vm.CloseRequested += calls.Add;
        vm.SaveCommand.Execute(null);

        var saved = Assert.Single(calls)!.Layout.Keys;
        // Normalisation shifts every key by the same amount, so only the
        // nudged key's delta moves: exactly 4 × 25 centi-u.
        Assert.Equal(unedited[0].X - unedited[1].X + 100, saved[0].X - saved[1].X);
        var (minX, _, _, _) = UserLayoutGeometry.RotatedBounds(saved);
        Assert.True(minX >= 0 && minX < 1, $"min rotated X was {minX}.");
    }

    [Fact]
    public async Task Save_WritesDtsiNextToEveryCopy_ButNeverAHandWrittenOne()
    {
        var root = CreateSlabwingTree();
        var layouts = Path.Combine(root, "layouts");
        var vm = CreateVm(layoutsDirectory: layouts);
        await vm.LoadSourceAsync(root, isFolder: true);
        Assert.Equal(LayoutSource.InfoJson, vm.SelectedCandidate!.Candidate.Layout.Source);
        GoToSaveStep(vm);
        vm.AlsoSaveToConfig = true;

        vm.SaveCommand.Execute(null);

        // The .dtsi travels with every copy of the layout...
        var appDtsi = Path.Combine(layouts, "slabwing.dtsi");
        var configDtsi = Path.Combine(root, "config", "slabwing.dtsi");
        Assert.True(File.Exists(Path.Combine(layouts, "slabwing.layout.json")));
        Assert.StartsWith(ZmkPhysicalLayoutWriter.GeneratedMarker, File.ReadAllText(appDtsi));
        Assert.StartsWith(ZmkPhysicalLayoutWriter.GeneratedMarker, File.ReadAllText(configDtsi));

        // ...but once a dtsi of that name is the user's own file, saving
        // again leaves it exactly as it is.
        const string handWritten = "/ { };";
        File.WriteAllText(configDtsi, handWritten);
        vm.SaveCommand.Execute(null);

        Assert.Equal(handWritten, File.ReadAllText(configDtsi));
        Assert.StartsWith(ZmkPhysicalLayoutWriter.GeneratedMarker, File.ReadAllText(appDtsi));
    }

    [Fact]
    public async Task Save_SavedLayoutCandidate_ReusesItsIdAndName()
    {
        var root = CreateSlabwingTree();
        var config = Path.Combine(root, "config");
        UserLayoutStore.Save(SavedSlabwingLayout() with { Name = "My Slabwing" }, config);
        var layouts = Path.Combine(root, "layouts");
        // The id is taken already: the same layout lives in app data too.
        var vm = CreateVm(existingIds: ["slabwing"], layoutsDirectory: layouts);
        await vm.LoadSourceAsync(root, isFolder: true);
        Assert.Equal(LayoutSource.UserLayout, vm.SelectedCandidate!.Candidate.Layout.Source);
        Assert.Equal("My Slabwing", vm.Name);
        GoToSaveStep(vm);
        vm.AlsoSaveToConfig = true;

        var calls = new List<LayoutWizardResult?>();
        vm.CloseRequested += calls.Add;
        vm.SaveCommand.Execute(null);

        // Saved back over itself, not as slabwing-2.
        Assert.Equal("slabwing", Assert.Single(calls)!.Layout.Id);
        Assert.Equal(
            ["slabwing.layout.json"],
            Directory.GetFiles(config, "*.layout.json").Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(layouts, "slabwing.layout.json")));
    }

    [Fact]
    public async Task Save_FailedDtsiWrite_LeavesNoLayoutFileBehind()
    {
        var root = CreateSlabwingTree();
        var layouts = Path.Combine(root, "layouts");
        // A directory where the atomic write puts its temp file: the .dtsi
        // write fails after the .layout.json was already written.
        Directory.CreateDirectory(Path.Combine(layouts, "slabwing.dtsi.tmp"));
        var vm = CreateVm(layoutsDirectory: layouts);
        await vm.LoadSourceAsync(root, isFolder: true);
        GoToSaveStep(vm);

        var calls = new List<LayoutWizardResult?>();
        vm.CloseRequested += calls.Add;
        vm.SaveCommand.Execute(null);

        Assert.Empty(calls);
        Assert.NotNull(vm.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(layouts, "slabwing.layout.json")));
        Assert.False(File.Exists(Path.Combine(layouts, "slabwing.dtsi")));
    }

    [Fact]
    public async Task Save_PhysicalLayoutCandidate_WritesNoDtsi()
    {
        var root = CreateTempRoot();
        var dtsi = Path.Combine(root, "cosmos82.dtsi");
        File.WriteAllText(dtsi, LayoutFixtures.Read("cosmos82.dtsi"));
        var layouts = Path.Combine(root, "layouts");
        var vm = CreateVm(layoutsDirectory: layouts);

        await vm.LoadSourceAsync(dtsi, isFolder: false);
        Assert.Equal(LayoutSource.PhysicalLayout, vm.SelectedCandidate!.Candidate.Layout.Source);
        GoToSaveStep(vm);

        var calls = new List<LayoutWizardResult?>();
        vm.CloseRequested += calls.Add;
        vm.SaveCommand.Execute(null);

        var result = Assert.Single(calls);
        Assert.NotNull(result);
        Assert.True(File.Exists(Path.Combine(layouts, result!.Layout.Id + ".layout.json")));
        // The candidate already IS a physical-layout dtsi — no twin export.
        Assert.Empty(Directory.GetFiles(layouts, "*.dtsi"));
    }

    [Fact]
    public async Task Save_ExistingIdInContext_TakesSuffixedId()
    {
        var root = CreateSlabwingTree();
        var layouts = Path.Combine(root, "layouts");
        var vm = CreateVm(existingIds: ["slabwing"], layoutsDirectory: layouts);
        await vm.LoadSourceAsync(root, isFolder: true);
        GoToSaveStep(vm);

        var calls = new List<LayoutWizardResult?>();
        vm.CloseRequested += calls.Add;
        vm.SaveCommand.Execute(null);

        Assert.True(File.Exists(Path.Combine(layouts, "slabwing-2.layout.json")));
        var result = Assert.Single(calls);
        Assert.NotNull(result);
        Assert.Equal("slabwing-2", result!.Layout.Id);
    }

    [Fact]
    public async Task Save_LayoutsDirectoryIsAFile_ReportsErrorAndStaysOpen()
    {
        var root = CreateSlabwingTree();
        var blocked = Path.Combine(root, "blocked");
        WriteFile(root, "blocked", "not a directory");
        var vm = CreateVm(layoutsDirectory: blocked);
        await vm.LoadSourceAsync(root, isFolder: true);
        GoToSaveStep(vm);

        var calls = new List<LayoutWizardResult?>();
        vm.CloseRequested += calls.Add;
        vm.SaveCommand.Execute(null);

        Assert.Empty(calls);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains(
            Loc.Instance["Wizard_SaveFailedFormat"].Split('{')[0], vm.ErrorMessage!);

        // Stepping back clears the message.
        vm.BackCommand.Execute(null);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task SaveCommand_RequiresStepTwoAndNonBlankName()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm();

        Assert.False(vm.SaveCommand.CanExecute(null)); // step 0, nothing scanned

        await vm.LoadSourceAsync(root, isFolder: true);
        Assert.False(vm.SaveCommand.CanExecute(null)); // still step 0
        vm.NextCommand.Execute(null);
        Assert.False(vm.SaveCommand.CanExecute(null)); // step 1 (preview)
        vm.NextCommand.Execute(null);
        Assert.True(vm.SaveCommand.CanExecute(null)); // step 2, Name prefilled

        vm.Name = "  ";
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.Name = "Slabwing";
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.BackCommand.Execute(null);
        Assert.False(vm.SaveCommand.CanExecute(null)); // left step 2 again
    }

    // --- helpers ---

    /// <summary>Walk the step machine the way the user would: source → adjust → save.</summary>
    private static void GoToSaveStep(LayoutWizardViewModel vm)
    {
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
    }

    private static string SourceLabel(LayoutSource source) => source switch
    {
        LayoutSource.UserLayout => Loc.Instance["Wizard_Source_UserLayout"],
        LayoutSource.PhysicalLayout => Loc.Instance["Wizard_Source_PhysicalLayout"],
        LayoutSource.InfoJson => Loc.Instance["Wizard_Source_InfoJson"],
        _ => Loc.Instance["Wizard_Source_MatrixTransform"],
    };

    private LayoutWizardViewModel CreateVm(
        LoadedKeymap? loadedKeymap = null,
        IReadOnlyList<string>? connected = null,
        IReadOnlyList<string>? existingIds = null,
        string? layoutsDirectory = null) =>
        new(new LayoutWizardContext(
            loadedKeymap,
            existingIds ?? Array.Empty<string>(),
            connected ?? Array.Empty<string>(),
            layoutsDirectory ?? Path.Combine(Path.GetTempPath(), "lviz-wizard-layouts")));

    private LoadedKeymap KeymapWithBindings(int count)
    {
        // Round-trip through the real parser; the temp folder is cleaned by Dispose.
        var root = CreateTempRoot();
        var path = Path.Combine(root, "seed.keymap");
        File.WriteAllText(path, KeymapText(count));
        return new LoadedKeymap(ZmkKeymapLoader.Load(path, "wizard-tests"));
    }

    private static string KeymapText(int bindings)
    {
        var cells = string.Join(" ", Enumerable.Repeat("&kp A", bindings));
        return $"/ {{ keymap {{ compatible = \"zmk,keymap\"; base {{ bindings = <{cells}>; }}; }}; }};";
    }

    private string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lviz-wizard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    /// <summary>boards/shields/slabwing + config: overlay, Kconfig, info.json, keymap (56 bindings by default).</summary>
    private string CreateSlabwingTree(int keymapBindings = 56)
    {
        var root = CreateTempRoot();
        WriteFile(root, "boards/shields/slabwing/slabwing.overlay", LayoutFixtures.Read("slabwing.overlay"));
        WriteFile(root, "boards/shields/slabwing/Kconfig.defconfig", """
            if SHIELD_SLABWING_RIGHT
            config ZMK_KEYBOARD_NAME
                default "Slabwing"

            endif
            """);
        WriteFile(root, "config/info.json", LayoutFixtures.Read("slabwing.info.json"));
        WriteFile(root, "config/slabwing.keymap", KeymapText(keymapBindings));
        return root;
    }

    /// <summary>A wizard-saved 56-key slabwing layout: 28 left keys, 28 right keys.</summary>
    private static UserLayoutFile SavedSlabwingLayout()
    {
        var keys = new List<UserLayoutKey>();
        for (var i = 0; i < 56; i++)
            keys.Add(new UserLayoutKey(i * 100, 0, 100, 100, 0, 0, 0, i < 28 ? Hand.Left : Hand.Right));
        return new UserLayoutFile(
            UserLayoutFile.CurrentSchemaVersion, "slabwing", "Slabwing", "Slabwing", keys);
    }

    private static void WriteFile(string root, string relative, string content)
    {
        string full = root;
        foreach (var segment in relative.Split('/'))
            full = Path.Combine(full, segment);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
