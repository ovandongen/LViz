using LViz.App.Services;
using LViz.App.Services.GitHub;
using LViz.App.ViewModels;
using LViz.Core.GitHub;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using LViz.Core.Settings;
using LViz.Tests.GitHub;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// GitHub entries in the main window: stored as <see cref="GitHubKeymapRef"/>
/// strings in the same settings dicts as local paths, re-opened through
/// <see cref="IGitHubKeymapService"/> from recents, and restored at startup
/// from the cache, then updated in the background when GitHub moved. VM built like in <see cref="MainWindowViewModelLoadTests"/>
/// — in-memory settings plus a canned-result keymap-state fake.
/// </summary>
public sealed class MainWindowViewModelGitHubTests : IDisposable
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

    // Binding count matches Corne (42) so loads stay on the seeded profile.
    private static FakeKeymapStateService Ok() =>
        new(new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));

    private static FakeKeymapStateService Failing() =>
        new(new KeymapLoadResult.Failure("bad.keymap", new InvalidOperationException("boom")));

    private const string TestRefString = "github:ovandongen/zmk-config-cosmos82:main:config/cosmos82.keymap";
    private static GitHubKeymapRef R => new("ovandongen", "zmk-config-cosmos82", "main", "config/cosmos82.keymap");

    // Temp files standing in for the local cache copy of the keymap.
    private readonly List<string> _tempFiles = [];

    private string TempKeymapFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lviz-gh-test-{Guid.NewGuid():N}.keymap");
        File.WriteAllText(path, "/* cache stand-in */");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
            try { File.Delete(path); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task OpenGitHubKeymapAsync_Loaded_StoresRefAsEntryNotCachePath()
    {
        var settings = new InMemorySettingsService();
        var tempPath = TempKeymapFile();
        var fake = new FakeGitHubKeymapService
        {
            OnOpen = _ => Task.FromResult<GitHubOpenResult>(
                new GitHubOpenResult.Loaded(tempPath, FromCacheOffline: false)),
        };
        var vm = new MainWindowViewModel(settings, keymapState: Ok(), gitHub: fake);

        await vm.OpenGitHubKeymapAsync(R);

        // Settings carry the reference, never the cache file path.
        Assert.Equal(TestRefString, settings.Current.LayoutJsonPaths["Corne"]);
        Assert.Equal(new[] { TestRefString }, settings.Current.RecentKeymapPaths["Corne"]);
        Assert.Equal(new[] { TestRefString }, vm.RecentKeymaps);
        Assert.True(vm.HasLayoutLoaded);
        Assert.Equal(tempPath, vm.LoadedLayoutPath);
        Assert.False(vm.Toast.IsVisible);

        var row = vm.RecentKeymapItems[0];
        Assert.True(row.IsGitHub);
        Assert.Equal("cosmos82.keymap", row.FileName);
        Assert.Equal("ovandongen/zmk-config-cosmos82 · main", row.Folder);
        Assert.True(row.Exists);
        Assert.True(row.IsCurrent);
    }

    [Fact]
    public async Task OpenGitHubKeymapAsync_LoadedOffline_LoadsAndToastsOffline()
    {
        var settings = new InMemorySettingsService();
        var tempPath = TempKeymapFile();
        var fake = new FakeGitHubKeymapService
        {
            OnOpen = _ => Task.FromResult<GitHubOpenResult>(
                new GitHubOpenResult.Loaded(tempPath, FromCacheOffline: true)),
        };
        var vm = new MainWindowViewModel(settings, keymapState: Ok(), gitHub: fake);

        await vm.OpenGitHubKeymapAsync(R);

        Assert.True(vm.HasLayoutLoaded);
        Assert.Equal(TestRefString, vm.RecentKeymaps[0]);
        Assert.True(vm.Toast.IsVisible);
        Assert.Equal(
            "GitHub not reachable, opened the cached copy of cosmos82.keymap",
            vm.Toast.Message);
    }

    [Fact]
    public async Task OpenGitHubKeymapAsync_Unauthorized_RequestsSignInWithoutLoading()
    {
        var settings = new InMemorySettingsService();
        var fake = new FakeGitHubKeymapService
        {
            OnOpen = _ => Task.FromResult<GitHubOpenResult>(
                new GitHubOpenResult.Failed(GitHubErrorKind.Unauthorized, "sign in")),
        };
        var vm = new MainWindowViewModel(settings, keymapState: Ok(), gitHub: fake);
        var openRequests = 0;
        vm.OpenFromGitHubRequested = () => openRequests++;
        var savesBefore = settings.SaveCount;

        await vm.OpenGitHubKeymapAsync(R);

        Assert.Equal(1, openRequests);
        Assert.False(vm.HasLayoutLoaded);
        // Nothing loaded and nothing written — recents unchanged.
        Assert.Equal(savesBefore, settings.SaveCount);
        Assert.False(settings.Current.RecentKeymapPaths.ContainsKey("Corne"));
        Assert.Equal("Sign in to GitHub to open cosmos82.keymap", vm.Toast.Message);
    }

    [Fact]
    public async Task OpenGitHubKeymapAsync_Network_LoadsNothingKeepsEntryAndRestoresStatus()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                RecentKeymapPaths = new Dictionary<string, List<string>>
                {
                    ["Corne"] = new() { TestRefString, "/keymaps/kept.keymap" },
                },
            },
        };
        var fake = new FakeGitHubKeymapService
        {
            OnOpen = _ => Task.FromResult<GitHubOpenResult>(
                new GitHubOpenResult.Failed(GitHubErrorKind.Network, "offline")),
        };
        var vm = new MainWindowViewModel(settings, keymapState: Ok(), gitHub: fake);
        vm.StatusMessage = "Loaded ok.keymap (2 layers)";
        var savesBefore = settings.SaveCount;

        await vm.OpenGitHubKeymapAsync(R);

        Assert.False(vm.HasLayoutLoaded);
        // The entry is kept: the repo may just be temporarily unreachable.
        Assert.Equal(savesBefore, settings.SaveCount);
        Assert.Equal(new[] { TestRefString, "/keymaps/kept.keymap" }, vm.RecentKeymaps);
        // The status bar shows what it said before "Loading…" again.
        Assert.Equal("Loaded ok.keymap (2 layers)", vm.StatusMessage);
        Assert.Equal(
            "GitHub not reachable and there is no cached copy of cosmos82.keymap",
            vm.Toast.Message);
    }

    [Fact]
    public async Task OpenGitHubKeymapAsync_NotFound_LoadsNothingKeepsEntry()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                RecentKeymapPaths = new Dictionary<string, List<string>>
                {
                    ["Corne"] = new() { TestRefString },
                },
            },
        };
        var fake = new FakeGitHubKeymapService
        {
            OnOpen = _ => Task.FromResult<GitHubOpenResult>(
                new GitHubOpenResult.Failed(GitHubErrorKind.NotFound, "nope")),
        };
        var vm = new MainWindowViewModel(settings, keymapState: Ok(), gitHub: fake);

        await vm.OpenGitHubKeymapAsync(R);

        Assert.False(vm.HasLayoutLoaded);
        Assert.Contains(TestRefString, vm.RecentKeymaps);
        Assert.Equal(
            "cosmos82.keymap wasn't found on GitHub, or LViz has no access to the repo",
            vm.Toast.Message);
    }

    [Fact]
    public void OpenRecentKeymap_GitHubRow_OpensThroughServiceEvenWithNoLocalFile()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                RecentKeymapPaths = new Dictionary<string, List<string>>
                {
                    ["Corne"] = new() { TestRefString },
                },
            },
        };
        var tempPath = TempKeymapFile();
        var fake = new FakeGitHubKeymapService
        {
            OnOpen = _ => Task.FromResult<GitHubOpenResult>(
                new GitHubOpenResult.Loaded(tempPath, FromCacheOffline: false)),
        };
        // Every local path is "missing" — irrelevant for a GitHub row.
        var keymapState = Ok();
        var vm = new MainWindowViewModel(settings, keymapState: keymapState, gitHub: fake)
        {
            FileExists = _ => false,
        };

        var row = vm.RecentKeymapItems[0];
        vm.OpenRecentKeymapCommand.Execute(row);

        Assert.Contains("OpenAsync", fake.Calls);
        Assert.True(vm.HasLayoutLoaded);
        Assert.Equal(tempPath, vm.LoadedLayoutPath);
        // The entry moved to the front of recents as its own string.
        Assert.Equal(new[] { TestRefString }, vm.RecentKeymaps);
    }

    [Fact]
    public void InitializeAsync_StoredGitHubRefWithCache_LoadsFromCacheWithoutNetwork()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                LiveKeyHighlighting = false, // startup tests exercise the load path, not HID
                LayoutJsonPaths = new Dictionary<string, string> { ["Corne"] = TestRefString },
                RecentKeymapPaths = new Dictionary<string, List<string>>
                {
                    ["Corne"] = new() { TestRefString },
                },
            },
        };
        var tempPath = TempKeymapFile();
        var fake = new FakeGitHubKeymapService { OnLocalPathFor = _ => tempPath };
        var vm = new MainWindowViewModel(settings, keymapState: Ok(), gitHub: fake);

        vm.InitializeAsync();

        Assert.True(vm.HasLayoutLoaded);
        Assert.Equal(tempPath, vm.LoadedLayoutPath);
        // Startup loads from the cache; it never goes through OpenAsync (the
        // background update check uses FetchRepoAsync, and is a no-op signed out).
        Assert.Contains("LocalPathFor", fake.Calls);
        Assert.DoesNotContain("OpenAsync", fake.Calls);
        // The stored entry stays the reference — the cache path is never written.
        Assert.Equal(TestRefString, settings.Current.LayoutJsonPaths["Corne"]);
        Assert.Equal(TestRefString, vm.RecentKeymaps[0]);
        Assert.True(vm.RecentKeymapItems[0].IsCurrent);
    }

    private static InMemorySettingsService StartupSettings() => new()
    {
        Current = new UserSettings
        {
            LiveKeyHighlighting = false,
            LayoutJsonPaths = new Dictionary<string, string> { ["Corne"] = TestRefString },
            RecentKeymapPaths = new Dictionary<string, List<string>> { ["Corne"] = new() { TestRefString } },
        },
    };

    [Fact]
    public async Task InitializeAsync_GitHubMovedSinceCache_ReloadsNewerCopyAndToasts()
    {
        var oldPath = TempKeymapFile();
        var newPath = TempKeymapFile();
        var current = oldPath;
        var fake = new FakeGitHubKeymapService
        {
            IsSignedIn = true,
            OnLocalPathFor = _ => current,
            OnFetchRepo = (_, _, _) =>
            {
                current = newPath; // the download replaced the cached copy
                return Task.FromResult(new GitHubCacheResult("folder", "sha", Downloaded: true));
            },
        };
        var keymapState = Ok();
        var vm = new MainWindowViewModel(StartupSettings(), keymapState: keymapState, gitHub: fake);

        vm.InitializeAsync();
        await vm.PendingGitHubCheck!;

        Assert.Equal(2, keymapState.LoadCallCount);
        Assert.Equal(newPath, vm.LoadedLayoutPath);
        Assert.Equal("Loaded the newer cosmos82.keymap from GitHub", vm.Toast.Message);
        Assert.Equal(TestRefString, vm.RecentKeymaps[0]);
    }

    [Fact]
    public async Task InitializeAsync_GitHubUnchanged_KeepsCachedLoad()
    {
        var path = TempKeymapFile();
        var fake = new FakeGitHubKeymapService
        {
            IsSignedIn = true,
            OnLocalPathFor = _ => path,
            OnFetchRepo = (_, _, _) => Task.FromResult(new GitHubCacheResult("folder", "sha", Downloaded: false)),
        };
        var keymapState = Ok();
        var vm = new MainWindowViewModel(StartupSettings(), keymapState: keymapState, gitHub: fake);

        vm.InitializeAsync();
        await vm.PendingGitHubCheck!;

        Assert.Equal(1, keymapState.LoadCallCount);
        Assert.Contains("FetchRepoAsync", fake.Calls);
        Assert.False(vm.Toast.IsVisible);
    }

    [Theory]
    [InlineData(false)] // signed out: no network at all
    [InlineData(true)]  // signed in but offline
    public async Task InitializeAsync_GitHubCheckImpossible_StaysSilentOnCachedCopy(bool signedIn)
    {
        var path = TempKeymapFile();
        var fake = new FakeGitHubKeymapService
        {
            IsSignedIn = signedIn,
            OnLocalPathFor = _ => path,
            OnFetchRepo = (_, _, _) => throw new GitHubApiException(GitHubErrorKind.Network, "offline"),
        };
        var keymapState = Ok();
        var vm = new MainWindowViewModel(StartupSettings(), keymapState: keymapState, gitHub: fake);
        var opened = 0;
        vm.OpenFromGitHubRequested = () => opened++;

        vm.InitializeAsync();
        await vm.PendingGitHubCheck!;

        Assert.Equal(1, keymapState.LoadCallCount);
        Assert.Equal(path, vm.LoadedLayoutPath);
        Assert.False(vm.Toast.IsVisible);
        Assert.Equal(0, opened);
        Assert.Equal(signedIn, fake.Calls.Contains("FetchRepoAsync"));
    }

    [Fact]
    public void InitializeAsync_StoredGitHubRefNoCacheSignedOut_KeepsStoredEntry()
    {
        // Signing out wipes the cache; the board must still remember its
        // GitHub keymap for after the next sign-in.
        var settings = StartupSettings();
        // OnLocalPathFor defaults to null — nothing cached; IsSignedIn false.
        var fake = new FakeGitHubKeymapService();
        var keymapState = Ok();
        var vm = new MainWindowViewModel(settings, keymapState: keymapState, gitHub: fake);

        vm.InitializeAsync();

        Assert.Equal(0, keymapState.LoadCallCount);
        Assert.False(vm.HasLayoutLoaded);
        Assert.Equal(TestRefString, settings.Current.LayoutJsonPaths["Corne"]);
    }

    [Fact]
    public async Task InitializeAsync_StoredGitHubRefNoCacheSignedIn_FetchesAndLoadsWithoutToast()
    {
        var settings = StartupSettings();
        var fetched = TempKeymapFile();
        string? cached = null;
        var fake = new FakeGitHubKeymapService
        {
            IsSignedIn = true,
            OnLocalPathFor = _ => cached,
            OnFetchRepo = (_, _, _) =>
            {
                cached = fetched;
                return Task.FromResult(new GitHubCacheResult("folder", "sha", Downloaded: true));
            },
        };
        var keymapState = Ok();
        var vm = new MainWindowViewModel(settings, keymapState: keymapState, gitHub: fake);

        vm.InitializeAsync();
        await vm.PendingGitHubCheck!;

        Assert.True(vm.HasLayoutLoaded);
        Assert.Equal(fetched, vm.LoadedLayoutPath);
        Assert.Equal(TestRefString, settings.Current.LayoutJsonPaths["Corne"]);
        Assert.False(vm.Toast.IsVisible); // nothing was "updated" — it just loaded
    }

    [Fact]
    public async Task LocalLoadAfterGitHub_OnlyLocalRowIsCurrent()
    {
        var settings = new InMemorySettingsService();
        var tempPath = TempKeymapFile();
        var fake = new FakeGitHubKeymapService
        {
            OnOpen = _ => Task.FromResult<GitHubOpenResult>(
                new GitHubOpenResult.Loaded(tempPath, FromCacheOffline: false)),
        };
        var vm = new MainWindowViewModel(settings, keymapState: Ok(), gitHub: fake)
        {
            FileExists = _ => true,
        };
        await vm.OpenGitHubKeymapAsync(R);

        vm.LoadLayoutFromPath("/keymaps/local.keymap");

        var rows = vm.RecentKeymapItems;
        var local = rows.Single(r => !r.IsGitHub);
        var github = rows.Single(r => r.IsGitHub);
        Assert.Equal("/keymaps/local.keymap", local.Path);
        Assert.True(local.IsCurrent);
        Assert.False(github.IsCurrent);
    }

    [Fact]
    public void LoadLayoutFromPath_EntryFailure_PrunesEntryNotPath()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                RecentKeymapPaths = new Dictionary<string, List<string>>
                {
                    ["Corne"] = new() { TestRefString, "/keymaps/kept.keymap" },
                },
            },
        };
        var tempPath = TempKeymapFile(); // exists on disk, but fails to parse
        var vm = new MainWindowViewModel(settings, keymapState: Failing());

        vm.LoadLayoutFromPath(tempPath, TestRefString);

        // The reference entry is pruned; the cache path was never in recents.
        Assert.Equal(new[] { "/keymaps/kept.keymap" }, settings.Current.RecentKeymapPaths["Corne"]);
    }

    [Fact]
    public void GitHubRow_WithoutService_ToastsInsteadOfThrowing()
    {
        var settings = new InMemorySettingsService
        {
            Current = new UserSettings
            {
                RecentKeymapPaths = new Dictionary<string, List<string>>
                {
                    ["Corne"] = new() { TestRefString },
                },
            },
        };
        var keymapState = Ok();
        // No gitHub service at all.
        var vm = new MainWindowViewModel(settings, keymapState: keymapState);

        var row = vm.RecentKeymapItems[0];
        Assert.True(row.IsGitHub);
        Assert.True(row.Exists);

        vm.OpenRecentKeymapCommand.Execute(row);

        Assert.Equal(0, keymapState.LoadCallCount);
        Assert.False(vm.HasLayoutLoaded);
        Assert.True(vm.Toast.IsVisible);
        Assert.Equal(
            "Couldn't open cosmos82.keymap from GitHub: GitHub is not available",
            vm.Toast.Message);
    }
}
