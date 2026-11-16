using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.Flashing;
using LViz.App.Services.GitHub;
using LViz.Core.Diagnostics;
using LViz.Core.GitHub;
using LViz.Core.Settings;
using LViz.Core.ZmkConfig;

namespace LViz.App.ViewModels;

/// <summary>
/// Everything the main window offers around the loaded keymap's zmk-config.
/// The live-features (module setup) entry points: the keymap flyout's item
/// and the status-bar hint, opening the wizard
/// (<see cref="IMainWindowShell.RunModuleSetupAsync"/>). The flashing entry
/// points: the flyout's "Flash keyboard…" and the status-bar line that
/// follows the firmware build of the commit LViz just saved to a GitHub
/// zmk-config, both opening the flash window through
/// <see cref="IMainWindowShell.RunFlashAsync"/>. The status line reads the
/// <see cref="FirmwareBuildWatcher"/>, which only ever watches the loaded
/// config's key and never opens anything by itself. The main view-model
/// feeds it the loaded keymap (<see cref="SetLoadedKeymap"/>), the edit
/// state (<see cref="SetEditing"/>) and the commit a save pushed
/// (<see cref="WatchSavedCommit"/>).
/// </summary>
public sealed partial class FirmwareViewModel : ObservableObject
{
    // What the last config lookup found: the config's identity key
    // (dismissal is stored per key), the context to hand the wizard, its
    // facts (null until the background check is in, and for an unreadable
    // config), whether the hint shows, and what the flash window needs —
    // the folder to inspect and, for a GitHub config, the owner/repo/branch
    // to build from (null for a local one).
    // Null = no zmk-config was found for the loaded keymap.
    private sealed record LiveFeaturesState(
        string Key,
        ModuleSetupContext Context,
        ZmkConfigFacts? Facts,
        bool Hint,
        string Root,
        FlashGitHubTarget? FlashTarget);

    private readonly ISettingsService _settings;
    private readonly IMainWindowShell _shell;

    // GitHub keymap service (the branch cache a GitHub entry resolves to).
    // Null in tests that don't wire it — a GitHub entry then finds no config.
    private readonly IGitHubKeymapService? _gitHub;

    // Follows the Actions build of the commit LViz just saved to a GitHub
    // zmk-config so the status bar can offer the firmware. Null in tests that
    // don't wire it; its Changed event fires on a background thread and is
    // marshalled in the constructor.
    private readonly FirmwareBuildWatcher? _watcher;
    private readonly DeviceStack _devices;

    // The main view-model's key-tracking wrapper: the flash window needs the
    // keyboard connection, which live highlighting owns.
    private readonly Func<Func<Task<bool>>, Task<bool>> _withKeyTracking;

    // The inputs of the last SetLoadedKeymap, replayed by a dismissal or a
    // wizard re-inspect without the main view-model being asked again.
    private string? _entry;
    private string? _loadedPath;

    private LiveFeaturesState? _liveFeatures;

    // Bumped by every config lookup (a load, a dismissal, a wizard
    // re-inspect). A finished background check only counts while it is still
    // the newest one; a result of a superseded lookup is dropped.
    private int _liveFeaturesGeneration;

    // True while a keymap edit is open — every "not while editing" rule.
    private bool _editing;

    // The status line as RefreshFirmwareStatus() last read it. Written on the
    // UI thread only: the watcher raises Changed on a background thread and
    // the constructor marshals every raise through the dispatcher.
    [ObservableProperty] private string? _firmwareStatusText;
    [ObservableProperty] private bool _showFirmwareStatus;
    [ObservableProperty] private bool _isFirmwareStatusLink;

    // The watch whose SignInNeeded link RefreshFirmwareStatus decided to show.
    // The decision is made once per watch, because showing it records the
    // config as reminded (FirmwareSignInHintsShown), and a later repaint of
    // the same watch must keep showing it.
    private FirmwareWatchStatus? _shownSignInWatch;

    public FirmwareViewModel(
        ISettingsService settings,
        IMainWindowShell shell,
        IGitHubKeymapService? gitHub,
        FirmwareBuildWatcher? watcher,
        DeviceStack devices,
        Func<Func<Task<bool>>, Task<bool>> withKeyTracking)
    {
        _settings = settings;
        _shell = shell;
        _gitHub = gitHub;
        _watcher = watcher;
        _devices = devices;
        _withKeyTracking = withKeyTracking;
        if (_watcher is not null)
            _watcher.Changed += () => Dispatcher.UIThread.Post(RefreshFirmwareStatus);
    }

    /// <summary>A zmk-config was found for the loaded keymap and no keymap
    /// edit is open — the flyout item shows and the command can run.</summary>
    public bool CanSetUpLiveFeatures => _liveFeatures is not null && !_editing;

    /// <summary>The status-bar hint: the config is not set up (or set up with
    /// the wrong fork), nothing blocks setup, the user has not hidden the
    /// hint for this config, and no keymap edit is open.</summary>
    public bool ShowLiveFeaturesHint => (_liveFeatures?.Hint ?? false) && !_editing;

    /// <summary>A zmk-config was found for the loaded keymap and no keymap
    /// edit is open — the flyout item shows and the command can run. A local
    /// config opens the flash window in file mode (no GitHub target).</summary>
    public bool CanFlashKeyboard => _liveFeatures is not null && !_editing;

    /// <summary>The newest config check, including its hint follow-up; a
    /// completed task when no zmk-config was found. The app never awaits it
    /// — the check raises the hint by itself when it is in; tests await it
    /// to read the hint deterministically.</summary>
    internal Task InspectionTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Re-reads which zmk-config the keymap loaded from these inputs belongs
    /// to and re-raises the affordances and the commands' enable state —
    /// synchronously, with the hint off. The config's files are then read on
    /// a worker thread (see <see cref="InspectionTask"/>) and the hint is
    /// raised once the newest check is in; a config whose files cannot be
    /// read keeps the flyout item (the wizard reports its own errors) and
    /// hides the hint (it needs the facts). A GitHub entry resolves through
    /// the keymap service's branch cache; anything else walks up from the
    /// loaded file. The main view-model calls this after every load and
    /// unload; a dismissal or wizard re-inspect replays the stored inputs.
    /// </summary>
    public void SetLoadedKeymap(string? entry, string? loadedPath)
    {
        _entry = entry;
        _loadedPath = loadedPath;
        RefreshLiveFeatures();
    }

    /// <summary>Whether a keymap edit is open — both affordances and the
    /// firmware line step aside while it is. Re-raises them and repaints the
    /// status line; it does not re-inspect the config (a save reloads the
    /// keymap through <see cref="SetLoadedKeymap"/>).</summary>
    public void SetEditing(bool editing)
    {
        _editing = editing;

        OnPropertyChanged(nameof(CanSetUpLiveFeatures));
        OnPropertyChanged(nameof(ShowLiveFeaturesHint));
        OnPropertyChanged(nameof(CanFlashKeyboard));
        SetUpLiveFeaturesCommand.NotifyCanExecuteChanged();
        DismissLiveFeaturesHintCommand.NotifyCanExecuteChanged();
        FlashKeyboardCommand.NotifyCanExecuteChanged();
        RefreshFirmwareStatus();
    }

    /// <summary>A save to GitHub just pushed <paramref name="sha"/> on the
    /// keymap's branch: start following that commit's Actions build so the
    /// status bar can offer it once the run is green, and repaint the line.
    /// The line only shows while a keymap of this key is loaded —
    /// <see cref="SetLoadedKeymap"/> drops a watch for another config.</summary>
    public void WatchSavedCommit(GitHubKeymapRef keymap, string sha)
    {
        _watcher?.Watch(
            $"github:{keymap.Owner}/{keymap.Repo}:{keymap.Branch}",
            keymap.Owner, keymap.Repo, keymap.Branch, sha);
        RefreshFirmwareStatus();
    }

    /// <summary>
    /// Works out what the loaded keymap's zmk-config is from the stored
    /// inputs — a GitHub entry resolves through the keymap service's branch
    /// cache, anything else walks up from the loaded file — and starts the
    /// background check of its files (<see cref="InspectAndApplyAsync"/>).
    /// Everything that does not need the facts (both affordances, the build
    /// watch, the status line) is decided and raised here, with the hint off
    /// until the check is in.
    /// </summary>
    private void RefreshLiveFeatures()
    {
        _liveFeatures = null;
        var generation = ++_liveFeaturesGeneration;

        string? key = null;
        string? root = null;
        ModuleSetupContext? context = null;
        FlashGitHubTarget? flashTarget = null;
        if (GitHubKeymapRef.TryParse(_entry, out var gitHubRef) && _gitHub is not null)
        {
            var folder = _gitHub.CacheFolderFor(gitHubRef.Owner, gitHubRef.Repo, gitHubRef.Branch);
            if (Directory.Exists(folder))
            {
                key = $"github:{gitHubRef.Owner}/{gitHubRef.Repo}:{gitHubRef.Branch}";
                root = folder;
                flashTarget = new FlashGitHubTarget(gitHubRef.Owner, gitHubRef.Repo, gitHubRef.Branch);
                context = new ModuleSetupContext(null,
                    new ModuleSetupGitHubTarget(gitHubRef.Owner, gitHubRef.Repo, gitHubRef.Branch),
                    ct => _devices.Hid.QueryConfigIdAsync(TimeSpan.FromSeconds(1), ct));
            }
        }
        else if (_loadedPath is { } path)
        {
            root = ZmkConfigLocator.FindRoot(path);
            if (root is not null)
            {
                key = Path.GetFullPath(root);
                context = new ModuleSetupContext(root, null,
                    ct => _devices.Hid.QueryConfigIdAsync(TimeSpan.FromSeconds(1), ct));
            }
        }

        LiveFeaturesState? state = null;
        if (context is not null)
        {
            // The facts — and with them the hint — come from the check below.
            state = new LiveFeaturesState(key!, context, Facts: null, Hint: false, root!, flashTarget);
            _liveFeatures = state;
        }

        // Another config loaded (or none): a build watch belongs to the config
        // it was saved to, so anything for another key is dropped. The status
        // line follows the (possibly new) key, and the flyout item the
        // (possibly missing) context.
        if (_liveFeatures is { } found) _watcher?.ClearUnless(found.Key);
        else _watcher?.Clear();
        RefreshFirmwareStatus();

        OnPropertyChanged(nameof(CanSetUpLiveFeatures));
        OnPropertyChanged(nameof(ShowLiveFeaturesHint));
        OnPropertyChanged(nameof(CanFlashKeyboard));
        SetUpLiveFeaturesCommand.NotifyCanExecuteChanged();
        DismissLiveFeaturesHintCommand.NotifyCanExecuteChanged();
        FlashKeyboardCommand.NotifyCanExecuteChanged();

        InspectionTask = state is null
            ? Task.CompletedTask
            : InspectAndApplyAsync(generation, state);
    }

    /// <summary>
    /// Reads the config's files on a worker thread and — unless a newer
    /// lookup has taken over in the meantime — stores the facts and raises
    /// the hint. IO and permission failures log a Warn and leave the hint
    /// off; anything else logs an Error. The task never faults: the app does
    /// not await it. The await deliberately has no <c>ConfigureAwait(false)</c>
    /// so the follow-up runs back on the caller's (UI) thread.
    /// </summary>
    private async Task InspectAndApplyAsync(int generation, LiveFeaturesState state)
    {
        try
        {
            var facts = await Task.Run(() =>
            {
                try
                {
                    return (ZmkConfigFacts?)ZmkConfigInspector.Inspect(state.Root);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Warn("MainVM", $"Inspecting the zmk-config failed: {ex.Message}");
                    return null;
                }
            });

            // A newer lookup owns the state from here on; this result says
            // nothing about the keymap shown anymore.
            if (generation != _liveFeaturesGeneration) return;

            var hint =
                facts is { } f
                && f.Blockers.Count == 0
                && f.Status is ZmkModuleStatus.NotSetUp or ZmkModuleStatus.NeedsFork
                && !_settings.Current.DismissedLiveFeatureHints.Contains(state.Key);
            _liveFeatures = state with { Facts = facts, Hint = hint };
            OnPropertyChanged(nameof(ShowLiveFeaturesHint));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("MainVM", $"Inspecting the zmk-config failed: {ex}");
        }
    }

    /// <summary>Opens the module setup wizard on the found config, then
    /// re-reads the facts always — the wizard may have saved, and the hint
    /// depends on them. A wizard save to GitHub starts the firmware build
    /// watch on the commit it committed: the save moved the branch cache's
    /// recorded SHA there, so the fetch straight after answers from the
    /// cache without downloading again.</summary>
    [RelayCommand(CanExecute = nameof(CanSetUpLiveFeatures))]
    private async Task SetUpLiveFeaturesAsync()
    {
        if (_liveFeatures is not { } state) return;
        if (await _shell.RunModuleSetupAsync(state.Context)
            && state.Context.GitHub is { } target && _gitHub is not null && _watcher is not null)
        {
            try
            {
                var cache = await _gitHub.FetchRepoAsync(target.Owner, target.Repo, target.Branch);
                _watcher.Watch(
                    state.Key, target.Owner, target.Repo, target.Branch, cache.Sha);
            }
            // The fetch may write the branch cache, so a disk error is as
            // possible as a GitHub one; either only costs the build watch.
            catch (Exception ex) when (ex is GitHubApiException or IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn("Flash", $"Reading the branch head after module setup failed: {ex.Message}");
            }
        }
        RefreshLiveFeatures();
    }

    /// <summary>Hide the hint for this config: remember its key (once) and
    /// refresh — the hint stays away across loads and restarts.</summary>
    [RelayCommand]
    private void DismissLiveFeaturesHint()
    {
        if (_liveFeatures is not { } state) return;
        PersistSetting(s => s.DismissedLiveFeatureHints.Contains(state.Key)
            ? s
            : s with { DismissedLiveFeatureHints = [.. s.DismissedLiveFeatureHints, state.Key] });
        RefreshLiveFeatures();
    }

    /// <summary>Opens the flash window on the loaded config: its newest
    /// branch build when the config is on GitHub, file mode when it is local.
    /// A finished flash (the window closed as saved) clears the build watch —
    /// what was built is now on the board.</summary>
    [RelayCommand(CanExecute = nameof(CanFlashKeyboard))]
    private async Task FlashKeyboardAsync()
    {
        if (_liveFeatures is not { } state) return;
        if (await RunFlashWindowAsync(new FlashContext(
                state.Key, state.Root, state.FlashTarget, null)))
        {
            _watcher?.Clear();
        }
    }

    /// <summary>
    /// Opens the flash window and returns whether it flashed. The keyboard
    /// connection the window needs (the bootloader command, the central's
    /// report in) is wrapped by the host's key-tracking hook: with
    /// highlighting off, the device stack runs for the window's lifetime
    /// only. A flash marks the config as one the user flashes from LViz
    /// (<see cref="UserSettings.FlashedConfigs"/>).
    /// </summary>
    private async Task<bool> RunFlashWindowAsync(FlashContext context)
    {
        var flashed = await _withKeyTracking(() => _shell.RunFlashAsync(context));

        if (flashed)
        {
            PersistSetting(s => s.FlashedConfigs.Contains(context.ConfigKey)
                ? s
                : s with { FlashedConfigs = [.. s.FlashedConfigs, context.ConfigKey] });
        }

        return flashed;
    }

    /// <summary>
    /// Repaints the status line from <see cref="FirmwareBuildWatcher.Current"/>:
    /// it shows only while the watch belongs to the loaded config's key and no
    /// edit is open, and never for <see cref="FirmwareWatchState.Hidden"/>.
    /// Waiting/Running is a plain "building" line; Ready, Failed and
    /// SignInNeeded are links (see <see cref="FirmwareStatusAsync"/>).
    /// </summary>
    private void RefreshFirmwareStatus()
    {
        var current = _watcher?.Current;
        if (current is null
            || !string.Equals(current.ConfigKey, _liveFeatures?.Key, StringComparison.Ordinal)
            || _editing
            || current.State == FirmwareWatchState.Hidden
            || (current.State == FirmwareWatchState.SignInNeeded && !ShowSignInLink(current)))
        {
            ShowFirmwareStatus = false;
            FirmwareStatusText = null;
            IsFirmwareStatusLink = false;
            return;
        }

        FirmwareStatusText = current.State switch
        {
            FirmwareWatchState.Waiting or FirmwareWatchState.Running
                => Loc.Instance["Status_FirmwareBuilding"],
            FirmwareWatchState.Ready => Loc.Instance["Status_FirmwareReady"],
            FirmwareWatchState.Failed => Loc.Instance["Status_FirmwareFailed"],
            FirmwareWatchState.SignInNeeded => Loc.Instance["Status_FirmwareFlash"],
            _ => null,
        };
        IsFirmwareStatusLink = current.State
            is FirmwareWatchState.Ready
            or FirmwareWatchState.Failed
            or FirmwareWatchState.SignInNeeded;
        ShowFirmwareStatus = FirmwareStatusText is not null;
    }

    /// <summary>
    /// Whether the SignInNeeded link may show for this watch: always for a
    /// config the user has flashed from LViz; otherwise only the first time
    /// for that config, which is recorded so later saves stay quiet.
    /// </summary>
    private bool ShowSignInLink(FirmwareWatchStatus watch)
    {
        if (watch == _shownSignInWatch) return true;
        var settings = _settings.Current;
        if (settings.FlashedConfigs.Contains(watch.ConfigKey)) return true;
        if (settings.FirmwareSignInHintsShown.Contains(watch.ConfigKey)) return false;
        PersistSetting(s => s with { FirmwareSignInHintsShown = [.. s.FirmwareSignInHintsShown, watch.ConfigKey] });
        _shownSignInWatch = watch;
        return true;
    }

    /// <summary>What a click on the status line does: Ready and SignInNeeded
    /// open the flash window on the watched commit's build (a finished flash
    /// clears the watch); Failed opens the failed run on GitHub so the user
    /// can read why. Waiting/Running is not a link and does nothing.</summary>
    [RelayCommand]
    private async Task FirmwareStatusAsync()
    {
        if (_watcher?.Current is not { } current) return;
        if (!string.Equals(current.ConfigKey, _liveFeatures?.Key, StringComparison.Ordinal)
            || _editing)
        {
            return;
        }

        switch (current.State)
        {
            case FirmwareWatchState.Ready or FirmwareWatchState.SignInNeeded:
                if (_liveFeatures is not { } state) return;
                if (await RunFlashWindowAsync(
                        new FlashContext(state.Key, state.Root, state.FlashTarget, current.Sha)))
                {
                    _watcher?.Clear();
                }
                break;
            case FirmwareWatchState.Failed:
                if (current.RunUrl is { } url) _shell.OpenUrl(url);
                break;
        }
    }

    /// <summary>✕ next to the status line: stop watching and hide it.</summary>
    [RelayCommand]
    private void DismissFirmwareStatus() => _watcher?.Clear();

    // Thin wrapper over ISettingsService.Update; the "MainVM" subsystem tag
    // is the one the window's other settings writes use (as in
    // WindowPreferencesViewModel), so a persist failure keeps logging there.
    private void PersistSetting(Func<UserSettings, UserSettings> update) => _settings.Update(update, "MainVM");
}
