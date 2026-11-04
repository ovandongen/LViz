using System.IO;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using LViz.Core.Persistence;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Keymap edit mode on <see cref="MainWindowViewModel"/>: entering and
/// discarding an edit, rendering the edited keymap (literal <c>&amp;trans</c>,
/// changed/selected marks, hidden tabs shown), key selection input, the locks
/// that refuse keymap/board switching while an edit is open, and saving —
/// writing the file, staying on the active layer, and the review/conflict
/// callbacks. Uses the real <see cref="KeymapStateService"/> over temp
/// <c>.keymap</c> files and a temp <see cref="KeymapBackupStore"/> root.
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

    private static InMemorySettingsService NewSettings(UserSettings? seed = null) =>
        new() { Current = (seed ?? new UserSettings()) with { LiveKeyHighlighting = false } };

    private MainWindowViewModel NewVm(InMemorySettingsService settings, IKeymapStateService? keymapState = null) =>
        new(settings, keymapState: keymapState, keymapBackups: new KeymapBackupStore(_backupsRoot));

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
        vm.EditClearSelectedKey(toNone: false);

        Assert.Equal("▽", vm.Keys[0].Label);
        Assert.True(vm.Keys[0].IsEditChanged);
        Assert.True(vm.Keys[0].IsEditSelected);
        Assert.False(vm.Keys[1].IsEditSelected);
        Assert.Equal(1, vm.UnsavedEditCount);
    }

    [Fact]
    public void EditMode_LowerLayer_LiteralTransShowsItself_NoFallThroughToBase()
    {
        var vm = StartEditing(out _);

        vm.Layers[1].SelectCommand.Execute(null);

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
        vm.EditClearSelectedKey(toNone: false);
        Assert.Equal("▽", vm.Keys[0].Label);

        vm.Edit!.UndoCommand.Execute(null);

        Assert.Equal("Q", vm.Keys[0].Label);
        Assert.False(vm.Keys[0].IsEditChanged);
        Assert.Equal(0, vm.UnsavedEditCount);
    }

    [Fact]
    public void DiscardEdit_LeavesEditMode_ClearsEveryFlag_AndRepaintsFromLoadedKeymap()
    {
        var vm = StartEditing(out _);
        Tap(vm, 0);
        vm.EditClearSelectedKey(toNone: false);

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
        Assert.False(vm.Layers[1].IsShown);

        vm.StartEditCommand.Execute(null);

        Assert.True(vm.Layers[1].IsShown);

        vm.DiscardEditCommand.Execute(null);

        Assert.False(vm.Layers[1].IsShown);
        // "in memory only, never persisted": the hidden setting survived untouched.
        Assert.Equal(new[] { 1 }, settings.Current.HiddenLayers["Corne"]);
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
        vm.EditClearSelectedKey(toNone: false);
    }

    /// <summary>Puts the file on disk out of sync with what the edit opened, so
    /// the next save reports a conflict instead of writing.</summary>
    private static void ChangeFileOnDisk(string path) =>
        File.AppendAllText(path, "\n// someone else was here\n");

    [Fact]
    public void SaveEditCommand_WithoutChanges_Disabled_AfterOneChange_Enabled()
    {
        var vm = StartEditing(out _);

        Assert.False(vm.SaveEditCommand.CanExecute(null));

        ClearKeyZero(vm);

        Assert.True(vm.SaveEditCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveEdit_NoCallbacks_WritesFile_LeavesEdit_ReportsInStatusBar()
    {
        var vm = StartEditing(out var path);
        ClearKeyZero(vm);

        await vm.SaveEditCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        Assert.False(vm.IsSavingEdit);

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
        vm.Layers[1].SelectCommand.Execute(null);
        Assert.Equal(1, vm.ActiveLayerIndex);

        await vm.SaveEditCommand.ExecuteAsync(null);

        // A reload lands on layer 0; the save must put the user back on Lower.
        Assert.Equal(1, vm.ActiveLayerIndex);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public async Task SaveEdit_ReviewDeclined_StaysInEditMode_WritesNothing()
    {
        var vm = StartEditing(out var path);
        var before = File.ReadAllText(path);
        KeymapReviewRequest? captured = null;
        vm.ReviewChangesRequested = request =>
        {
            captured = request;
            return Task.FromResult(false);
        };
        ClearKeyZero(vm);

        await vm.SaveEditCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditing);
        Assert.Equal(1, vm.UnsavedEditCount);
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
        vm.SaveConflictRequested = request =>
        {
            captured = request;
            return Task.FromResult(KeymapSaveConflictChoice.Cancel);
        };
        ClearKeyZero(vm);

        await vm.SaveEditCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditing);
        Assert.Equal(1, vm.UnsavedEditCount);
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
        vm.SaveConflictRequested = _ => Task.FromResult(KeymapSaveConflictChoice.Overwrite);
        ClearKeyZero(vm);

        await vm.SaveEditCommand.ExecuteAsync(null);

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
        vm.SaveConflictRequested = _ => Task.FromResult(KeymapSaveConflictChoice.Reload);
        ClearKeyZero(vm);

        await vm.SaveEditCommand.ExecuteAsync(null);

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

        await vm.SaveEditCommand.ExecuteAsync(null);

        // No dialog wired up (parts 8-9): the safe answer, keep editing.
        Assert.True(vm.IsEditing);
        Assert.Equal(1, vm.UnsavedEditCount);
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

        vm.IsSavingEdit = true;
        Assert.False(vm.DiscardEditCommand.CanExecute(null));
        Assert.False(vm.SaveEditCommand.CanExecute(null));
    }
}
