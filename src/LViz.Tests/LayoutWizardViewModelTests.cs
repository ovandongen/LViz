using LViz.App.Localization;
using LViz.App.ViewModels;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout.Import;
using LViz.Tests.Layout;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// <see cref="LayoutWizardViewModel"/> source step and navigation: scanning a
/// temp zmk-config tree built from the layout fixtures, keymap choice (loaded
/// vs. scanned vs. unreadable), candidate ranking and labelling, the info
/// lines, name/device-name prefills, and step can-execute rules — plus the
/// preview step (caps, labels, warnings) and saving (layout files, error
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

    // --- preview step ---

    [Fact]
    public async Task Preview_SlabwingKeymap_SplitsTwentyEightTwentyEightAndLabelsBindings()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm();

        await vm.LoadSourceAsync(root, isFolder: true);

        Assert.Equal(28, vm.LeftKeys.Count);
        Assert.Equal(28, vm.RightKeys.Count);
        Assert.All(vm.LeftKeys.Concat(vm.RightKeys), k => Assert.Equal("A", k.Label));
        Assert.Null(vm.PreviewWarning);
    }

    [Fact]
    public async Task Preview_KeymapBiggerThanLayout_WarnsWithBothCounts()
    {
        var root = CreateSlabwingTree(keymapBindings: 58);
        var vm = CreateVm();

        await vm.LoadSourceAsync(root, isFolder: true);

        Assert.Contains(
            Loc.Instance.Format("Wizard_KeyCountMismatchFormat", 56, 58),
            vm.PreviewWarning!);
        // Bindings 56-57 have no cap to sit on; every board key still gets one.
        Assert.Equal(56, vm.LeftKeys.Count + vm.RightKeys.Count);
        Assert.All(vm.LeftKeys.Concat(vm.RightKeys), k => Assert.Equal("A", k.Label));
    }

    [Fact]
    public async Task Preview_NoKeymap_LabelsAreKeyIndices()
    {
        var root = CreateSlabwingTree();
        File.Delete(Path.Combine(root, "config", "slabwing.keymap"));
        var vm = CreateVm();

        await vm.LoadSourceAsync(root, isFolder: true);

        Assert.Null(vm.Keymap);
        var keys = vm.LeftKeys.Concat(vm.RightKeys).ToList();
        Assert.Equal(56, keys.Count);
        Assert.All(keys, k => Assert.Equal(k.Position.Index.ToString(), k.Label));
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

        Assert.NotNull(vm.PreviewWarning);
        Assert.All(rough.Candidate.Layout.Warnings, w => Assert.Contains(w, vm.PreviewWarning!));
    }

    [Fact]
    public async Task Preview_DroppingCandidate_ClearsKeysAndWarning()
    {
        var root = CreateSlabwingTree();
        var vm = CreateVm();
        await vm.LoadSourceAsync(root, isFolder: true);

        vm.SelectedCandidate = null;

        Assert.Empty(vm.LeftKeys);
        Assert.Empty(vm.RightKeys);
        Assert.Null(vm.PreviewWarning);
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

    /// <summary>Walk the step machine the way the user would: source → preview → save.</summary>
    private static void GoToSaveStep(LayoutWizardViewModel vm)
    {
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
    }

    private static string SourceLabel(LayoutSource source) => source switch
    {
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

    private static void WriteFile(string root, string relative, string content)
    {
        string full = root;
        foreach (var segment in relative.Split('/'))
            full = Path.Combine(full, segment);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
