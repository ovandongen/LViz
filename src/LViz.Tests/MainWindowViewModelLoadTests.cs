using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout;
using LViz.Core.Models;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Drives <see cref="MainWindowViewModel.LoadLayoutFromPath"/> through a fake
/// <see cref="IKeymapStateService"/> so the load orchestration (render,
/// profile auto-switch, failure isolation) is testable without disk IO.
/// </summary>
public class MainWindowViewModelLoadTests
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public int SaveCount { get; private set; }
        public UserSettings Load() => Current;
        public void Save(UserSettings settings)
        {
            Current = settings;
            SaveCount++;
        }
    }

    // Returns a canned result and records state the way the real service does.
    private sealed class FakeKeymapStateService : IKeymapStateService
    {
        private readonly KeymapLoadResult _result;
        public FakeKeymapStateService(KeymapLoadResult result) => _result = result;

        public LoadedKeymap? Current { get; private set; }
        public bool HasLayout => Current is not null;
        public string? LoadedPath { get; private set; }
        public string? LastLoadError { get; private set; }
        public int LoadCallCount { get; private set; }

        public TransparentBindingKind? ClassifyBinding(int layer, int key)
            => Current?.ClassifyBinding(layer, key);

        public KeymapLoadResult Load(string path, string profileId)
        {
            LoadCallCount++;
            if (_result is KeymapLoadResult.Success s)
            {
                Current = s.Keymap;
                LoadedPath = path;
                LastLoadError = null;
            }
            else if (_result is KeymapLoadResult.Failure f)
            {
                LastLoadError = $"{path}: {f.Error.GetType().Name}: {f.Error.Message}";
            }
            return _result;
        }

        public void Clear() { Current = null; LoadedPath = null; }
    }

    private const string TwoLayerSource = @"
        / {
            keymap {
                compatible = ""zmk,keymap"";
                default_layer { display-name = ""Base""; bindings = <&kp A &kp B &kp C>; };
                lower         { display-name = ""Lower""; bindings = <&kp X &kp Y &kp Z>; };
            };
        };";

    private static LoadedKeymap SampleKeymap() =>
        new(ZmkKeymapLoader.LoadFromText(TwoLayerSource, "test"));

    [Fact]
    public void LoadLayoutFromPath_ParseFailure_LeavesProfileAndLayoutUntouched()
    {
        var settings = new InMemorySettingsService();
        var failing = new FakeKeymapStateService(
            new KeymapLoadResult.Failure("bad.keymap", new InvalidOperationException("boom")));
        var vm = new MainWindowViewModel(settings, keymapState: failing);

        var profileBefore = vm.SelectedKeyboard;
        vm.LoadLayoutFromPath("bad.keymap");

        // The fix: a parse failure must not mutate the profile or claim a layout.
        Assert.Same(profileBefore, vm.SelectedKeyboard);
        Assert.False(vm.HasLayoutLoaded);
        Assert.Empty(vm.Layers);
    }

    [Fact]
    public void LoadLayoutFromPath_Success_PopulatesLayers()
    {
        var settings = new InMemorySettingsService();
        // Binding count matches Corne (42) → no auto-switch, stays put.
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok);

        vm.LoadLayoutFromPath("ok.keymap");

        Assert.True(vm.HasLayoutLoaded);
        Assert.Equal(2, vm.Layers.Count);
        Assert.Equal("Corne", vm.SelectedKeyboard.Id);
    }

    [Fact]
    public void LoadLayoutFromPath_BindingCountMatchesOtherProfile_AutoSwitches()
    {
        var settings = new InMemorySettingsService();
        // 80 bindings matches the Glove80 profile → the load should auto-switch.
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "glove.keymap", LayerCount: 2, FirstLayerBindingCount: 80));
        var vm = new MainWindowViewModel(settings, keymapState: ok);
        Assert.Equal("Corne", vm.SelectedKeyboard.Id);

        vm.LoadLayoutFromPath("glove.keymap");

        Assert.Equal("Glove80", vm.SelectedKeyboard.Id);
        Assert.Equal("Glove80", settings.Current.Keyboard);
        Assert.True(vm.HasLayoutLoaded);
    }

    [Fact]
    public void LoadLayoutFromPath_Success_RecordsPathAtFrontOfActiveProfileRecents()
    {
        var settings = new InMemorySettingsService();
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "first.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok);

        vm.LoadLayoutFromPath("first.keymap");

        Assert.Equal(new[] { "first.keymap" }, settings.Current.RecentKeymapPaths["Corne"]);
        Assert.Equal(new[] { "first.keymap" }, vm.RecentKeymaps);
    }

    [Fact]
    public void LoadLayoutFromPath_AutoSwitch_RecordsPathUnderNewProfile()
    {
        var settings = new InMemorySettingsService();
        // 80 bindings matches the Glove80 profile → the load auto-switches.
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "glove.keymap", LayerCount: 2, FirstLayerBindingCount: 80));
        var vm = new MainWindowViewModel(settings, keymapState: ok);

        vm.LoadLayoutFromPath("glove.keymap");

        Assert.Equal("Glove80", vm.SelectedKeyboard.Id);
        // The recents entry rides the auto-switch: recorded under Glove80,
        // never under the profile the user was on when picking the file.
        Assert.Equal(new[] { "glove.keymap" }, settings.Current.RecentKeymapPaths["Glove80"]);
        Assert.False(settings.Current.RecentKeymapPaths.ContainsKey("Corne"));
        Assert.Equal(new[] { "glove.keymap" }, vm.RecentKeymaps);
    }

    [Fact]
    public void LoadLayoutFromPath_Failure_RemovesPathFromProfileRecents()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                RecentKeymapPaths = new Dictionary<string, List<string>>
                {
                    ["Corne"] = new() { "gone.keymap", "still.keymap" },
                },
            },
        };
        var failing = new FakeKeymapStateService(
            new KeymapLoadResult.Failure("gone.keymap", new InvalidOperationException("boom")));
        var vm = new MainWindowViewModel(settings, keymapState: failing);

        vm.LoadLayoutFromPath("gone.keymap");

        Assert.Equal(new[] { "still.keymap" }, settings.Current.RecentKeymapPaths["Corne"]);
        Assert.Equal(new[] { "still.keymap" }, vm.RecentKeymaps);
    }

    [Fact]
    public void LoadLayoutFromPath_Success_RaisesPropertyChangedForRecentKeymaps()
    {
        var settings = new InMemorySettingsService();
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.LoadLayoutFromPath("ok.keymap");

        Assert.Contains("RecentKeymaps", raised);
    }

    [Fact]
    public void LoadLayoutFromPath_Failure_RaisesPropertyChangedForRecentKeymaps()
    {
        var settings = new InMemorySettingsService();
        var failing = new FakeKeymapStateService(
            new KeymapLoadResult.Failure("bad.keymap", new InvalidOperationException("boom")));
        var vm = new MainWindowViewModel(settings, keymapState: failing);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.LoadLayoutFromPath("bad.keymap");

        Assert.Contains("RecentKeymaps", raised);
    }

    [Fact]
    public void LoadLayoutFromPath_Failure_UnlistedPath_SkipsSettingsWrite()
    {
        var settings = new InMemorySettingsService();
        var failing = new FakeKeymapStateService(
            new KeymapLoadResult.Failure("bad.keymap", new InvalidOperationException("boom")));
        var vm = new MainWindowViewModel(settings, keymapState: failing);
        var original = settings.Current;

        vm.LoadLayoutFromPath("bad.keymap");

        // No recents entry to prune → no save, so the stored object is untouched.
        Assert.Same(original, settings.Current);
    }

    [Fact]
    public void SelectKeyboard_RaisesPropertyChangedForRecentKeymaps()
    {
        var settings = new InMemorySettingsService();
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok);
        var glove80 = KeyboardProfileRegistry.All.First(p => p.Id == "Glove80");

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SelectKeyboardCommand.Execute(glove80);

        Assert.Contains("RecentKeymaps", raised);
        // The surfaced list follows the new profile — Corne's recents don't leak.
        Assert.Empty(vm.RecentKeymaps);
    }

    // --- Recents flyout surface: RecentKeymapItems / HasRecentKeymaps / commands ---

    [Fact]
    public void RecentKeymapItems_MapsPathsInOrderWithDisplayFields()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                RecentKeymapPaths = new Dictionary<string, List<string>>
                {
                    ["Corne"] = new() { "/keymaps/one.keymap", "/keymaps/gone.keymap" },
                },
            },
        };
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "one.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok)
        {
            FileExists = p => p != "/keymaps/gone.keymap",
        };

        vm.LoadLayoutFromPath("/keymaps/one.keymap");

        var items = vm.RecentKeymapItems;
        Assert.Equal(2, items.Count);

        Assert.Equal("/keymaps/one.keymap", items[0].Path);
        Assert.Equal("one.keymap", items[0].FileName);
        Assert.Equal("/keymaps", items[0].Folder);
        Assert.True(items[0].Exists);
        // Only the loaded path is current.
        Assert.True(items[0].IsCurrent);

        Assert.Equal("/keymaps/gone.keymap", items[1].Path);
        Assert.Equal("gone.keymap", items[1].FileName);
        Assert.Equal("/keymaps", items[1].Folder);
        Assert.False(items[1].Exists);
        Assert.False(items[1].IsCurrent);
    }

    [Fact]
    public void HasRecentKeymaps_TracksActiveProfileList()
    {
        var settings = new InMemorySettingsService();
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok);

        Assert.False(vm.HasRecentKeymaps);

        vm.LoadLayoutFromPath("ok.keymap");

        Assert.True(vm.HasRecentKeymaps);
    }

    [Fact]
    public void OpenRecentKeymap_ExistingFile_LoadsAndMovesToFront()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                RecentKeymapPaths = new Dictionary<string, List<string>>
                {
                    ["Corne"] = new() { "/keymaps/first.keymap", "/keymaps/second.keymap" },
                },
            },
        };
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "second.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok) { FileExists = _ => true };

        var target = vm.RecentKeymapItems.First(i => i.Path == "/keymaps/second.keymap");
        vm.OpenRecentKeymapCommand.Execute(target);

        Assert.Equal("/keymaps/second.keymap", vm.LoadedLayoutPath);
        Assert.Equal(
            new[] { "/keymaps/second.keymap", "/keymaps/first.keymap" },
            vm.RecentKeymaps);
        Assert.True(vm.RecentKeymapItems[0].IsCurrent);
    }

    [Fact]
    public void OpenRecentKeymap_MissingFile_RemovesWithoutLoading()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                RecentKeymapPaths = new Dictionary<string, List<string>>
                {
                    ["Corne"] = new() { "/keymaps/gone.keymap", "/keymaps/kept.keymap" },
                },
            },
        };
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "kept.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok)
        {
            FileExists = p => p == "/keymaps/kept.keymap",
        };

        var target = vm.RecentKeymapItems.First(i => i.Path == "/keymaps/gone.keymap");
        vm.OpenRecentKeymapCommand.Execute(target);

        Assert.Equal(new[] { "/keymaps/kept.keymap" }, settings.Current.RecentKeymapPaths["Corne"]);
        // The load path was never entered — the stale entry was pruned.
        Assert.Equal(0, ok.LoadCallCount);
        Assert.False(vm.HasLayoutLoaded);
        // Literal check (not Loc.Format of the same key) so a missing resx
        // entry can't pass by cancelling out on both sides.
        Assert.Equal(
            "Removed gone.keymap from recent keymaps (file not found)",
            vm.StatusMessage);
    }

    [Fact]
    public void OpenRecentKeymap_NullParameter_DoesNothing()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                RecentKeymapPaths = new Dictionary<string, List<string>>
                {
                    ["Corne"] = new() { "/keymaps/one.keymap" },
                },
            },
        };
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "one.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        // Nothing exists — a null click must not fall through to the prune path.
        var vm = new MainWindowViewModel(settings, keymapState: ok) { FileExists = _ => false };
        var savesBefore = settings.SaveCount;

        vm.OpenRecentKeymapCommand.Execute(null);

        Assert.Equal(savesBefore, settings.SaveCount);
        Assert.Equal(0, ok.LoadCallCount);
        Assert.Equal(new[] { "/keymaps/one.keymap" }, vm.RecentKeymaps);
    }

    [Fact]
    public void ClearRecentKeymaps_EmptiesActiveProfileAndKeepsOthers()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                RecentKeymapPaths = new Dictionary<string, List<string>>
                {
                    ["Corne"] = new() { "/keymaps/one.keymap", "/keymaps/two.keymap" },
                    ["Glove80"] = new() { "/keymaps/glove.keymap" },
                },
            },
        };
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok) { FileExists = _ => true };

        vm.ClearRecentKeymapsCommand.Execute(null);

        Assert.False(settings.Current.RecentKeymapPaths.ContainsKey("Corne"));
        Assert.Equal(new[] { "/keymaps/glove.keymap" }, settings.Current.RecentKeymapPaths["Glove80"]);
        Assert.False(vm.HasRecentKeymaps);
        Assert.Empty(vm.RecentKeymapItems);
    }

    [Fact]
    public void ClearRecentKeymaps_EmptyList_SkipsSettingsWrite()
    {
        var settings = new InMemorySettingsService();
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok);
        var savesBefore = settings.SaveCount;
        var currentBefore = settings.Current;

        vm.ClearRecentKeymapsCommand.Execute(null);

        Assert.Equal(savesBefore, settings.SaveCount);
        Assert.Same(currentBefore, settings.Current);
    }

    [Fact]
    public void RefreshRecentKeymapsCommand_RaisesPropertyChangedForRecentKeymapItems()
    {
        var settings = new InMemorySettingsService();
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.RefreshRecentKeymapsCommand.Execute(null);

        Assert.Contains("RecentKeymapItems", raised);
        Assert.Contains("HasRecentKeymaps", raised);
        Assert.Contains("RecentKeymaps", raised);
    }

    [Fact]
    public void LoadLayoutFromPath_Failure_RaisesPropertyChangedForDerivedRecentSurfaces()
    {
        var settings = new InMemorySettingsService();
        var failing = new FakeKeymapStateService(
            new KeymapLoadResult.Failure("bad.keymap", new InvalidOperationException("boom")));
        var vm = new MainWindowViewModel(settings, keymapState: failing);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.LoadLayoutFromPath("bad.keymap");

        // The "RecentKeymaps" raise on this path is pinned by
        // LoadLayoutFromPath_Failure_RaisesPropertyChangedForRecentKeymaps;
        // the derived flyout members must ride along with it.
        Assert.Contains("RecentKeymapItems", raised);
        Assert.Contains("HasRecentKeymaps", raised);
    }

    // --- Per-layer tab visibility (hidden layer buttons) ---

    [Fact]
    public void LoadLayoutFromPath_SeededHiddenLayers_HidesOnlyThatTab()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                HiddenLayers = new Dictionary<string, List<int>> { ["Corne"] = new() { 1 } },
            },
        };
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok);

        vm.LoadLayoutFromPath("ok.keymap");

        // The hidden layer stays in Layers (Settings pickers + keyboard push
        // depend on it) — only its button visibility flag flips.
        Assert.Equal(2, vm.Layers.Count);
        Assert.True(vm.Layers[0].IsShown);
        Assert.False(vm.Layers[1].IsShown);
    }

    [Fact]
    public void SetLayerShown_HideThenShow_PersistsAndUpdatesTabInPlace()
    {
        var settings = new InMemorySettingsService();
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok);
        vm.LoadLayoutFromPath("ok.keymap");

        vm.SetLayerShown(1, false);

        Assert.Equal(new[] { 1 }, settings.Current.HiddenLayers["Corne"]);
        Assert.False(vm.Layers[1].IsShown);

        vm.SetLayerShown(1, true);

        Assert.False(settings.Current.HiddenLayers.ContainsKey("Corne"));
        Assert.True(vm.Layers[1].IsShown);
    }

    [Fact]
    public void SetLayerShown_LayerZero_IgnoredWithoutPersisting()
    {
        var settings = new InMemorySettingsService();
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok);
        vm.LoadLayoutFromPath("ok.keymap");

        vm.SetLayerShown(0, false);

        Assert.False(settings.Current.HiddenLayers.ContainsKey("Corne"));
        Assert.True(vm.Layers[0].IsShown);
    }

    [Fact]
    public void SelectKeyboard_SameKeyCountProfile_RestampsHiddenTabsFromNewProfile()
    {
        // Sofle and GO60 both have 60 keys, so switching keeps the keymap —
        // the tabs must still follow the new board's hidden set.
        var bindings = string.Join(" ", Enumerable.Repeat("&kp A", 60));
        var source = $$"""
            / {
                keymap {
                    compatible = "zmk,keymap";
                    default_layer { display-name = "Base"; bindings = <{{bindings}}>; };
                    lower         { display-name = "Lower"; bindings = <{{bindings}}>; };
                };
            };
            """;
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                HiddenLayers = new Dictionary<string, List<int>> { ["Sofle"] = new() { 1 } },
            },
        };
        var ok = new FakeKeymapStateService(new KeymapLoadResult.Success(
            new LoadedKeymap(ZmkKeymapLoader.LoadFromText(source, "test")),
            "sixty.keymap", LayerCount: 2, FirstLayerBindingCount: 60));
        var vm = new MainWindowViewModel(settings, keymapState: ok);
        vm.LoadLayoutFromPath("sixty.keymap");
        Assert.Equal("Sofle", vm.SelectedKeyboard.Id);
        Assert.False(vm.Layers[1].IsShown);

        vm.SelectKeyboardCommand.Execute(KeyboardProfileRegistry.All.First(p => p.Id == "GO60"));

        Assert.True(vm.HasLayoutLoaded);
        Assert.True(vm.Layers[1].IsShown);
    }

    [Fact]
    public void SettingsViewModel_LayerEntries_CanHideFlagsAndToggleThroughToMainVm()
    {
        var settings = new InMemorySettingsService();
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok);
        vm.LoadLayoutFromPath("ok.keymap");
        var settingsVm = new SettingsViewModel(settings, vm, new UpdateService(settings));

        Assert.False(settingsVm.LayerEntries[0].CanHide);
        Assert.True(settingsVm.LayerEntries[1].CanHide);
        Assert.True(settingsVm.LayerEntries[1].IsShown);

        settingsVm.LayerEntries[1].IsShown = false;

        Assert.False(vm.Layers[1].IsShown);
        Assert.Equal(new[] { 1 }, settings.Current.HiddenLayers["Corne"]);
    }
}
