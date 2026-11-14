using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.Flashing;
using LViz.App.Services.GitHub;
using LViz.App.ViewModels;
using LViz.Core.GitHub;
using LViz.Core.Settings;
using LViz.Tests.GitHub;
using LViz.Tests.Keymap;
using LViz.Tests.ZmkConfig;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The flashing entry points on the main window: the flyout item opens the
/// flash window on the loaded config's folder/GitHub target, and the
/// status-bar line follows the firmware build the <see cref="FirmwareBuildWatcher"/>
/// reports for the loaded config's key — shown as building/ready/failed per
/// watch state, hidden for another config's key, while editing or when
/// dismissed; the link flashes (or opens a failed run). The watcher is real
/// over a fake <see cref="IFirmwareBuildService"/> whose answers arrive
/// instantly, so the dispatcher-marshalled refresh runs on the headless
/// dispatcher the <c>[AvaloniaFact]</c> tests pump with
/// <see cref="Dispatcher.UIThread.RunJobs"/>.
/// </summary>
public sealed class MainWindowViewModelFlashingTests : IDisposable
{
    private const string Owner = "ovandongen";
    private const string Repo = "zmk-config-slabwing";
    private const string Branch = "main";
    private const string GitHubKey = $"github:{Owner}/{Repo}:{Branch}";
    private const string Sha = "aa11aa22aa33aa44aa55aa66aa77aa88aa99aa00";
    private const string RunUrl = $"https://github.com/{Owner}/{Repo}/actions/runs/101";

    // Same in-memory settings fake as the live-features tests.
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    private sealed class RecordingHostExecutor : IHostActionExecutor
    {
        public List<string> Launches { get; } = [];

        public void Launch(string target) => Launches.Add(target);

        public void Shell(string command) => throw new NotSupportedException();
    }

    private readonly List<string> _tempDirs = [];
    private readonly List<FirmwareBuildWatcher> _watchers = [];

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Copies the cosmos82 zmk-config fixture to temp and writes the
    /// keymap into its <c>config/</c>; returns (root, keymap path).</summary>
    private (string Root, string KeymapPath) LocalCopy()
    {
        var copy = ZmkConfigFixtures.CopyToTemp("cosmos82");
        _tempDirs.Add(copy);
        return (copy, WriteKeymap(copy, "cosmos82.keymap"));
    }

    /// <summary>A temp copy of the slabwing fixture standing in for a GitHub
    /// branch cache folder; returns the copy and the entry it is the cache of.</summary>
    private string GitHubCopy(out string entry)
    {
        var copy = ZmkConfigFixtures.CopyToTemp("slabwing");
        _tempDirs.Add(copy);
        WriteKeymap(copy, "slabwing.keymap");
        entry = $"github:{Owner}/{Repo}:{Branch}:config/slabwing.keymap";
        return copy;
    }

    private static string WriteKeymap(string root, string keymapName)
    {
        var path = Path.Combine(root, "config", keymapName);
        File.WriteAllText(path, KeymapFixtures.Read(keymapName));
        return path;
    }

    /// <summary>A watcher over instantly-answering builds; the delay completes
    /// after 1 ms but still honours cancellation, as the watcher-test fake's does.</summary>
    private FirmwareBuildWatcher NewWatcher(FakeBuilds builds)
    {
        var watcher = new FirmwareBuildWatcher(builds, (_, ct) => Task.Delay(1, ct));
        _watchers.Add(watcher);
        return watcher;
    }

    /// <summary>Drains dispatcher callbacks posted by the watcher's Changed
    /// marshalling, so the VM's status properties are up to date.</summary>
    private static void FlushDispatcher() => Dispatcher.UIThread.RunJobs();

    // ---- the flyout item ----

    [AvaloniaFact]
    public async Task LocalKeymap_CommandRecordsFileModeContext()
    {
        var shell = new FakeMainWindowShell();
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: shell);
        var (root, path) = LocalCopy();
        vm.LoadLayoutFromPath(path);

        Assert.True(vm.CanFlashKeyboard);

        await vm.FlashKeyboardCommand.ExecuteAsync(null);

        var context = Assert.Single(shell.FlashContexts);
        Assert.Null(context.GitHub);
        Assert.Null(context.Sha);
        Assert.Equal(root, context.ConfigRoot);
    }

    [AvaloniaFact]
    public async Task GitHubKeymap_CommandRecordsTheTargetWithoutASha()
    {
        var copy = GitHubCopy(out var entry);
        var gitHub = new FakeGitHubKeymapService { OnCacheFolderFor = (_, _, _) => copy };
        var shell = new FakeMainWindowShell();
        var vm = TestMainVm.Create(new InMemorySettingsService(), gitHub: gitHub, shell: shell);

        vm.LoadLayoutFromPath(Path.Combine(copy, "config", "slabwing.keymap"), entry);

        Assert.True(vm.CanFlashKeyboard);

        await vm.FlashKeyboardCommand.ExecuteAsync(null);

        var context = Assert.Single(shell.FlashContexts);
        Assert.Equal(new FlashGitHubTarget(Owner, Repo, Branch), context.GitHub);
        Assert.Null(context.Sha);
        Assert.Equal(copy, context.ConfigRoot);
    }

    [Fact]
    public void WhileEditing_TheFlyoutItemIsGatedOff()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService(), shell: new FakeMainWindowShell());
        var (_, path) = LocalCopy();
        vm.LoadLayoutFromPath(path);
        Assert.True(vm.CanFlashKeyboard);

        vm.StartEditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        Assert.False(vm.CanFlashKeyboard);

        vm.DiscardEditCommand.Execute(null);
        Assert.True(vm.CanFlashKeyboard);
    }

    // ---- the status-bar firmware line ----

    [AvaloniaFact]
    public async Task WatcherReady_LinkShows_AndCommandFlashesThatCommit()
    {
        var builds = new FakeBuilds { Responder = _ => Ready() };
        var watcher = NewWatcher(builds);
        var copy = GitHubCopy(out var entry);
        var gitHub = new FakeGitHubKeymapService { OnCacheFolderFor = (_, _, _) => copy };
        var shell = new FakeMainWindowShell { FlashAnswer = true };
        var vm = TestMainVm.Create(
            new InMemorySettingsService(), gitHub: gitHub, shell: shell, firmwareWatcher: watcher);
        vm.LoadLayoutFromPath(Path.Combine(copy, "config", "slabwing.keymap"), entry);

        watcher.Watch(GitHubKey, Owner, Repo, Branch, Sha);
        FlushDispatcher();

        Assert.True(vm.ShowFirmwareStatus);
        Assert.True(vm.IsFirmwareStatusLink);
        Assert.Equal(Loc.Instance["Status_FirmwareReady"], vm.FirmwareStatusText);

        await vm.FirmwareStatusCommand.ExecuteAsync(null);

        var context = Assert.Single(shell.FlashContexts);
        Assert.Equal(Sha, context.Sha);
        Assert.Equal(new FlashGitHubTarget(Owner, Repo, Branch), context.GitHub);
        // The window closed as flashed: the watch is cleared and the line hides.
        Assert.Null(watcher.Current);
        FlushDispatcher();
        Assert.False(vm.ShowFirmwareStatus);
    }

    [AvaloniaFact]
    public void WatcherRunning_PlainBuildingLine()
    {
        var builds = new FakeBuilds { Responder = _ => Running() };
        var watcher = NewWatcher(builds);
        var copy = GitHubCopy(out var entry);
        var gitHub = new FakeGitHubKeymapService { OnCacheFolderFor = (_, _, _) => copy };
        var vm = TestMainVm.Create(
            new InMemorySettingsService(), gitHub: gitHub, firmwareWatcher: watcher);
        vm.LoadLayoutFromPath(Path.Combine(copy, "config", "slabwing.keymap"), entry);

        watcher.Watch(GitHubKey, Owner, Repo, Branch, Sha);
        FlushDispatcher();

        Assert.True(vm.ShowFirmwareStatus);
        Assert.False(vm.IsFirmwareStatusLink);
        Assert.Equal(Loc.Instance["Status_FirmwareBuilding"], vm.FirmwareStatusText);
    }

    [AvaloniaFact]
    public async Task WatcherFailed_TheLinkOpensTheRunPage()
    {
        var builds = new FakeBuilds { Responder = _ => Failed() };
        var watcher = NewWatcher(builds);
        var copy = GitHubCopy(out var entry);
        var gitHub = new FakeGitHubKeymapService { OnCacheFolderFor = (_, _, _) => copy };
        var host = new RecordingHostExecutor();
        var vm = TestMainVm.Create(
            new InMemorySettingsService(), gitHub: gitHub, hostActionExecutor: host,
            firmwareWatcher: watcher);
        vm.LoadLayoutFromPath(Path.Combine(copy, "config", "slabwing.keymap"), entry);

        watcher.Watch(GitHubKey, Owner, Repo, Branch, Sha);
        FlushDispatcher();

        Assert.True(vm.ShowFirmwareStatus);
        Assert.True(vm.IsFirmwareStatusLink);
        Assert.Equal(Loc.Instance["Status_FirmwareFailed"], vm.FirmwareStatusText);

        await vm.FirmwareStatusCommand.ExecuteAsync(null);

        Assert.Equal(RunUrl, Assert.Single(host.Launches));
    }

    [AvaloniaFact]
    public void WatcherOnAnotherConfig_HidesTheLine_AndReloadingClearsIt()
    {
        var builds = new FakeBuilds { Responder = _ => Ready() };
        var watcher = NewWatcher(builds);
        var vm = TestMainVm.Create(
            new InMemorySettingsService(), shell: new FakeMainWindowShell(), firmwareWatcher: watcher);
        var (_, path) = LocalCopy();
        vm.LoadLayoutFromPath(path);

        watcher.Watch("github:someone/else:main", "someone", "else", "main", Sha);
        FlushDispatcher();

        // A watch that belongs to another config never shows on this one.
        Assert.NotNull(watcher.Current);
        Assert.False(vm.ShowFirmwareStatus);

        // Reloading re-runs RefreshLiveFeatures, which drops a foreign watch.
        vm.LoadLayoutFromPath(path);
        Assert.Null(watcher.Current);
    }

    [AvaloniaFact]
    public void Dismiss_StopsWatchingAndHides()
    {
        var builds = new FakeBuilds { Responder = _ => Ready() };
        var watcher = NewWatcher(builds);
        var copy = GitHubCopy(out var entry);
        var gitHub = new FakeGitHubKeymapService { OnCacheFolderFor = (_, _, _) => copy };
        var vm = TestMainVm.Create(
            new InMemorySettingsService(), gitHub: gitHub, firmwareWatcher: watcher);
        vm.LoadLayoutFromPath(Path.Combine(copy, "config", "slabwing.keymap"), entry);

        watcher.Watch(GitHubKey, Owner, Repo, Branch, Sha);
        FlushDispatcher();
        Assert.True(vm.ShowFirmwareStatus);

        vm.DismissFirmwareStatusCommand.Execute(null);
        FlushDispatcher();

        Assert.Null(watcher.Current);
        Assert.False(vm.ShowFirmwareStatus);
    }

    // ---- instant build answers ----

    private static FirmwareBuild Ready() =>
        new(FirmwareBuildState.Ready, Run("completed", "success"), 4001, false);

    private static FirmwareBuild Running() =>
        new(FirmwareBuildState.Running, Run("in_progress", null), null, false);

    private static FirmwareBuild Failed() =>
        new(FirmwareBuildState.Failed, Run("completed", "failure"), null, false);

    private static GitHubWorkflowRun Run(string status, string? conclusion) =>
        new(
            101,
            "Build firmware",
            Branch,
            Sha,
            status,
            conclusion,
            RunUrl,
            new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero),
            null);

    /// <summary>Minimal builds service for the watcher: signed in, answers
    /// <c>FindBuildAsync</c> from a responder, refuses everything else — the
    /// watcher's own fake in <c>FirmwareBuildWatcherTests</c> in miniature.</summary>
    private sealed class FakeBuilds : IFirmwareBuildService
    {
        public Func<string, FirmwareBuild> Responder { get; set; } =
            _ => new FirmwareBuild(FirmwareBuildState.NoRun, null, null, false);

        public bool SignedIn { get; set; } = true;

        public string? Login => SignedIn ? "octocat" : null;

        public bool IsSignedIn => SignedIn;

        public bool IsLoginPersisted => false;

        // The watcher never subscribes to login changes; nothing raises this.
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public Task EnsureRestoredAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<FirmwareBuild> FindBuildAsync(
            string owner, string repo, string? headSha, string? branch, CancellationToken ct = default) =>
            Task.FromResult(Responder(headSha!));

        public Task<GitHubDeviceCode> StartSignInAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SignOutAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FirmwareDownloadResult> DownloadAsync(
            string owner, string repo, string sha, long artifactId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PeripheralAdvice> AdvisePeripheralAsync(
            string owner, string repo, string? lastFlashedSha, string buildSha,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
