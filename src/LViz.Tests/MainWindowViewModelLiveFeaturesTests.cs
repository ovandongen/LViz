using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Layout;
using LViz.Core.Settings;
using LViz.Core.ZmkConfig;
using LViz.Tests.GitHub;
using LViz.Tests.Keymap;
using LViz.Tests.ZmkConfig;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The live-features entry points on the main window: the flyout item and the
/// status-bar hint follow the loaded keymap's zmk-config — shown for a stock
/// config, hidden for a set-up or blocked one, dismissed per config through
/// settings, and hidden while editing. The config's files are read on a
/// worker thread, so a test that reads the hint first awaits
/// <c>vm.Firmware.InspectionTask</c>; the flyout item is decided
/// synchronously by the path checks. The wizard itself is exercised by the
/// module-setup tests; here the shell just records the context it was handed.
/// </summary>
public sealed class MainWindowViewModelLiveFeaturesTests : IDisposable
{
    /// <summary>Wraps the fake shell, runs a mutation on the config before
    /// answering "the wizard saved" — how a test simulates a wizard save
    /// without opening the wizard.</summary>
    private sealed class SavingShell : IMainWindowShell
    {
        private readonly FakeMainWindowShell _inner = new();
        public Action<ModuleSetupContext>? OnRun { get; set; }
        public List<ModuleSetupContext> Contexts => _inner.ModuleSetupContexts;

        public Task<bool> RunModuleSetupAsync(ModuleSetupContext context)
        {
            OnRun?.Invoke(context);
            return _inner.RunModuleSetupAsync(context);
        }

        public Task<bool> RunFlashAsync(FlashContext context) => _inner.RunFlashAsync(context);

        public Task<string?> PickUf2FileAsync() => _inner.PickUf2FileAsync();

        public void OpenUrl(string url) => _inner.OpenUrl(url);

        public void Quit() => _inner.Quit();
        public void ShowWindow() => _inner.ShowWindow();
        public Task<string?> PickKeymapFileAsync() => _inner.PickKeymapFileAsync();
        public Task<bool> CopyTextAsync(string text) => _inner.CopyTextAsync(text);
        public void OpenSettings() => _inner.OpenSettings();
        public Task ShowShortcutsAsync() => _inner.ShowShortcutsAsync();
        public Task<LayoutWizardResult?> RunLayoutWizardAsync(LayoutWizardContext context)
            => _inner.RunLayoutWizardAsync(context);
        public Task<GitHubOpenSelection?> PickGitHubKeymapAsync(Func<IReadOnlyCollection<string>> attachedHidNames)
            => _inner.PickGitHubKeymapAsync(attachedHidNames);
        public Task<UserLayoutFile?> EditLayoutAsync(LayoutEditContext context)
            => _inner.EditLayoutAsync(context);
        public Task<LabelEditAnswer<KeyLabelOverride>?> EditKeyLabelAsync(
            int layerIndex, int keyIndex, string defaultHint, KeyLabelOverride? existing)
            => _inner.EditKeyLabelAsync(layerIndex, keyIndex, defaultHint, existing);
        public Task<LabelEditAnswer<ComboLabelOverride>?> EditComboLabelAsync(
            int number, string keyPositionsKey, string defaultLabel, string participatingKeys,
            ComboLabelOverride? existing)
            => _inner.EditComboLabelAsync(number, keyPositionsKey, defaultLabel, participatingKeys, existing);
    }

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Copies a zmk-config fixture to temp and writes the cosmos82
    /// keymap into its <c>config/</c>; returns (root, keymap path).</summary>
    private (string Root, string KeymapPath) KeymapCopy(string configName)
    {
        var copy = ZmkConfigFixtures.CopyToTemp(configName);
        _tempDirs.Add(copy);
        var path = WriteKeymap(copy, "cosmos82.keymap");
        return (copy, path);
    }

    private static string WriteKeymap(string root, string keymapName)
    {
        var path = Path.Combine(root, "config", keymapName);
        File.WriteAllText(path, KeymapFixtures.Read(keymapName));
        return path;
    }

    /// <summary>Turns a config copy into the <paramref name="from"/> fixture's
    /// file-for-file equivalent of everything <see cref="ZmkConfigFileSet.Read"/>
    /// inspects, so the inspector sees that fixture's status (glove80 = set up).</summary>
    private static void ReplaceInspectedFiles(string copy, string from)
    {
        var source = ZmkConfigFileSet.Read(ZmkConfigFixtures.Root(from));
        var target = ZmkConfigFileSet.Read(copy);
        foreach (var (key, _) in target.Files)
        {
            var local = LocalOf(copy, key);
            if (!source.Files.TryGetValue(key, out var text)) File.Delete(local);
            else File.WriteAllText(local, text);
        }
        foreach (var (key, text) in source.Files)
        {
            if (target.Files.ContainsKey(key)) continue;
            var local = LocalOf(copy, key);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, text);
        }
    }

    private static string LocalOf(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Cosmos82Copy_ItemAndHintShow_CommandRecordsTheLocalRoot()
    {
        var settings = new InMemorySettingsService();
        var shell = new FakeMainWindowShell();
        var vm = TestMainVm.Create(settings, shell: shell);
        var (root, path) = KeymapCopy("cosmos82");

        vm.LoadLayoutFromPath(path);
        await vm.Firmware.InspectionTask;

        Assert.True(vm.Firmware.CanSetUpLiveFeatures);
        Assert.True(vm.Firmware.ShowLiveFeaturesHint);

        await vm.Firmware.SetUpLiveFeaturesCommand.ExecuteAsync(null);

        var context = Assert.Single(shell.ModuleSetupContexts);
        Assert.Equal(root, context.LocalRoot);
        Assert.Null(context.GitHub);
    }

    [Fact]
    public async Task Glove80Copy_ItemAtCommand_HintHiddenBecauseSetUp()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        var (_, path) = KeymapCopy("glove80");

        vm.LoadLayoutFromPath(path);
        await vm.Firmware.InspectionTask;

        Assert.True(vm.Firmware.CanSetUpLiveFeatures);
        Assert.False(vm.Firmware.ShowLiveFeaturesHint);
    }

    [Fact]
    public async Task DongleCopy_HintHiddenBecauseBlocked()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        var (_, path) = KeymapCopy("dongle");

        vm.LoadLayoutFromPath(path);
        await vm.Firmware.InspectionTask;

        Assert.True(vm.Firmware.CanSetUpLiveFeatures);
        Assert.False(vm.Firmware.ShowLiveFeaturesHint);
    }

    [Fact]
    public void KeymapOutsideAnyZmkConfig_NothingShows_CommandCannotExecute()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lviz-livefeatures-plain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var path = Path.Combine(dir, "cosmos82.keymap");
        File.WriteAllText(path, KeymapFixtures.Read("cosmos82.keymap"));

        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        vm.LoadLayoutFromPath(path);

        Assert.False(vm.Firmware.CanSetUpLiveFeatures);
        Assert.False(vm.Firmware.ShowLiveFeaturesHint);
        Assert.False(vm.Firmware.SetUpLiveFeaturesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Dismiss_HidesHintAndStoresKey_FreshVmOnSameSettingsKeepsItHidden()
    {
        var settings = new InMemorySettingsService();
        var vm = TestMainVm.Create(settings, shell: new FakeMainWindowShell());
        var (root, path) = KeymapCopy("cosmos82");
        vm.LoadLayoutFromPath(path);
        await vm.Firmware.InspectionTask;
        Assert.True(vm.Firmware.ShowLiveFeaturesHint);

        vm.Firmware.DismissLiveFeaturesHintCommand.Execute(null);
        await vm.Firmware.InspectionTask; // the dismissal re-reads the config

        Assert.False(vm.Firmware.ShowLiveFeaturesHint);
        Assert.True(vm.Firmware.CanSetUpLiveFeatures);
        Assert.Equal(new[] { root }, settings.Current.DismissedLiveFeatureHints);

        var vm2 = TestMainVm.Create(settings, shell: new FakeMainWindowShell());
        vm2.LoadLayoutFromPath(path);
        await vm2.Firmware.InspectionTask;
        Assert.True(vm2.Firmware.CanSetUpLiveFeatures);
        Assert.False(vm2.Firmware.ShowLiveFeaturesHint);
    }

    [Fact]
    public async Task WizardSaved_ReInspectClearsTheHint()
    {
        var shell = new SavingShell { OnRun = ctx => ReplaceInspectedFiles(ctx.LocalRoot!, "glove80") };
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: shell);
        var (root, path) = KeymapCopy("cosmos82");
        vm.LoadLayoutFromPath(path);
        await vm.Firmware.InspectionTask;
        Assert.True(vm.Firmware.ShowLiveFeaturesHint);

        await vm.Firmware.SetUpLiveFeaturesCommand.ExecuteAsync(null);
        await vm.Firmware.InspectionTask; // the saved wizard re-reads the config

        Assert.Equal(root, Assert.Single(shell.Contexts).LocalRoot);
        Assert.True(vm.Firmware.CanSetUpLiveFeatures);
        Assert.False(vm.Firmware.ShowLiveFeaturesHint);
    }

    [Fact]
    public async Task GitHubEntry_CommandRecordsTheGitHubTargetNotALocalRoot()
    {
        var copy = ZmkConfigFixtures.CopyToTemp("slabwing");
        _tempDirs.Add(copy);
        var cached = WriteKeymap(copy, "slabwing.keymap");
        var entry = "github:ovandongen/zmk-config-slabwing:main:config/slabwing.keymap";
        var gitHub = new FakeGitHubKeymapService { OnCacheFolderFor = (_, _, _) => copy };
        var shell = new FakeMainWindowShell();
        var vm = TestMainVm.Create(new InMemorySettingsService(), gitHub: gitHub, shell: shell);

        vm.LoadLayoutFromPath(cached, entry);

        await vm.Firmware.SetUpLiveFeaturesCommand.ExecuteAsync(null);

        var context = Assert.Single(shell.ModuleSetupContexts);
        Assert.Null(context.LocalRoot);
        Assert.Equal(new ModuleSetupGitHubTarget("ovandongen", "zmk-config-slabwing", "main"), context.GitHub);
    }

    [Fact]
    public async Task BoardSwitch_UnloadsTheKeymapAndClearsBoth()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        var (_, path) = KeymapCopy("cosmos82");
        vm.LoadLayoutFromPath(path);
        await vm.Firmware.InspectionTask;
        Assert.True(vm.Firmware.CanSetUpLiveFeatures);
        Assert.True(vm.Firmware.ShowLiveFeaturesHint);

        vm.SelectKeyboardCommand.Execute(KeyboardProfileRegistry.Bundled.First(p => p.Id == "GO60"));

        Assert.False(vm.Firmware.CanSetUpLiveFeatures);
        Assert.False(vm.Firmware.ShowLiveFeaturesHint);
    }

    [Fact]
    public async Task StartingEdit_HidesBoth_LeavingTheEditShowsThemAgain()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        var (_, path) = KeymapCopy("cosmos82");
        vm.LoadLayoutFromPath(path);
        await vm.Firmware.InspectionTask;
        Assert.True(vm.Firmware.ShowLiveFeaturesHint);

        vm.StartEditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        Assert.False(vm.Firmware.CanSetUpLiveFeatures);
        Assert.False(vm.Firmware.ShowLiveFeaturesHint);

        vm.DiscardEditCommand.Execute(null);
        Assert.False(vm.IsEditing);
        Assert.True(vm.Firmware.CanSetUpLiveFeatures);
        Assert.True(vm.Firmware.ShowLiveFeaturesHint);
    }

    /// <summary>The one edit lifecycle over all three affordances (the setup
    /// item, the hint and the flash item) and their commands' enable state —
    /// the child VM's <c>SetEditing</c> drives the whole set, not just the
    /// half each sibling test covers.</summary>
    [Fact]
    public async Task Editing_HidesBothAffordances_AndLeavingShowsThemAgain()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        var (_, path) = KeymapCopy("cosmos82");
        vm.LoadLayoutFromPath(path);
        await vm.Firmware.InspectionTask;
        Assert.True(vm.Firmware.CanSetUpLiveFeatures);
        Assert.True(vm.Firmware.ShowLiveFeaturesHint);
        Assert.True(vm.Firmware.CanFlashKeyboard);

        vm.StartEditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        Assert.False(vm.Firmware.CanSetUpLiveFeatures);
        Assert.False(vm.Firmware.ShowLiveFeaturesHint);
        Assert.False(vm.Firmware.CanFlashKeyboard);
        Assert.False(vm.Firmware.SetUpLiveFeaturesCommand.CanExecute(null));
        Assert.False(vm.Firmware.FlashKeyboardCommand.CanExecute(null));

        vm.DiscardEditCommand.Execute(null);
        Assert.False(vm.IsEditing);
        Assert.True(vm.Firmware.CanSetUpLiveFeatures);
        Assert.True(vm.Firmware.ShowLiveFeaturesHint);
        Assert.True(vm.Firmware.CanFlashKeyboard);
        Assert.True(vm.Firmware.SetUpLiveFeaturesCommand.CanExecute(null));
        Assert.True(vm.Firmware.FlashKeyboardCommand.CanExecute(null));
    }

    /// <summary>The flyout item comes from the path checks and is there the
    /// moment the keymap is loaded; the hint is only decided once the
    /// background config check is in.</summary>
    [Fact]
    public async Task Load_HintIsOffUntilTheCheckFinishes_ThenOn()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        var (_, path) = KeymapCopy("cosmos82");

        vm.LoadLayoutFromPath(path);

        // Synchronously after the load: the flyout item is in, the check is
        // still running.
        Assert.True(vm.Firmware.CanSetUpLiveFeatures);

        await vm.Firmware.InspectionTask;

        Assert.True(vm.Firmware.ShowLiveFeaturesHint);
    }

    /// <summary>Two loads started back to back let both checks run at once;
    /// only the newest one may set the hint, whatever order they finish in.</summary>
    [Fact]
    public async Task TwoLoadsInARow_OnlyTheSecondDecidesTheHint()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        var (_, needsSetup) = KeymapCopy("cosmos82");
        var (_, isSetUp) = KeymapCopy("glove80");

        vm.LoadLayoutFromPath(needsSetup);
        vm.LoadLayoutFromPath(isSetUp);

        await vm.Firmware.InspectionTask;

        // The set-up config is shown, so its result holds — the first
        // config's check (which would set the hint) is dropped.
        Assert.True(vm.Firmware.CanSetUpLiveFeatures);
        Assert.False(vm.Firmware.ShowLiveFeaturesHint);
    }

    /// <summary>No zmk-config: there is nothing to check, so the task the
    /// tests await is already completed.</summary>
    [Fact]
    public void Load_WithoutAConfig_InspectionTaskIsCompleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lviz-livefeatures-notask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var path = Path.Combine(dir, "cosmos82.keymap");
        File.WriteAllText(path, KeymapFixtures.Read("cosmos82.keymap"));

        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        vm.LoadLayoutFromPath(path);

        Assert.True(vm.Firmware.InspectionTask.IsCompleted);
        Assert.False(vm.Firmware.CanSetUpLiveFeatures);
        Assert.False(vm.Firmware.ShowLiveFeaturesHint);
    }

    /// <summary>A config whose files cannot be read: the check logs and
    /// leaves the hint off, while the flyout item — decided by the path
    /// checks alone — stays.</summary>
    [Fact]
    public async Task UnreadableConfig_HintOff_FlyoutItemStays()
    {
        var (root, path) = KeymapCopy("cosmos82");
        // Hold the manifest with no sharing: the lookup's read meets an
        // IO error, while the path checks (existence only) stay unaffected.
        using var hold = new FileStream(
            Path.Combine(root, "config", "west.yml"), FileMode.Open, FileAccess.Read, FileShare.None);

        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        vm.LoadLayoutFromPath(path);

        await vm.Firmware.InspectionTask;

        Assert.False(vm.Firmware.ShowLiveFeaturesHint);
        Assert.True(vm.Firmware.CanSetUpLiveFeatures);
        Assert.True(vm.Firmware.SetUpLiveFeaturesCommand.CanExecute(null));
    }
}
