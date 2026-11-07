using System.IO;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Layout;
using LViz.Core.Models;
using LViz.Core.Persistence;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Keymap edit mode on <see cref="MainWindowViewModel"/>: entering and
/// discarding an edit, rendering the edited keymap (literal <c>&amp;trans</c>,
/// changed/selected marks, hidden tabs shown), key selection input, the locks
/// that refuse keymap/board switching while an edit is open, saving —
/// writing the file, staying on the active layer, and the review/conflict
/// callbacks — and the layer operations (add/duplicate/move/rename/delete with
/// the shown-layer decisions, status report and blocker dialog). Uses the real
/// <see cref="KeymapStateService"/> over temp <c>.keymap</c> files and a temp
/// <see cref="KeymapBackupStore"/> root.
/// </summary>
public class MainWindowViewModelEditTests : IDisposable
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    // Returns a canned result and records state the way the real service does
    // (same shape as MainWindowViewModelLoadTests'); the .json case needs it
    // because a real load of a fake path can only fail.
    private sealed class FakeKeymapStateService : IKeymapStateService
    {
        private readonly KeymapLoadResult _result;
        public FakeKeymapStateService(KeymapLoadResult result) => _result = result;

        public LoadedKeymap? Current { get; private set; }
        public bool HasLayout => Current is not null;
        public string? LoadedPath { get; private set; }
        public string? LastLoadError { get; private set; }

        public TransparentBindingKind? ClassifyBinding(int layer, int key)
            => Current?.ClassifyBinding(layer, key);

        public KeymapLoadResult Load(string path, string profileId)
        {
            if (_result is KeymapLoadResult.Success s)
            {
                Current = s.Keymap;
                LoadedPath = path;
                LastLoadError = null;
            }
            return _result;
        }

        public void Clear() { Current = null; LoadedPath = null; }
    }

    /// <summary>
    /// 2 layers ("Base", "Lower") with 42 bindings each — 42 keeps the
    /// default Corne profile, so no load auto-switches the board. Base key 0
    /// is <c>&amp;kp Q</c>, key 1 <c>&amp;kp W</c>, the rest <c>&amp;kp A</c>;
    /// Lower is entirely <c>&amp;trans</c> (it falls through to Base normally,
    /// which is what the edit-mode literal rendering must NOT do).
    /// </summary>
    private static string KeymapText()
    {
        var baseBindings = new List<string> { "&kp Q", "&kp W" };
        baseBindings.AddRange(Enumerable.Repeat("&kp A", 40));
        return $$"""
            / {
                keymap {
                    compatible = "zmk,keymap";

                    base {
                        display-name = "Base";
                        bindings = <{{string.Join(" ", baseBindings)}}>;
                    };

                    lower {
                        display-name = "Lower";
                        bindings = <{{string.Join(" ", Enumerable.Repeat("&trans", 42))}}>;
                    };
                };
            };
            """;
    }

    /// <summary>
    /// Three layers ("Base", "Lower", "Raise") with 42 bindings each — the
    /// fixture for the visible-layer-fallback tests. Bindings are
    /// <see cref="KeymapText"/>'s: Base key 0 <c>&amp;kp Q</c>, key 1
    /// <c>&amp;kp W</c>, the rest <c>&amp;kp A</c>; Lower and Raise are
    /// entirely <c>&amp;trans</c>.
    /// </summary>
    private static string KeymapText3()
    {
        var baseBindings = new List<string> { "&kp Q", "&kp W" };
        baseBindings.AddRange(Enumerable.Repeat("&kp A", 40));
        return $$"""
            / {
                keymap {
                    compatible = "zmk,keymap";

                    base {
                        display-name = "Base";
                        bindings = <{{string.Join(" ", baseBindings)}}>;
                    };

                    lower {
                        display-name = "Lower";
                        bindings = <{{string.Join(" ", Enumerable.Repeat("&trans", 42))}}>;
                    };

                    raise {
                        display-name = "Raise";
                        bindings = <{{string.Join(" ", Enumerable.Repeat("&trans", 42))}}>;
                    };
                };
            };
            """;
    }

    /// <summary>The load tests' tiny 3-key keymap — only its layer name text
    /// matters to the fake service, which reports its own binding count.</summary>
    private static LoadedKeymap SampleKeymap() =>
        new(ZmkKeymapLoader.LoadFromText("""
            / {
                keymap {
                    compatible = "zmk,keymap";
                    default_layer { display-name = "Base"; bindings = <&kp A &kp B &kp C>; };
                    lower         { display-name = "Lower"; bindings = <&kp X &kp Y &kp Z>; };
                };
            };
            """, "test"));

    private readonly string _backupsRoot =
        Path.Combine(Path.GetTempPath(), $"lviz-mainedit-backups-{Guid.NewGuid():N}");
    private readonly List<string> _tempFiles = new();
    private readonly FakeKeymapEditDialogs _dialogs = new();

    private static int Unsaved(MainWindowViewModel vm) => vm.Edit?.ChangeCount ?? 0;

    private static InMemorySettingsService NewSettings(UserSettings? seed = null) =>
        new() { Current = (seed ?? new UserSettings()) with { LiveKeyHighlighting = false } };

    /// <summary>A settings seed hiding the given Corne layer tabs.</summary>
    private static UserSettings HiddenCorneLayers(params int[] layers) => new()
    {
        HiddenLayers = new Dictionary<string, List<int>> { ["Corne"] = layers.ToList() },
    };

    private MainWindowViewModel NewVm(InMemorySettingsService settings, IKeymapStateService? keymapState = null) =>
        new(settings, keymapState: keymapState, keymapBackups: new KeymapBackupStore(_backupsRoot),
            editDialogs: _dialogs);

    /// <summary>Writes a valid 42-key keymap to a fresh temp file.</summary>
    private string WriteTempKeymap(string name = "main-edit", string? text = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lviz-mainedit-{name}-{Guid.NewGuid():N}.keymap");
        File.WriteAllText(path, text ?? KeymapText());
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Loads a valid temp keymap and enters edit mode.</summary>
    private MainWindowViewModel StartEditing(out string path)
    {
        var vm = NewVm(NewSettings());
        path = WriteTempKeymap();
        vm.LoadLayoutFromPath(path);
        vm.StartEditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        return vm;
    }

    /// <summary>Loads the three-layer ("Base", "Lower", "Raise") temp keymap
    /// and enters edit mode.</summary>
    private MainWindowViewModel StartEditing3()
    {
        var vm = NewVm(NewSettings());
        vm.LoadLayoutFromPath(WriteTempKeymap(text: KeymapText3()));
        vm.StartEditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        return vm;
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
            if (File.Exists(file)) File.Delete(file);
        if (Directory.Exists(_backupsRoot)) Directory.Delete(_backupsRoot, recursive: true);
    }

    /// <summary>Taps a key exactly the way the BoardView does — through the
    /// surface delegate, which must be live while editing.</summary>
    private static void Tap(MainWindowViewModel vm, int keyIndex)
    {
        var tap = vm.OnKeyTapped
            ?? throw new InvalidOperationException("expected a key-tap handler while editing");
        tap(keyIndex);
    }

    // ---- entering edit mode ----------------------------------------------------

    [Fact]
    public void StartEdit_BeforeLoad_Disabled_AfterLoad_OpensAndDisablesItself()
    {
        var vm = NewVm(NewSettings());
        Assert.False(vm.CanStartEdit);
        Assert.False(vm.StartEditCommand.CanExecute(null));

        vm.LoadLayoutFromPath(WriteTempKeymap());

        Assert.True(vm.CanStartEdit);
        Assert.True(vm.StartEditCommand.CanExecute(null));
        Assert.Equal("Edit keymap", vm.EditButtonTooltip);

        vm.StartEditCommand.Execute(null);

        Assert.True(vm.IsEditing);
        Assert.NotNull(vm.Edit);
        Assert.False(vm.CanStartEdit);
        Assert.False(vm.StartEditCommand.CanExecute(null));
    }

    [Fact]
    public void StartEdit_FileTurnedToJunk_ToastsAndStaysOutOfEditMode()
    {
        var vm = NewVm(NewSettings());
        var path = WriteTempKeymap();
        vm.LoadLayoutFromPath(path);

        // StartEdit re-reads the file — the on-disk junk is what it parses.
        File.WriteAllText(path, "this is not a keymap {{{");

        vm.StartEditCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Null(vm.Edit);
        Assert.True(vm.Toast.IsVisible);
    }

    // ---- clearing a key ----------------------------------------------------------

    [Fact]
    public void EditMode_TapThenClear_WritesTrans_MarksChangedAndSelected_CountsOne()
    {
        var vm = StartEditing(out _);

        Tap(vm, 0);
        vm.Edit!.ClearSelectedKey(toNone: false);

        Assert.Equal("▽", vm.Keys[0].Label);
        Assert.True(vm.Keys[0].IsEditChanged);
        Assert.True(vm.Keys[0].IsEditSelected);
        Assert.False(vm.Keys[1].IsEditSelected);
        Assert.Equal(1, Unsaved(vm));
    }

    [Fact]
    public void EditMode_SwitchingLayer_DropsTheSelection_ChangedOutlineStaysOnItsLayer()
    {
        var vm = StartEditing(out _);
        vm.LayerTabs.Layers[1].SelectCommand.Execute(null);
        Tap(vm, 0);
        vm.Edit!.ClearSelectedKey(toNone: true);
        Assert.True(vm.Keys[0].IsEditChanged);

        vm.LayerTabs.Layers[0].SelectCommand.Execute(null);

        Assert.False(vm.Keys[0].IsEditChanged);
        Assert.False(vm.Keys[0].IsEditSelected);
        Assert.Null(vm.Edit!.SelectedKeyIndex);

        vm.LayerTabs.Layers[1].SelectCommand.Execute(null);

        Assert.True(vm.Keys[0].IsEditChanged);
        Assert.False(vm.Keys[0].IsEditSelected);
    }

    [Fact]
    public void EditMode_Undo_KeepsTheSelection()
    {
        var vm = StartEditing(out _);
        Tap(vm, 0);
        vm.Edit!.ClearSelectedKey(toNone: false);

        vm.Edit!.UndoCommand.Execute(null);

        Assert.True(vm.Keys[0].IsEditSelected);
    }

    [Fact]
    public void EditMode_LowerLayer_LiteralTransShowsItself_NoFallThroughToBase()
    {
        var vm = StartEditing(out _);

        vm.LayerTabs.Layers[1].SelectCommand.Execute(null);

        // Base key 5 is &kp A; outside edit mode Lower's &trans resolves to it.
        // In edit mode the layer's own binding is what paints.
        Assert.Equal("▽", vm.Keys[5].Label);
        Assert.False(vm.Keys[5].IsEditChanged);
    }

    [Fact]
    public void EditMode_Undo_RestoresBinding_ClearsChangedFlag_AndCount()
    {
        var vm = StartEditing(out _);
        Tap(vm, 0);
        vm.Edit!.ClearSelectedKey(toNone: false);
        Assert.Equal("▽", vm.Keys[0].Label);

        vm.Edit!.UndoCommand.Execute(null);

        Assert.Equal("Q", vm.Keys[0].Label);
        Assert.False(vm.Keys[0].IsEditChanged);
        Assert.Equal(0, Unsaved(vm));
    }

    [Fact]
    public void DiscardEdit_LeavesEditMode_ClearsEveryFlag_AndRepaintsFromLoadedKeymap()
    {
        var vm = StartEditing(out _);
        Tap(vm, 0);
        vm.Edit!.ClearSelectedKey(toNone: false);

        vm.DiscardEditCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Null(vm.Edit);
        Assert.All(vm.Keys, k =>
        {
            Assert.False(k.IsEditChanged);
            Assert.False(k.IsEditSelected);
        });
        // The clear was discarded along with the edit: the loaded file's Q is back.
        Assert.Equal("Q", vm.Keys[0].Label);
    }

    // ---- layer tabs ----------------------------------------------------------------

    [Fact]
    public void EditMode_ShowsHiddenTab_DiscardHidesItAgain_WithoutTouchingSettings()
    {
        var settings = NewSettings(new UserSettings
        {
            HiddenLayers = new Dictionary<string, List<int>> { ["Corne"] = new() { 1 } },
        });
        var vm = NewVm(settings);
        vm.LoadLayoutFromPath(WriteTempKeymap());
        Assert.False(vm.LayerTabs.Layers[1].IsShown);

        vm.StartEditCommand.Execute(null);

        // Edit mode shows every tab and hands each one the layer actions.
        Assert.All(vm.LayerTabs.Layers, l => Assert.True(l.IsShown));
        Assert.All(vm.LayerTabs.Layers, l => Assert.True(l.IsEditable));

        vm.DiscardEditCommand.Execute(null);

        Assert.False(vm.LayerTabs.Layers[1].IsShown);
        // Leaving edit mode rebuilds the plain tabs — no actions again.
        Assert.All(vm.LayerTabs.Layers, l => Assert.False(l.IsEditable));
        // "in memory only, never persisted": the hidden setting survived untouched.
        Assert.Equal(new[] { 1 }, settings.Current.HiddenLayers["Corne"]);
    }

    // ---- visible layer after leaving edit mode ------------------------------------

    /// <summary>Loads the 3-layer fixture with the seeded hidden tabs and
    /// enters edit mode.</summary>
    private MainWindowViewModel StartEditing3(InMemorySettingsService settings)
    {
        var vm = NewVm(settings);
        vm.LoadLayoutFromPath(WriteTempKeymap(text: KeymapText3()));
        Assert.Equal(3, vm.LayerTabs.Layers.Count);
        vm.StartEditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        return vm;
    }

    [Fact]
    public void Discard_OnHiddenLayer_FallsBackToNearestShown_LowerIndexWinsTie()
    {
        var vm = StartEditing3(NewSettings(HiddenCorneLayers(1)));
        vm.LayerTabs.Layers[1].SelectCommand.Execute(null);
        Assert.Equal(1, vm.ActiveLayerIndex);

        vm.DiscardEditCommand.Execute(null);

        // 0 and 2 are both one step away and shown; the tie goes to the lower.
        Assert.Equal(0, vm.ActiveLayerIndex);
    }

    [Fact]
    public void Discard_WithAllHigherLayersHidden_FallsBackToBase()
    {
        var vm = StartEditing3(NewSettings(HiddenCorneLayers(1, 2)));
        vm.LayerTabs.Layers[2].SelectCommand.Execute(null);
        Assert.Equal(2, vm.ActiveLayerIndex);

        vm.DiscardEditCommand.Execute(null);

        Assert.Equal(0, vm.ActiveLayerIndex);
    }

    [Fact]
    public void Discard_WhileOnShownLayer_StaysOnThatLayer()
    {
        var vm = StartEditing3(NewSettings(HiddenCorneLayers(2)));
        vm.LayerTabs.Layers[1].SelectCommand.Execute(null);
        Assert.Equal(1, vm.ActiveLayerIndex);

        vm.DiscardEditCommand.Execute(null);

        // Its tab survived the rebuild — no reason to move.
        Assert.Equal(1, vm.ActiveLayerIndex);
    }

    // ---- locks -----------------------------------------------------------------------

    [Fact]
    public void EditMode_LoadAnotherKeymap_IsRefused()
    {
        var vm = StartEditing(out var path);
        var other = WriteTempKeymap("other");

        vm.LoadLayoutFromPath(other);

        Assert.Equal(path, vm.LoadedLayoutPath);
        Assert.True(vm.IsEditing);
    }

    [Fact]
    public void EditMode_RightClickIsInert_AndRestoredAfterDiscard()
    {
        var vm = NewVm(NewSettings());
        vm.LoadLayoutFromPath(WriteTempKeymap());
        Assert.NotNull(vm.OnKeyRightTapped);

        vm.StartEditCommand.Execute(null);
        Assert.Null(vm.OnKeyRightTapped);

        vm.DiscardEditCommand.Execute(null);
        Assert.NotNull(vm.OnKeyRightTapped);
    }

    // ---- .json cannot be edited ---------------------------------------------------------

    [Fact]
    public void JsonKeymap_CannotStartEdit_TooltipSaysWhy()
    {
        // The real service would refuse a fake path anyway; the fake reports a
        // loaded Moergo .json like MainWindowViewModelLoadTests' successes.
        var fake = new FakeKeymapStateService(new KeymapLoadResult.Success(
            SampleKeymap(), "moergo.json", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = NewVm(NewSettings(), fake);

        vm.LoadLayoutFromPath("moergo.json");

        Assert.False(vm.CanStartEdit);
        Assert.Equal(
            "MoErgo .json keymaps can't be edited. Open the .keymap file instead.",
            vm.EditButtonTooltip);
    }

    // ---- saving ------------------------------------------------------------------------

    /// <summary>The shared opening move of every save test: select key 0 on the
    /// active layer and clear it to <c>&amp;trans</c> — one unsaved change.</summary>
    private static void ClearKeyZero(MainWindowViewModel vm)
    {
        Tap(vm, 0);
        vm.Edit!.ClearSelectedKey(toNone: false);
    }

    /// <summary>Puts the file on disk out of sync with what the edit opened, so
    /// the next save reports a conflict instead of writing.</summary>
    private static void ChangeFileOnDisk(string path) =>
        File.AppendAllText(path, "\n// someone else was here\n");

    [Fact]
    public void SaveEditCommand_WithoutChanges_Disabled_AfterOneChange_Enabled()
    {
        var vm = StartEditing(out _);

        Assert.False(vm.Edit!.SaveEditCommand.CanExecute(null));

        ClearKeyZero(vm);

        Assert.True(vm.Edit!.SaveEditCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveEdit_WritesFile_LeavesEdit_ReportsInStatusBar()
    {
        var vm = StartEditing(out var path);
        var edit = vm.Edit!;
        ClearKeyZero(vm);

        await edit.SaveEditCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        Assert.False(edit.IsSaving);

        // The plan replaced only key 0 of Base; the neighbour survived.
        var saved = File.ReadAllText(path);
        Assert.Contains("&trans", saved);
        Assert.DoesNotContain("&kp Q", saved);
        Assert.Contains("&kp W", saved);

        // The board repaints from the reloaded file, and the edit marks are gone.
        Assert.Equal("▽", vm.Keys[0].Label);
        Assert.False(vm.Keys[0].IsEditChanged);
        Assert.False(vm.Keys[0].IsEditSelected);

        // Success goes to the status bar, not to the toast.
        Assert.Equal(
            Loc.Instance.Format("Status_EditSavedFormat", Path.GetFileName(path)),
            vm.StatusMessage);
        Assert.DoesNotContain("[Key", vm.StatusMessage);
        Assert.Contains(Path.GetFileName(path), vm.StatusMessage);
        Assert.False(vm.Toast.IsVisible);
    }

    [Fact]
    public async Task SaveEdit_ReopensTheLayerThatWasActive()
    {
        var vm = StartEditing(out _);
        ClearKeyZero(vm);
        vm.LayerTabs.Layers[1].SelectCommand.Execute(null);
        Assert.Equal(1, vm.ActiveLayerIndex);

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        // A reload lands on layer 0; the save must put the user back on Lower.
        Assert.Equal(1, vm.ActiveLayerIndex);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public async Task SaveEdit_OnHiddenLayer_FallsBackToNearestShown()
    {
        var vm = StartEditing3(NewSettings(HiddenCorneLayers(1)));
        vm.LayerTabs.Layers[1].SelectCommand.Execute(null);
        // Lower is all &trans — clearing key 0 to &none is the one change.
        Tap(vm, 0);
        vm.Edit!.ClearSelectedKey(toNone: true);
        Assert.Equal(1, Unsaved(vm));

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        // Same rule as on discard: layer 1's tab is hidden again after the
        // reload, and the tie between 0 and 2 goes to the lower index.
        Assert.Equal(0, vm.ActiveLayerIndex);
    }

    [Fact]
    public async Task SaveEdit_KeepsTheLoadedInfoTooltip()
    {
        var vm = StartEditing(out var path);
        ClearKeyZero(vm);

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        // The reload's "Loaded … (2 layers)" base stays up — a save must not
        // reset the tooltip to "No layout loaded"; only the status bar changes.
        Assert.StartsWith(
            Loc.Instance.Format("Status_Loaded", Path.GetFileName(path), 2),
            vm.LoadInfoTooltip);
        Assert.DoesNotContain(Loc.Instance["Status_NoLayoutLoaded"], vm.LoadInfoTooltip);
        Assert.Equal(
            Loc.Instance.Format("Status_EditSavedFormat", Path.GetFileName(path)),
            vm.StatusMessage);
    }

    [Fact]
    public async Task SaveEdit_ReviewDeclined_StaysInEditMode_WritesNothing()
    {
        var vm = StartEditing(out var path);
        var before = File.ReadAllText(path);
        KeymapReviewRequest? captured = null;
        _dialogs.Review = request =>
        {
            captured = request;
            return Task.FromResult<KeymapReviewAnswer?>(null);
        };
        ClearKeyZero(vm);

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditing);
        Assert.Equal(1, Unsaved(vm));
        Assert.Equal(before, File.ReadAllText(path));

        Assert.NotNull(captured);
        Assert.Equal(Path.GetFileName(path), captured!.TargetName);
        // A local save has no commit message to review.
        Assert.Null(captured.CommitMessage);
        Assert.Single(captured.Plan.BindingChanges);
    }

    [Fact]
    public async Task SaveEdit_ConflictCancelled_StaysInEditMode_WritesNothing()
    {
        var vm = StartEditing(out var path);
        ChangeFileOnDisk(path);
        var onDisk = File.ReadAllText(path);
        KeymapConflictRequest? captured = null;
        _dialogs.Conflict = request =>
        {
            captured = request;
            return Task.FromResult(KeymapSaveConflictChoice.Cancel);
        };
        ClearKeyZero(vm);

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditing);
        Assert.Equal(1, Unsaved(vm));
        Assert.Equal(onDisk, File.ReadAllText(path));

        Assert.NotNull(captured);
        Assert.False(captured!.IsGitHub);
        Assert.Equal(Path.GetFileName(path), captured.TargetName);
    }

    [Fact]
    public async Task SaveEdit_ConflictOverwrite_WritesOverTheChangedFile()
    {
        var vm = StartEditing(out var path);
        ChangeFileOnDisk(path);
        _dialogs.Conflict = _ => Task.FromResult(KeymapSaveConflictChoice.Overwrite);
        ClearKeyZero(vm);

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        var saved = File.ReadAllText(path);
        Assert.Contains("&trans", saved);
        Assert.DoesNotContain("&kp Q", saved);
        // Overwrite writes the planned text, so the other party's line is gone.
        Assert.DoesNotContain("someone else", saved);
    }

    [Fact]
    public async Task SaveEdit_ConflictReload_DropsTheEditAndShowsTheDiskVersion()
    {
        var vm = StartEditing(out var path);
        ChangeFileOnDisk(path);
        _dialogs.Conflict = _ => Task.FromResult(KeymapSaveConflictChoice.Reload);
        ClearKeyZero(vm);

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        // Nothing was written and nothing was reloaded from the edit: the file
        // and the board both show what the other party left.
        Assert.Contains("someone else", File.ReadAllText(path));
        Assert.Contains("&kp Q", File.ReadAllText(path));
        Assert.Equal("Q", vm.Keys[0].Label);
    }

    [Fact]
    public async Task SaveEdit_WithoutConflictCallback_TreatsAConflictAsCancel()
    {
        var vm = StartEditing(out var path);
        ChangeFileOnDisk(path);
        ClearKeyZero(vm);

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        // The default fake answers a conflict with cancel: keep editing.
        Assert.True(vm.IsEditing);
        Assert.Equal(1, Unsaved(vm));
        var onDisk = File.ReadAllText(path);
        Assert.Contains("someone else", onDisk);
        Assert.Contains("&kp Q", onDisk);
    }

    [Fact]
    public void DiscardEditCommand_WhileNotEditing_Disabled_AgainWhileSaving_Disabled()
    {
        var vm = NewVm(NewSettings());
        Assert.False(vm.DiscardEditCommand.CanExecute(null));

        vm.LoadLayoutFromPath(WriteTempKeymap());
        vm.StartEditCommand.Execute(null);
        Assert.True(vm.DiscardEditCommand.CanExecute(null));

        vm.Edit!.IsSaving = true;
        Assert.False(vm.DiscardEditCommand.CanExecute(null));
        Assert.False(vm.Edit!.SaveEditCommand.CanExecute(null));
    }

    // ---- the key dialog --------------------------------------------------------------

    private static KeyBinding Kp(string keycode) => new("&kp", new[] { keycode });

    [Fact]
    public async Task KeyDialog_CallbackReturnsBinding_RequestNamesShownKey_ChangeAppliesAsOneStep()
    {
        var vm = StartEditing(out _);
        Tap(vm, 1);
        var configAtOpen = vm.Edit!.Session.Current;

        KeyDialogRequest? captured = null;
        _dialogs.KeyEditor = request =>
        {
            captured = request;
            return Task.FromResult<KeyBinding?>(Kp("B"));
        };

        await vm.Edit!.EditSelectedKeyAsync();

        Assert.NotNull(captured);
        Assert.Equal(vm.ActiveLayerIndex, captured!.LayerIndex);
        Assert.Equal(1, captured.KeyIndex);
        // The dialog reads exactly the config the session held when it opened —
        // records are replaced, never mutated, so it still shows the old binding.
        Assert.Same(configAtOpen, captured.Config);
        var opened = captured.Config.Layers[captured.LayerIndex].Bindings[captured.KeyIndex];
        Assert.Equal("&kp", opened.Behavior);
        Assert.Equal("W", opened.Params[0]);

        // What it answers lands as one unsaved change, and the board repaints.
        var now = vm.Edit!.Session.Current.Layers[0].Bindings[1];
        Assert.Equal("&kp", now.Behavior);
        Assert.Equal("B", now.Params[0]);
        Assert.Equal(1, Unsaved(vm));
        Assert.Equal("B", vm.Keys[1].Label);
    }

    [Fact]
    public async Task KeyDialog_CallbackReturnsNull_ChangesNothing()
    {
        var vm = StartEditing(out _);
        Tap(vm, 1);
        _dialogs.KeyEditor = _ => Task.FromResult<KeyBinding?>(null);

        await vm.Edit!.EditSelectedKeyAsync();

        Assert.Equal(0, Unsaved(vm));
        Assert.False(vm.Edit!.CanUndo);
        Assert.Equal("W", vm.Keys[1].Label);
    }

    [Fact]
    public async Task KeyDialog_NoSelection_CallbackNotCalled()
    {
        var vm = StartEditing(out _);
        var calls = 0;
        _dialogs.KeyEditor = _ =>
        {
            calls++;
            return Task.FromResult<KeyBinding?>(Kp("B"));
        };

        await vm.Edit!.EditSelectedKeyAsync();

        Assert.Equal(0, calls);
        Assert.Equal(0, Unsaved(vm));
    }


    [Fact]
    public async Task KeyDialog_ActiveLowerLayer_RequestNamesLayerOne_ChangeLandsThere()
    {
        var vm = StartEditing(out _);
        vm.LayerTabs.Layers[1].SelectCommand.Execute(null);
        Tap(vm, 3);

        KeyDialogRequest? captured = null;
        _dialogs.KeyEditor = request =>
        {
            captured = request;
            return Task.FromResult<KeyBinding?>(Kp("B"));
        };

        await vm.Edit!.EditSelectedKeyAsync();

        Assert.Equal(1, captured!.LayerIndex);
        Assert.Equal(1, vm.ActiveLayerIndex);
        var now = vm.Edit!.Session.Current.Layers[1].Bindings[3];
        Assert.Equal("&kp", now.Behavior);
        Assert.Equal("B", now.Params[0]);
        // The same key on Base is untouched.
        Assert.Equal("A", vm.Edit.Session.Current.Layers[0].Bindings[3].Params[0]);
    }

    [Fact]
    public async Task KeyDialog_SecondCallWhileDialogOpen_IgnoredUntilFirstCloses()
    {
        var vm = StartEditing(out _);
        Tap(vm, 1);

        var gate = new TaskCompletionSource<KeyBinding?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _dialogs.KeyEditor = _ =>
        {
            calls++;
            return gate.Task;
        };

        var first = vm.Edit!.EditSelectedKeyAsync();
        Assert.Equal(1, calls);

        await vm.Edit!.EditSelectedKeyAsync();
        Assert.Equal(1, calls);

        gate.SetResult(Kp("B"));
        await first;

        Assert.Equal(1, calls);
        Assert.Equal(1, Unsaved(vm));
        Assert.Equal("B", vm.Edit!.Session.Current.Layers[0].Bindings[1].Params[0]);
    }

    [Fact]
    public void KeyDialog_DoubleTapSelectsTheKeyAndOpensTheDialog_NullWhenNotEditing()
    {
        var vm = NewVm(NewSettings());
        Assert.Null(vm.OnKeyDoubleTapped);

        vm.LoadLayoutFromPath(WriteTempKeymap());
        vm.StartEditCommand.Execute(null);

        KeyDialogRequest? captured = null;
        _dialogs.KeyEditor = request =>
        {
            captured = request;
            return Task.FromResult<KeyBinding?>(null);
        };

        vm.OnKeyDoubleTapped!.Invoke(1);

        Assert.Equal(1, vm.Edit!.SelectedKeyIndex);
        Assert.True(vm.Keys[1].IsEditSelected);
        Assert.NotNull(captured);
        Assert.Equal(1, captured!.KeyIndex);
    }

    [Fact]
    public void RingSetting_DefaultsOn_AndPersists_ForTheDialogHostToRead()
    {
        // Ring versus dialog is the dialog host's call (AvaloniaKeymapEditDialogs
        // reads RingKeyEditor from the settings service on every open), so the
        // setting must reach the settings service the moment it changes.
        var settings = NewSettings();
        var vm = NewVm(settings);
        Assert.True(vm.Preferences.UseRingKeyEditor);
        Assert.True(settings.Current.RingKeyEditor);

        vm.Preferences.UseRingKeyEditor = false;

        Assert.False(settings.Current.RingKeyEditor);
    }

    // ---- the Settings keyboard picker lock ------------------------------------------

    [Fact]
    public void SettingsVM_Editing_CanPickKeyboardFalse_RaisesAndTurnsTrueOnDiscard()
    {
        var settings = NewSettings();
        var vm = NewVm(settings);
        vm.LoadLayoutFromPath(WriteTempKeymap());
        vm.StartEditCommand.Execute(null);
        var settingsVm = new SettingsViewModel(settings, vm, new UpdateService(settings));

        Assert.False(settingsVm.CanPickKeyboard);
        var raised = new List<string?>();
        settingsVm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.DiscardEditCommand.Execute(null);

        // The main VM's IsEditing change propagates through _mainPropagators.
        Assert.Contains(nameof(SettingsViewModel.CanPickKeyboard), raised);
        Assert.True(settingsVm.CanPickKeyboard);
    }

    [Fact]
    public void SettingsVM_Editing_AssigningProfile_LeavesMainVmOnItsBoard()
    {
        var settings = NewSettings();
        var vm = NewVm(settings);
        vm.LoadLayoutFromPath(WriteTempKeymap());
        vm.StartEditCommand.Execute(null);
        var settingsVm = new SettingsViewModel(settings, vm, new UpdateService(settings));

        var active = vm.SelectedKeyboard!;
        var other = KeyboardProfileRegistry.All.First(p => p.Id != active.Id);

        settingsVm.SelectedKeyboardProfile = other;

        // The switch is refused and the combo is re-raised so it snaps back.
        Assert.Same(active, vm.SelectedKeyboard);
        Assert.Equal(active, settingsVm.SelectedKeyboardProfile);
    }

    // ---- layer operations ---------------------------------------------------------

    private static List<string> LayerNames(MainWindowViewModel vm) =>
        vm.Edit!.Session.Current.Layers.Select(l => l.Name).ToList();

    [Fact]
    public void NewLayer_AppendsShowsCounts_UndoOfAddClampsShownLayer()
    {
        var vm = StartEditing(out _);

        Assert.True(vm.Edit!.NewLayerCommand.CanExecute(null));
        vm.Edit!.NewLayerCommand.Execute(null);

        Assert.Equal(3, vm.Edit!.Session.Current.Layers.Count);
        Assert.Equal(2, vm.ActiveLayerIndex);
        Assert.Equal(1, Unsaved(vm));

        // Undoing the add removes the shown layer — the board falls back to
        // the last remaining one instead of painting nothing.
        vm.Edit.UndoCommand.Execute(null);

        Assert.Equal(2, vm.Edit.Session.Current.Layers.Count);
        Assert.Equal(1, vm.ActiveLayerIndex);
    }

    [Fact]
    public void DuplicateShownLayer_OnUpper_CopiesAfterItAndShowsTheCopy()
    {
        var vm = StartEditing(out _);
        vm.LayerTabs.Layers[1].SelectCommand.Execute(null);

        vm.Edit!.DuplicateShownLayerCommand.Execute(null);

        Assert.Equal("Lower copy", LayerNames(vm)[2]);
        Assert.Equal(2, vm.ActiveLayerIndex);
    }

    [Fact]
    public void MoveShownLayerRight_MovesItStaysShown_EndsDisableTheMove()
    {
        var vm = StartEditing3();
        vm.LayerTabs.Layers[1].SelectCommand.Execute(null); // Lower

        vm.Edit!.MoveShownLayerRightCommand.Execute(null);

        Assert.Equal(new[] { "Base", "Raise", "Lower" }, LayerNames(vm));
        Assert.Equal(2, vm.ActiveLayerIndex);
        Assert.False(vm.Edit!.MoveShownLayerRightCommand.CanExecute(null));
        Assert.True(vm.Edit!.MoveShownLayerLeftCommand.CanExecute(null));

        vm.LayerTabs.Layers[0].SelectCommand.Execute(null);
        Assert.False(vm.Edit!.MoveShownLayerLeftCommand.CanExecute(null));
        Assert.True(vm.Edit!.MoveShownLayerRightCommand.CanExecute(null));
    }

    [Fact]
    public void UndoAndRedoOfAMove_TheBoardFollowsTheLayer()
    {
        var vm = StartEditing3();
        vm.LayerTabs.Layers[1].SelectCommand.Execute(null); // Lower
        vm.Edit!.MoveShownLayerRightCommand.Execute(null);
        Assert.Equal(2, vm.ActiveLayerIndex);

        vm.Edit!.UndoCommand.Execute(null);
        Assert.Equal(new[] { "Base", "Lower", "Raise" }, LayerNames(vm));
        Assert.Equal(1, vm.ActiveLayerIndex);

        vm.Edit!.RedoCommand.Execute(null);
        Assert.Equal(new[] { "Base", "Raise", "Lower" }, LayerNames(vm));
        Assert.Equal(2, vm.ActiveLayerIndex);
    }

    [Fact]
    public void UndoOfAMove_WhileAnotherLayerIsShown_KeepsShowingThatLayer()
    {
        var vm = StartEditing3();
        vm.Edit!.MoveLayer(0, 0 + (2));                       // Lower, Raise, Base — Base shown at 2
        vm.LayerTabs.Layers[1].SelectCommand.Execute(null); // Raise

        vm.Edit!.UndoCommand.Execute(null);       // Base, Lower, Raise

        Assert.Equal(2, vm.ActiveLayerIndex);     // still Raise
    }

    [Fact]
    public async Task RenameLayer_CallbackAnswers_RequestCarriesOldName_NewNameLands()
    {
        var vm = StartEditing3();
        RenameLayerRequest? captured = null;
        _dialogs.Rename = request =>
        {
            captured = request;
            return Task.FromResult<string?>("Nav");
        };

        await vm.Edit!.RenameLayerAsync(1);

        Assert.NotNull(captured);
        Assert.Equal(1, captured!.LayerIndex);
        Assert.Equal("Lower", captured.CurrentName);
        Assert.Equal("Nav", LayerNames(vm)[1]);
    }

    [Fact]
    public async Task RenameLayer_CallbackCancels_ChangesNothing()
    {
        var vm = StartEditing3();
        _dialogs.Rename = _ => Task.FromResult<string?>(null);

        await vm.Edit!.RenameLayerAsync(1);

        Assert.Equal("Lower", LayerNames(vm)[1]);
        Assert.Equal(0, Unsaved(vm));
    }

    [Fact]
    public async Task DeleteLayer_Allowed_DeletesShowsPrevious_ReportsInStatusBar()
    {
        var vm = StartEditing3();
        var blockedCalled = false;
        _dialogs.DeleteBlocked = _ =>
        {
            blockedCalled = true;
            return Task.CompletedTask;
        };

        await vm.Edit!.DeleteLayerAsync(1); // "Lower", unreferenced

        Assert.False(blockedCalled);
        Assert.Equal(new[] { "Base", "Raise" }, LayerNames(vm));
        Assert.Equal(0, vm.ActiveLayerIndex);
        Assert.Equal(Loc.Instance.Format("Edit_LayerDeletedNoRefsFormat", "Lower"), vm.StatusMessage);
        Assert.DoesNotContain("[Key", vm.StatusMessage);
    }

    [Fact]
    public async Task DeleteLayer_LastLayerRemaining_BlockedDialogNamesIt_NothingChanges()
    {
        var vm = StartEditing(out _); // "Base", "Lower"
        LayerDeleteBlockedRequest? blocked = null;
        _dialogs.DeleteBlocked = request =>
        {
            blocked = request;
            return Task.CompletedTask;
        };

        await vm.Edit!.DeleteLayerAsync(1); // allowed — Lower goes first
        Assert.Equal("Base", Assert.Single(LayerNames(vm)));

        await vm.Edit!.DeleteLayerAsync(0); // Base is now the only layer

        Assert.NotNull(blocked);
        Assert.Equal("Base", blocked!.LayerName);
        var blocker = Assert.Single(blocked.Blockers);
        Assert.Equal(LayerDeleteBlockerKind.LastLayer, blocker.Kind);
        Assert.Equal("Base", Assert.Single(LayerNames(vm)));
        Assert.Equal(0, vm.ActiveLayerIndex);
    }

    [Fact]
    public void LayerCommands_AfterTheEditClosed_AllDisabled_AddLayerDoesNothing()
    {
        var vm = StartEditing(out _);
        var edit = vm.Edit!;
        vm.DiscardEditCommand.Execute(null);
        Assert.Null(vm.Edit);

        Assert.False(edit.NewLayerCommand.CanExecute(null));
        Assert.False(edit.DuplicateShownLayerCommand.CanExecute(null));
        Assert.False(edit.MoveShownLayerLeftCommand.CanExecute(null));
        Assert.False(edit.MoveShownLayerRightCommand.CanExecute(null));
        Assert.False(edit.RenameShownLayerCommand.CanExecute(null));
        Assert.False(edit.DeleteShownLayerCommand.CanExecute(null));

        Assert.Null(edit.AddLayer());

        Assert.Equal(2, edit.Session.Current.Layers.Count);
    }

    // ---- the layer tabs follow the edit ---------------------------------------------

    [Fact]
    public void AddLayer_RebuildsTabs_NewTabShownSelectedWithDefaultPositionColor()
    {
        var vm = StartEditing(out _);

        vm.Edit!.AddLayer();

        Assert.Equal(3, vm.LayerTabs.Layers.Count);
        Assert.Equal("2 : Layer 2", vm.LayerTabs.Layers[2].DisplayName);
        Assert.True(vm.LayerTabs.Layers[2].IsSelected);
        Assert.False(vm.LayerTabs.Layers[0].IsSelected);
        // A new layer has no baseline, so it gets the palette default of its
        // position rather than any override.
        Assert.Equal(LayerColorPalette.GetDefaultColor(2), vm.LayerTabs.Layers[2].TabColor);
    }

    [Fact]
    public void MoveLayer_TabColorFollowsTheLayer()
    {
        // The VM seeds the static palette from these settings at construction —
        // layer 1 owns a custom colour and nothing else is touched.
        var settings = NewSettings(new UserSettings
        {
            LayerColors = new Dictionary<string, Dictionary<int, string>>
            {
                ["Corne"] = new() { [1] = "#123456" },
            },
        });
        var vm = NewVm(settings);
        vm.LoadLayoutFromPath(WriteTempKeymap(text: KeymapText3()));
        vm.StartEditCommand.Execute(null);
        Assert.Equal("#123456", vm.LayerTabs.Layers[1].TabColor);

        vm.Edit!.MoveLayer(1, 1 + (1)); // Base, Raise, Lower

        Assert.Equal("2 : Lower", vm.LayerTabs.Layers[2].DisplayName);
        // The colour travels with the layer it belongs to…
        Assert.Equal("#123456", vm.LayerTabs.Layers[2].TabColor);
        // …and the vacated position reads as the layer now standing there.
        Assert.Equal(LayerColorPalette.GetDefaultColor(2), vm.LayerTabs.Layers[1].TabColor);
    }

    [Fact]
    public void Rename_InSession_TabCaptionFollowsAtOnce()
    {
        var vm = StartEditing3();

        vm.Edit!.RenameLayer(1, "Nav");

        Assert.Equal("1 : Nav", vm.LayerTabs.Layers[1].DisplayName);
    }

    [Fact]
    public void KeyEdit_KeepsTheSameTabInstances()
    {
        var vm = StartEditing(out _);
        var tabs = vm.LayerTabs.Layers.ToList();

        Tap(vm, 0);
        vm.Edit!.ClearSelectedKey(toNone: false);

        // A plain key edit must not replace the tab buttons — only structural
        // changes rebuild the strip.
        Assert.Equal(tabs.Count, vm.LayerTabs.Layers.Count);
        for (var i = 0; i < tabs.Count; i++)
            Assert.Same(tabs[i], vm.LayerTabs.Layers[i]);
    }

    [Fact]
    public void NotEditing_TabsCarryNoActions()
    {
        var vm = NewVm(NewSettings());
        vm.LoadLayoutFromPath(WriteTempKeymap());

        Assert.Equal(2, vm.LayerTabs.Layers.Count);
        Assert.All(vm.LayerTabs.Layers, tab =>
        {
            Assert.False(tab.IsEditable);
            Assert.False(tab.RenameCommand.CanExecute(null));
            Assert.False(tab.DuplicateCommand.CanExecute(null));
            Assert.False(tab.MoveLeftCommand.CanExecute(null));
            Assert.False(tab.MoveRightCommand.CanExecute(null));
            Assert.False(tab.DeleteCommand.CanExecute(null));
        });
    }

    // ---- per-layer settings follow a save that moved layers -------------------------

    /// <summary>A Corne seed with layer 1 coloured, layer 2 hidden and one
    /// app→layer rule on layer 1 — everything a layer move must carry along.</summary>
    private static UserSettings LayerScopedCorneSettings() => new()
    {
        LayerColors = new Dictionary<string, Dictionary<int, string>>
        {
            ["Corne"] = new() { [1] = "#123456" },
        },
        HiddenLayers = new Dictionary<string, List<int>> { ["Corne"] = new() { 2 } },
        AppLayerRules = new Dictionary<string, List<AppLayerRule>>
        {
            ["Corne"] = new() { new AppLayerRule("code", 1) },
        },
    };

    /// <summary>The seeded per-layer settings, asserted unchanged.</summary>
    private static void AssertSettingsUnchanged(UserSettings saved)
    {
        Assert.Equal(new Dictionary<int, string> { [1] = "#123456" }, saved.LayerColors["Corne"]);
        Assert.Equal(new[] { 2 }, saved.HiddenLayers["Corne"]);
        Assert.Equal(new AppLayerRule("code", 1), Assert.Single(saved.AppLayerRules["Corne"]));
    }

    [Fact]
    public async Task SaveEdit_MovedLayers_SettingsTravelWithTheirLayer()
    {
        var settings = NewSettings(LayerScopedCorneSettings());
        var vm = StartEditing3(settings);
        IReadOnlyList<string>? shown = null;
        _dialogs.Notices = lines =>
        {
            shown = lines;
            return Task.CompletedTask;
        };

        vm.Edit!.MoveLayer(1, 1 + (1)); // Base, Raise, Lower

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        var saved = settings.Current;
        Assert.Equal(new Dictionary<int, string> { [2] = "#123456" }, saved.LayerColors["Corne"]);
        Assert.Equal(new[] { 1 }, saved.HiddenLayers["Corne"]);
        Assert.Equal(new AppLayerRule("code", 2), Assert.Single(saved.AppLayerRules["Corne"]));
        // The in-memory engine list follows the persisted rules.
        Assert.Equal(new AppLayerRule("code", 2), Assert.Single(vm.AutoSwitch.AppLayerRules));
        // The reloaded tabs read the remapped palette.
        Assert.Equal("2 : Lower", vm.LayerTabs.Layers[2].DisplayName);
        Assert.Equal("#123456", vm.LayerTabs.Layers[2].TabColor);
        // Nothing was dropped — nothing is reported.
        Assert.Null(shown);
    }

    [Fact]
    public async Task SaveEdit_DeletedRuleLayer_RemovesTheRuleAndReportsIt()
    {
        var settings = NewSettings(new UserSettings
        {
            AppLayerRules = new Dictionary<string, List<AppLayerRule>>
            {
                ["Corne"] = new() { new AppLayerRule("code", 1) },
            },
        });
        var vm = StartEditing3(settings);
        IReadOnlyList<string>? shown = null;
        _dialogs.Notices = lines =>
        {
            shown = lines;
            return Task.CompletedTask;
        };

        await vm.Edit!.DeleteLayerAsync(1); // "Lower", unreferenced

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        // The rule aimed at the deleted layer is gone, and one line says so.
        Assert.False(settings.Current.AppLayerRules.ContainsKey("Corne"));
        Assert.Equal(
            Loc.Instance.Format("Edit_NoticeAppRuleRemovedFormat", "code", 1),
            Assert.Single(shown!));
    }

    [Fact]
    public async Task SaveEdit_KeyChangeOnly_LeavesSettingsAndCallbackUntouched()
    {
        var settings = NewSettings(LayerScopedCorneSettings());
        var vm = StartEditing3(settings);
        IReadOnlyList<string>? shown = null;
        _dialogs.Notices = lines =>
        {
            shown = lines;
            return Task.CompletedTask;
        };

        ClearKeyZero(vm);

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        // Only bindings were written — the layer numbers never moved.
        AssertSettingsUnchanged(settings.Current);
        Assert.Null(shown);
    }

    [Fact]
    public async Task SaveEdit_MoveAndMoveBack_LeavesSettingsUntouched()
    {
        var settings = NewSettings(LayerScopedCorneSettings());
        var vm = StartEditing3(settings);
        IReadOnlyList<string>? shown = null;
        _dialogs.Notices = lines =>
        {
            shown = lines;
            return Task.CompletedTask;
        };

        vm.Edit!.MoveLayer(1, 1 + (1));
        vm.Edit!.MoveLayer(2, 2 + (-1)); // back to the baseline order
        vm.LayerTabs.Layers[0].SelectCommand.Execute(null);
        ClearKeyZero(vm);

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        // The layer list is back to the baseline, so there was nothing to move.
        AssertSettingsUnchanged(settings.Current);
        Assert.Null(shown);
    }

    [Fact]
    public void DiscardEdit_AfterMove_LeavesSettingsUntouched()
    {
        var settings = NewSettings(LayerScopedCorneSettings());
        var vm = StartEditing3(settings);

        vm.Edit!.MoveLayer(1, 1 + (1));
        vm.DiscardEditCommand.Execute(null);

        Assert.False(vm.IsEditing);
        // Only a save moves settings — a discarded move never does.
        AssertSettingsUnchanged(settings.Current);
    }

    // ---- combo legend and combo actions while editing ----------------------

    /// <summary>A dialog answer: the combo on <paramref name="keys"/> fires
    /// <c>&amp;kp ESC</c> on every layer, node name <c>combo_ESC</c>.</summary>
    private static ZmkCombo EscCombo(IReadOnlyList<int> keys) =>
        new("ESC", "", keys, [-1], new KeyBinding("&kp", ["ESC"])) { NodeName = "combo_ESC" };

    /// <summary>A review callback answering go-ahead, so the save proceeds.</summary>
    private static Task<KeymapReviewAnswer?> GoAhead(KeymapReviewRequest _) =>
        Task.FromResult<KeymapReviewAnswer?>(new KeymapReviewAnswer(null));

    [Fact]
    public void IsComboLegendVisible_ForcedOnWhileEditing_OffAgainAfterDiscard()
    {
        var raised = new List<string?>();
        var vm = NewVm(NewSettings());
        vm.ComboLegend.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.LoadLayoutFromPath(WriteTempKeymap());

        Assert.False(vm.ComboLegend.IsOverlayVisible); // the persisted toggle is off
        vm.StartEditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        Assert.True(vm.ComboLegend.IsLegendVisible);   // edit mode shows the legend anyway
        Assert.Contains(nameof(ComboLegendViewModel.IsLegendVisible), raised);

        vm.DiscardEditCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.False(vm.ComboLegend.IsLegendVisible);
    }

    [Fact]
    public async Task NewCombo_Answer_AddsEditableChangedTile_AndCarriesRequest()
    {
        var vm = StartEditing(out _);
        ComboDialogRequest? captured = null;
        _dialogs.Combo = request =>
        {
            captured = request;
            return Task.FromResult<ZmkCombo?>(EscCombo([0, 1]));
        };

        await vm.Edit!.NewComboCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Same(vm.SelectedKeyboard, captured!.Profile);
        Assert.Null(captured.ComboIndex);
        Assert.Equal(vm.ActiveLayerIndex, captured.ShownLayerIndex);

        Assert.Single(vm.ComboLegend.Combos);
        Assert.True(vm.ComboLegend.Combos[0].IsEditable);
        Assert.True(vm.ComboLegend.Combos[0].IsChanged);
        Assert.Equal(1, Unsaved(vm));
        Assert.Single(vm.Edit!.Session.Current.Combos);
    }

    [Fact]
    public async Task ComboCommands_AfterTheEditClosed_DoNothing()
    {
        var vm = StartEditing(out _);
        var edit = vm.Edit!;

        // The tile numbers are out of range of an empty combo list.
        await edit.EditComboCommand.ExecuteAsync(1);
        Assert.Empty(vm.ComboLegend.Combos);
        Assert.Equal(0, Unsaved(vm));
        Assert.Empty(_dialogs.ComboRequests);

        vm.DiscardEditCommand.Execute(null);
        Assert.False(edit.NewComboCommand.CanExecute(null));
        await edit.NewComboCommand.ExecuteAsync(null); // a closed edit: nothing, no dialog, no throw
        edit.DeleteComboCommand.Execute(1);
        Assert.Empty(vm.ComboLegend.Combos);
        Assert.Empty(_dialogs.ComboRequests);
    }

    [Fact]
    public async Task NewCombo_SecondRequestWhileDialogOpen_IsIgnored()
    {
        var vm = StartEditing(out _);
        var gate = new TaskCompletionSource<ZmkCombo?>();
        var calls = 0;
        _dialogs.Combo = _ =>
        {
            calls++;
            return gate.Task;
        };

        var first = vm.Edit!.NewComboCommand.ExecuteAsync(null);
        await vm.Edit!.NewComboCommand.ExecuteAsync(null); // guard: returns at once
        Assert.Equal(1, calls);

        gate.SetResult(EscCombo([0, 1]));
        await first;
        Assert.Single(vm.ComboLegend.Combos);
    }

    [Fact]
    public async Task EditCombo_Answer_ReplacesBinding_NullAndOutOfRange_ChangeNothing()
    {
        var vm = StartEditing(out _);
        _dialogs.Combo = _ => Task.FromResult<ZmkCombo?>(EscCombo([0, 1]));
        await vm.Edit!.NewComboCommand.ExecuteAsync(null);
        var original = vm.Edit!.Session.Current.Combos[0];

        var changed = original with { Binding = new KeyBinding("&kp", ["TAB"]) };
        ComboDialogRequest? captured = null;
        _dialogs.Combo = request =>
        {
            captured = request;
            return Task.FromResult<ZmkCombo?>(changed);
        };
        await vm.Edit!.EditComboCommand.ExecuteAsync(1);

        Assert.NotNull(captured);
        Assert.Equal(0, captured!.ComboIndex);
        Assert.Equal(vm.ActiveLayerIndex, captured.ShownLayerIndex);
        Assert.Same(changed.Binding, vm.Edit!.Session.Current.Combos[0].Binding);

        // A cancelled dialog (null) changes nothing.
        _dialogs.Combo = _ => Task.FromResult<ZmkCombo?>(null);
        await vm.Edit!.EditComboCommand.ExecuteAsync(1);
        Assert.Same(changed.Binding, vm.Edit.Session.Current.Combos[0].Binding);

        // An out-of-range tile number never even reaches the callback.
        var called = false;
        _dialogs.Combo = _ =>
        {
            called = true;
            return Task.FromResult<ZmkCombo?>(changed);
        };
        await vm.Edit!.EditComboCommand.ExecuteAsync(2);
        Assert.False(called);
        Assert.Same(changed.Binding, vm.Edit.Session.Current.Combos[0].Binding);
    }

    [Fact]
    public async Task DeleteCombo_RemovesTheAddedTile_NetsBackToZero()
    {
        var vm = StartEditing(out _);
        _dialogs.Combo = _ => Task.FromResult<ZmkCombo?>(EscCombo([0, 1]));
        await vm.Edit!.NewComboCommand.ExecuteAsync(null);
        Assert.Single(vm.ComboLegend.Combos);
        Assert.Equal(1, Unsaved(vm));

        vm.Edit!.DeleteComboCommand.Execute(1);

        // The session's change event rebuilds the legend, and add-plus-delete
        // nets against the baseline.
        Assert.Empty(vm.ComboLegend.Combos);
        Assert.Equal(0, Unsaved(vm));

        vm.Edit!.DeleteComboCommand.Execute(1); // out of range: nothing, no throw
        vm.Edit!.DeleteComboCommand.Execute(0);
        Assert.Empty(vm.ComboLegend.Combos);
    }

    [Fact]
    public async Task ComboEditAndDelete_DoNothingWhileASaveIsInFlight()
    {
        var vm = StartEditing(out _);
        _dialogs.Combo = _ => Task.FromResult<ZmkCombo?>(EscCombo([0, 1]));
        await vm.Edit!.NewComboCommand.ExecuteAsync(null);
        Assert.Single(vm.ComboLegend.Combos);

        var opened = 0;
        _dialogs.Combo = _ => { opened++; return Task.FromResult<ZmkCombo?>(EscCombo([0, 2])); };
        vm.Edit!.IsSaving = true;

        // The save plan is already built: an edit now would be lost.
        vm.Edit!.DeleteComboCommand.Execute(1);
        await vm.Edit!.EditComboCommand.ExecuteAsync(1);

        Assert.Single(vm.Edit!.Session.Current.Combos);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task NewCombo_Undo_EmptiesTheLegendAgain()
    {
        var vm = StartEditing(out _);
        _dialogs.Combo = _ => Task.FromResult<ZmkCombo?>(EscCombo([0, 1]));
        await vm.Edit!.NewComboCommand.ExecuteAsync(null);
        Assert.Single(vm.ComboLegend.Combos);

        vm.Edit!.UndoCommand.Execute(null);

        Assert.Empty(vm.ComboLegend.Combos);
        Assert.Equal(0, Unsaved(vm));
    }

    [Fact]
    public async Task SaveEdit_AfterComboAdd_WritesComboNode_ReloadsOnePlainTile()
    {
        var vm = StartEditing(out var path);
        _dialogs.Review = GoAhead;
        _dialogs.Combo = _ => Task.FromResult<ZmkCombo?>(EscCombo([0, 1]));
        await vm.Edit!.NewComboCommand.ExecuteAsync(null);

        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        var saved = File.ReadAllText(path);
        Assert.Contains("combo_ESC", saved);
        Assert.Single(ZmkKeymapLoader.LoadFromText(saved, "test").Combos);

        // Out of edit mode the legend is the reloaded file's combo — no
        // edit-mode flags left on the tile.
        Assert.Single(vm.ComboLegend.Combos);
        Assert.False(vm.ComboLegend.Combos[0].IsEditable);
        Assert.False(vm.ComboLegend.Combos[0].IsChanged);
    }

    [Fact]
    public async Task SaveEdit_MovesComboLabelWithCombo_ClearsItOnDelete()
    {
        var settings = NewSettings();
        var vm = NewVm(settings);
        var path = WriteTempKeymap();
        vm.LoadLayoutFromPath(path);
        _dialogs.Review = GoAhead;

        // Add the combo on keys 0+1 and get it into the file.
        vm.StartEditCommand.Execute(null);
        _dialogs.Combo = _ => Task.FromResult<ZmkCombo?>(EscCombo([0, 1]));
        await vm.Edit!.NewComboCommand.ExecuteAsync(null);
        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        vm.ComboLegend.SetLabelOverride("0,1", new ComboLabelOverride { MainLabel = "Esc" });
        Assert.NotNull(ComboLabelOverrides.Get("Corne", "0,1"));

        // Move it to keys 0+2 — the label follows the combo to the new key.
        vm.StartEditCommand.Execute(null);
        _dialogs.Combo = _ => Task.FromResult<ZmkCombo?>(EscCombo([0, 2]));
        await vm.Edit!.EditComboCommand.ExecuteAsync(1);
        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        Assert.NotNull(ComboLabelOverrides.Get("Corne", "0,2"));
        Assert.Null(ComboLabelOverrides.Get("Corne", "0,1"));
        Assert.True(settings.Current.ComboLabelOverrides.TryGetValue("Corne", out var stored)
            && stored.ContainsKey("0,2") && !stored.ContainsKey("0,1"));

        // Delete the combo — its label goes with it.
        vm.StartEditCommand.Execute(null);
        vm.Edit!.DeleteComboCommand.Execute(1);
        await vm.Edit!.SaveEditCommand.ExecuteAsync(null);

        Assert.Null(ComboLabelOverrides.Get("Corne", "0,2"));
        Assert.False(settings.Current.ComboLabelOverrides.TryGetValue("Corne", out var left)
            && left.ContainsKey("0,2"));
    }
}
