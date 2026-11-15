using LViz.App.Services;
using LViz.App.Services.GitHub;
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
/// settings, and hidden while editing. The wizard itself is exercised by the
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

        Assert.True(vm.CanSetUpLiveFeatures);
        Assert.True(vm.ShowLiveFeaturesHint);

        await vm.SetUpLiveFeaturesCommand.ExecuteAsync(null);

        var context = Assert.Single(shell.ModuleSetupContexts);
        Assert.Equal(root, context.LocalRoot);
        Assert.Null(context.GitHub);
    }

    [Fact]
    public void Glove80Copy_ItemAtCommand_HintHiddenBecauseSetUp()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        var (_, path) = KeymapCopy("glove80");

        vm.LoadLayoutFromPath(path);

        Assert.True(vm.CanSetUpLiveFeatures);
        Assert.False(vm.ShowLiveFeaturesHint);
    }

    [Fact]
    public void DongleCopy_HintHiddenBecauseBlocked()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        var (_, path) = KeymapCopy("dongle");

        vm.LoadLayoutFromPath(path);

        Assert.True(vm.CanSetUpLiveFeatures);
        Assert.False(vm.ShowLiveFeaturesHint);
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

        Assert.False(vm.CanSetUpLiveFeatures);
        Assert.False(vm.ShowLiveFeaturesHint);
        Assert.False(vm.SetUpLiveFeaturesCommand.CanExecute(null));
    }

    [Fact]
    public void Dismiss_HidesHintAndStoresKey_FreshVmOnSameSettingsKeepsItHidden()
    {
        var settings = new InMemorySettingsService();
        var vm = TestMainVm.Create(settings, shell: new FakeMainWindowShell());
        var (root, path) = KeymapCopy("cosmos82");
        vm.LoadLayoutFromPath(path);
        Assert.True(vm.ShowLiveFeaturesHint);

        vm.DismissLiveFeaturesHintCommand.Execute(null);

        Assert.False(vm.ShowLiveFeaturesHint);
        Assert.True(vm.CanSetUpLiveFeatures);
        Assert.Equal(new[] { root }, settings.Current.DismissedLiveFeatureHints);

        var vm2 = TestMainVm.Create(settings, shell: new FakeMainWindowShell());
        vm2.LoadLayoutFromPath(path);
        Assert.True(vm2.CanSetUpLiveFeatures);
        Assert.False(vm2.ShowLiveFeaturesHint);
    }

    [Fact]
    public async Task WizardSaved_ReInspectClearsTheHint()
    {
        var shell = new SavingShell { OnRun = ctx => ReplaceInspectedFiles(ctx.LocalRoot!, "glove80") };
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: shell);
        var (root, path) = KeymapCopy("cosmos82");
        vm.LoadLayoutFromPath(path);
        Assert.True(vm.ShowLiveFeaturesHint);

        await vm.SetUpLiveFeaturesCommand.ExecuteAsync(null);

        Assert.Equal(root, Assert.Single(shell.Contexts).LocalRoot);
        Assert.True(vm.CanSetUpLiveFeatures);
        Assert.False(vm.ShowLiveFeaturesHint);
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

        await vm.SetUpLiveFeaturesCommand.ExecuteAsync(null);

        var context = Assert.Single(shell.ModuleSetupContexts);
        Assert.Null(context.LocalRoot);
        Assert.Equal(new ModuleSetupGitHubTarget("ovandongen", "zmk-config-slabwing", "main"), context.GitHub);
    }

    [Fact]
    public void BoardSwitch_UnloadsTheKeymapAndClearsBoth()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        var (_, path) = KeymapCopy("cosmos82");
        vm.LoadLayoutFromPath(path);
        Assert.True(vm.CanSetUpLiveFeatures);
        Assert.True(vm.ShowLiveFeaturesHint);

        vm.SelectKeyboardCommand.Execute(KeyboardProfileRegistry.All.First(p => p.Id == "GO60"));

        Assert.False(vm.CanSetUpLiveFeatures);
        Assert.False(vm.ShowLiveFeaturesHint);
    }

    [Fact]
    public void StartingEdit_HidesBoth_LeavingTheEditShowsThemAgain()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        var (_, path) = KeymapCopy("cosmos82");
        vm.LoadLayoutFromPath(path);
        Assert.True(vm.ShowLiveFeaturesHint);

        vm.StartEditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        Assert.False(vm.CanSetUpLiveFeatures);
        Assert.False(vm.ShowLiveFeaturesHint);

        vm.DiscardEditCommand.Execute(null);
        Assert.False(vm.IsEditing);
        Assert.True(vm.CanSetUpLiveFeatures);
        Assert.True(vm.ShowLiveFeaturesHint);
    }
}
