using LViz.App.Services;
using LViz.App.Services.GitHub;
using LViz.App.ViewModels;
using LViz.Core.GitHub;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout;
using LViz.Core.Models;
using LViz.Core.Settings;
using LViz.Tests.GitHub;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Main-window hooks for the layout wizard: the start command, the
/// <see cref="MainWindowViewModel.HasUnmatchedKeymap"/> bookkeeping, the
/// context snapshot handed to the wizard window, and applying its result.
/// Fake setup mirrors <see cref="MainWindowViewModelLoadTests"/>; the profile
/// registry is process-global static state, so every test restores it in a
/// <c>finally</c> (the suite runs sequentially). The context tests also cover
/// the GitHub repo cache folder it snapshots for the wizard's start.
/// </summary>
public sealed class MainWindowViewModelLayoutWizardTests : IDisposable
{
    // Temp file standing in for the local cache copy of a GitHub keymap.
    private readonly List<string> _tempFiles = [];

    private string TempKeymapFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lviz-wz-gh-{Guid.NewGuid():N}.keymap");
        File.WriteAllText(path, "/* cache stand-in */");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
            try { File.Delete(path); } catch (IOException) { /* best effort */ }
    }

    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    // Canned load result, swappable between loads so one VM can load twice.
    private sealed class FakeKeymapStateService : IKeymapStateService
    {
        public KeymapLoadResult Result { get; set; } =
            new KeymapLoadResult.Failure("?", new InvalidOperationException("nothing queued"));

        public LoadedKeymap? Current { get; private set; }
        public bool HasLayout => Current is not null;
        public string? LoadedPath { get; private set; }
        public string? LastLoadError { get; private set; }

        public TransparentBindingKind? ClassifyBinding(int layer, int key)
            => Current?.ClassifyBinding(layer, key);

        public KeymapLoadResult Load(string path, string profileId)
        {
            if (Result is KeymapLoadResult.Success s)
            {
                Current = s.Keymap;
                LoadedPath = path;
                LastLoadError = null;
            }
            else
            {
                LastLoadError = path;
            }
            return Result;
        }

        public void Clear() { Current = null; LoadedPath = null; }
    }

    // A one-layer keymap with exactly `bindings` &kp cells, so the fake
    // result's FirstLayerBindingCount (built by Success below from the keymap
    // itself) and SelectKeyboard's fit check (which reads the keymap) agree.
    private static LoadedKeymap KeymapWith(int bindings)
    {
        var cells = string.Join(" ", Enumerable.Range(0, bindings).Select(_ => "&kp A"));
        var source =
            "/ { keymap { compatible = \"zmk,keymap\"; " +
            "default_layer { display-name = \"Base\"; bindings = <" + cells + ">; }; }; };";
        return new LoadedKeymap(ZmkKeymapLoader.LoadFromText(source, "test"));
    }

    private static KeymapLoadResult.Success Success(LoadedKeymap keymap, string path) =>
        new(keymap, path, keymap.LayerCount, keymap.Layers[0].Bindings.Count);

    private static UserLayoutFile Layout(string id, int keyCount) => new(
        UserLayoutFile.CurrentSchemaVersion,
        id,
        "My Board",
        null,
        [.. Enumerable.Range(0, keyCount)
            .Select(i => new UserLayoutKey(i * 100, 0, 100, 100, 0, 0, 0, Hand.Left))]);

    // Same "myboard" 7-key row, but key 0 dragged to X 500 — a geometry-only
    // edit like the "Edit layout…" window produces.
    private static UserLayoutFile EditedLayout(int key0X) => new(
        UserLayoutFile.CurrentSchemaVersion,
        "myboard",
        "My Board",
        null,
        [.. Enumerable.Range(0, 7)
            .Select(i => new UserLayoutKey(i == 0 ? key0X : i * 100, 0, 100, 100, 0, 0, 0, Hand.Left))]);

    [Fact]
    public void AddOwnBoardCommand_InvokesCallback()
    {
        var vm = new MainWindowViewModel(
            new InMemorySettingsService(), keymapState: new FakeKeymapStateService());

        var invoked = false;
        vm.AddOwnBoardRequested = () => invoked = true;
        vm.AddOwnBoardCommand.Execute(null);

        Assert.True(invoked);
    }

    [Fact]
    public void LoadLayoutFromPath_KeyCountNoProfileHas_ArmsFlag_MatchingLoadClearsIt()
    {
        var settings = new InMemorySettingsService();
        var keymap = new FakeKeymapStateService();
        var vm = new MainWindowViewModel(settings, keymapState: keymap);
        KeyboardProfileRegistry.SetUserProfiles([]);
        try
        {
            // 7 bindings: no bundled or user profile has 7 keys → orphaned.
            keymap.Result = Success(KeymapWith(7), "odd.keymap");
            vm.LoadLayoutFromPath("odd.keymap");
            Assert.True(vm.HasUnmatchedKeymap);

            // 42 bindings fit Corne → flag drops on the next successful load.
            keymap.Result = Success(KeymapWith(42), "corne.keymap");
            vm.LoadLayoutFromPath("corne.keymap");
            Assert.False(vm.HasUnmatchedKeymap);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void GetLayoutWizardContext_SnapshotsKeymapUserIdsAndFolder()
    {
        var settings = new InMemorySettingsService();
        var keymap = new FakeKeymapStateService();
        var vm = new MainWindowViewModel(settings, keymapState: keymap);
        var loaded = KeymapWith(42);
        keymap.Result = Success(loaded, "corne.keymap");
        vm.LoadLayoutFromPath("corne.keymap");
        KeyboardProfileRegistry.SetUserProfiles([new UserLayoutProfile(Layout("slabwing", 2))]);
        try
        {
            var ctx = vm.GetLayoutWizardContext();

            Assert.Same(loaded, ctx.LoadedKeymap);
            // Bare ids, no "user:" prefix.
            Assert.Equal(new[] { "slabwing" }, ctx.ExistingUserLayoutIds);
            Assert.Equal(UserLayoutStore.DefaultDirectory, ctx.LayoutsDirectory);
            // Nothing was ever opened on the bus in tests.
            Assert.Empty(ctx.ConnectedDeviceNames);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public async Task GetLayoutWizardContext_GitHubKeymapOpen_SnapshotsRepoCacheFolder()
    {
        var settings = new InMemorySettingsService();
        var keymap = new FakeKeymapStateService();
        var cacheFile = TempKeymapFile();
        var fake = new FakeGitHubKeymapService
        {
            OnOpen = _ => Task.FromResult<GitHubOpenResult>(
                new GitHubOpenResult.Loaded(cacheFile, FromCacheOffline: false)),
            OnCacheFolderFor = (owner, repo, branch) => $"cache/{owner}/{repo}/{branch}",
        };
        var vm = new MainWindowViewModel(settings, keymapState: keymap, gitHub: fake);
        keymap.Result = Success(KeymapWith(42), cacheFile);
        KeyboardProfileRegistry.SetUserProfiles([]);
        try
        {
            await vm.OpenGitHubKeymapAsync(
                new GitHubKeymapRef("ovandongen", "zmk-config-cosmos82", "main", "config/cosmos82.keymap"));

            var ctx = vm.GetLayoutWizardContext();

            Assert.Equal(
                fake.OnCacheFolderFor("ovandongen", "zmk-config-cosmos82", "main"),
                ctx.GitHubFolder);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void GetLayoutWizardContext_LocalKeymapOpen_GitHubFolderIsNull()
    {
        var keymap = new FakeKeymapStateService();
        // Even with the service wired, a local entry yields no GitHub folder.
        var vm = new MainWindowViewModel(
            new InMemorySettingsService(), keymapState: keymap, gitHub: new FakeGitHubKeymapService());
        keymap.Result = Success(KeymapWith(42), "corne.keymap");
        KeyboardProfileRegistry.SetUserProfiles([]);
        try
        {
            vm.LoadLayoutFromPath("corne.keymap");

            Assert.Null(vm.GetLayoutWizardContext().GitHubFolder);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void ApplyLayoutWizardResult_RegistersAndSelects_NewProfile_KeepsFittingKeymap()
    {
        var settings = new InMemorySettingsService();
        var keymap = new FakeKeymapStateService();
        var vm = new MainWindowViewModel(settings, keymapState: keymap);
        KeyboardProfileRegistry.SetUserProfiles([]);
        try
        {
            var odd = KeymapWith(7);
            keymap.Result = Success(odd, "odd.keymap");
            vm.LoadLayoutFromPath("odd.keymap");
            Assert.True(vm.HasUnmatchedKeymap);

            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.ApplyLayoutWizardResult(new LayoutWizardResult(Layout("myboard", 7), KeymapPathToLoad: null));

            var added = Assert.Single(vm.AvailableKeyboards, p => p.Id == "user:myboard");
            Assert.Equal("user:myboard", vm.SelectedKeyboard.Id);
            // The 7-binding keymap fits the new 7-key board and stays loaded.
            Assert.True(vm.HasLayoutLoaded);
            Assert.Same(odd, keymap.Current);
            Assert.False(vm.HasUnmatchedKeymap);
            Assert.Contains("AvailableKeyboards", raised);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void ApplyLayoutWizardResult_OpenKeymap_IsRememberedForTheNewBoard()
    {
        // The reported bug: open an unmatched keymap on Corne, build a board
        // for it in the wizard, switch away and back — it must come back.
        var settings = new InMemorySettingsService();
        var keymap = new FakeKeymapStateService();
        var vm = new MainWindowViewModel(settings, keymapState: keymap);
        KeyboardProfileRegistry.SetUserProfiles([]);
        var path = Path.Combine(Path.GetTempPath(), $"lviz-wiz-{Guid.NewGuid():N}.keymap");
        File.WriteAllText(path, "/* stand-in */");
        try
        {
            keymap.Result = Success(KeymapWith(7), "odd.keymap");
            vm.LoadLayoutFromPath(path);
            Assert.Equal(path, settings.Current.LayoutJsonPaths["Corne"]);

            vm.ApplyLayoutWizardResult(new LayoutWizardResult(Layout("myboard", 7), KeymapPathToLoad: null));

            Assert.Equal(path, settings.Current.LayoutJsonPaths["user:myboard"]);
            Assert.False(settings.Current.LayoutJsonPaths.ContainsKey("Corne"));
            Assert.Equal(path, settings.Current.RecentKeymapPaths["user:myboard"][0]);

            vm.SelectKeyboardCommand.Execute(KeyboardProfileRegistry.All.First(p => p.Id == "Glove80"));
            Assert.False(vm.HasLayoutLoaded);
            vm.SelectKeyboardCommand.Execute(vm.AvailableKeyboards.First(p => p.Id == "user:myboard"));

            Assert.True(vm.HasLayoutLoaded);
            Assert.Equal(path, vm.LoadedLayoutPath);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
            File.Delete(path);
        }
    }

    [Fact]
    public void ApplyLayoutWizardResult_ExistingId_ReplacesProfile()
    {
        var settings = new InMemorySettingsService();
        var vm = new MainWindowViewModel(settings, keymapState: new FakeKeymapStateService());
        KeyboardProfileRegistry.SetUserProfiles([new UserLayoutProfile(Layout("myboard", 7))]);
        try
        {
            vm.ApplyLayoutWizardResult(new LayoutWizardResult(Layout("myboard", 9), KeymapPathToLoad: null));

            var users = vm.AvailableKeyboards.OfType<UserLayoutProfile>().ToList();
            var kept = Assert.Single(users);
            Assert.Equal(9, kept.KeyCount);
            Assert.Equal("user:myboard", vm.SelectedKeyboard.Id);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void CanEditLayout_FalseOnBundledBoard_TrueAfterWizardResult()
    {
        var vm = new MainWindowViewModel(
            new InMemorySettingsService(), keymapState: new FakeKeymapStateService());
        KeyboardProfileRegistry.SetUserProfiles([]);
        try
        {
            Assert.False(vm.CanEditLayout);

            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.ApplyLayoutWizardResult(new LayoutWizardResult(Layout("myboard", 7), KeymapPathToLoad: null));

            Assert.True(vm.CanEditLayout);
            Assert.Contains("CanEditLayout", raised);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void GetLayoutEditContext_NullOnBundledBoard_SnapshotsUserLayout()
    {
        var vm = new MainWindowViewModel(
            new InMemorySettingsService(), keymapState: new FakeKeymapStateService());
        KeyboardProfileRegistry.SetUserProfiles([]);
        try
        {
            Assert.Null(vm.GetLayoutEditContext());

            vm.ApplyLayoutWizardResult(new LayoutWizardResult(Layout("myboard", 7), KeymapPathToLoad: null));

            var ctx = vm.GetLayoutEditContext();
            Assert.NotNull(ctx);
            Assert.Equal("myboard", ctx.Layout.Id);
            // No keymap loaded → bare index captions, one per key.
            Assert.Equal(7, ctx.Labels.Count);
            Assert.Equal(UserLayoutStore.DefaultDirectory, ctx.LayoutsDirectory);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void ApplyEditedLayout_SelectedId_ReplacesProfileAndRefreshesGeometry()
    {
        var vm = new MainWindowViewModel(
            new InMemorySettingsService(), keymapState: new FakeKeymapStateService());
        KeyboardProfileRegistry.SetUserProfiles([]);
        try
        {
            vm.ApplyLayoutWizardResult(new LayoutWizardResult(Layout("myboard", 7), KeymapPathToLoad: null));
            var old = vm.SelectedKeyboard;
            var key0X = vm.Keys[0].Position.X;

            // Key 0 at X 500 instead of 0 — a geometry-only edit under the
            // selected id. SelectKeyboard's same-id early return must NOT
            // leave the old board on screen.
            vm.ApplyEditedLayout(EditedLayout(500));

            Assert.NotSame(old, vm.SelectedKeyboard);
            Assert.Equal("user:myboard", vm.SelectedKeyboard.Id);
            Assert.NotEqual(key0X, vm.Keys[0].Position.X);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void ApplyEditedLayout_SelectedId_KeepsFittingKeymapPainted()
    {
        var keymap = new FakeKeymapStateService();
        var vm = new MainWindowViewModel(new InMemorySettingsService(), keymapState: keymap);
        KeyboardProfileRegistry.SetUserProfiles([]);
        try
        {
            keymap.Result = Success(KeymapWith(7), "odd.keymap");
            vm.LoadLayoutFromPath("odd.keymap");
            vm.ApplyLayoutWizardResult(new LayoutWizardResult(Layout("myboard", 7), KeymapPathToLoad: null));
            Assert.True(vm.HasLayoutLoaded);

            vm.ApplyEditedLayout(Layout("myboard", 7));

            Assert.True(vm.HasLayoutLoaded);
            // Rebuilt key VMs must come back painted, not blank.
            Assert.All(vm.Keys, k => Assert.Equal("A", k.Label));
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void ApplyLayoutWizardResult_TwiceSameId_RefreshesKeyCount()
    {
        var vm = new MainWindowViewModel(
            new InMemorySettingsService(), keymapState: new FakeKeymapStateService());
        KeyboardProfileRegistry.SetUserProfiles([]);
        try
        {
            vm.ApplyLayoutWizardResult(new LayoutWizardResult(Layout("myboard", 7), KeymapPathToLoad: null));
            Assert.Equal(7, vm.Keys.Count);

            vm.ApplyLayoutWizardResult(new LayoutWizardResult(Layout("myboard", 9), KeymapPathToLoad: null));

            Assert.Equal(9, vm.Keys.Count);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void EditLayoutCommand_InvokesCallback()
    {
        var vm = new MainWindowViewModel(
            new InMemorySettingsService(), keymapState: new FakeKeymapStateService());

        var invoked = false;
        vm.EditLayoutRequested = () => invoked = true;
        vm.EditLayoutCommand.Execute(null);

        Assert.True(invoked);
    }
}
