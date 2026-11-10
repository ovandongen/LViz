using CommunityToolkit.Mvvm.Input;
using LViz.App.Services;
using LViz.Core.Diagnostics;
using LViz.Core.GitHub;
using LViz.Core.ZmkConfig;

namespace LViz.App.ViewModels;

/// <summary>
/// The live-features (module setup) entry points: the keymap flyout's item and
/// the status-bar hint for the zmk-config the loaded keymap belongs to, and
/// the wizard both open (<see cref="IMainWindowShell.RunModuleSetupAsync"/>).
/// </summary>
public partial class MainWindowViewModel
{
    // What the last RefreshLiveFeatures() found: the config's identity key
    // (dismissal is stored per key), the context to hand the wizard (null =
    // no config found), and its facts (null = not read or unreadable).
    private string? _liveFeaturesKey;
    private ModuleSetupContext? _liveFeaturesContext;
    private ZmkConfigFacts? _liveFeaturesFacts;
    private bool _liveFeaturesHint;

    /// <summary>A zmk-config was found for the loaded keymap and no keymap
    /// edit is open — the flyout item shows and the command can run.</summary>
    public bool CanSetUpLiveFeatures => _liveFeaturesContext is not null && !IsEditing;

    /// <summary>The status-bar hint: the config is not set up (or set up with
    /// the wrong fork), nothing blocks setup, the user has not hidden the
    /// hint for this config, and no keymap edit is open.</summary>
    public bool ShowLiveFeaturesHint => _liveFeaturesHint && !IsEditing;

    /// <summary>
    /// Re-reads which zmk-config the loaded keymap belongs to and what the
    /// inspector makes of it, then re-raises both properties and the two
    /// commands' enable state. A GitHub entry resolves through the keymap
    /// service's branch cache; anything else walks up from the loaded file.
    /// A config whose files cannot be read keeps the flyout item (the wizard
    /// reports its own errors) and hides the hint (it needs the facts).
    /// </summary>
    private void RefreshLiveFeatures()
    {
        _liveFeaturesKey = null;
        _liveFeaturesContext = null;
        _liveFeaturesFacts = null;
        _liveFeaturesHint = false;

        string? root = null;
        if (GitHubKeymapRef.TryParse(_loadedEntry, out var gitHubRef) && _gitHub is not null)
        {
            var folder = _gitHub.CacheFolderFor(gitHubRef.Owner, gitHubRef.Repo, gitHubRef.Branch);
            if (Directory.Exists(folder))
            {
                root = folder;
                _liveFeaturesKey = $"github:{gitHubRef.Owner}/{gitHubRef.Repo}:{gitHubRef.Branch}";
                _liveFeaturesContext = new ModuleSetupContext(null,
                    new ModuleSetupGitHubTarget(gitHubRef.Owner, gitHubRef.Repo, gitHubRef.Branch),
                    () => Devices.Hid.IsConnected);
            }
        }
        else if (LoadedLayoutPath is { } path)
        {
            root = ZmkConfigLocator.FindRoot(path);
            if (root is not null)
            {
                _liveFeaturesKey = Path.GetFullPath(root);
                _liveFeaturesContext = new ModuleSetupContext(root, null, () => Devices.Hid.IsConnected);
            }
        }

        if (_liveFeaturesContext is not null)
        {
            try
            {
                _liveFeaturesFacts = ZmkConfigInspector.Inspect(root!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn("MainVM", $"Inspecting the zmk-config failed: {ex.Message}");
            }

            _liveFeaturesHint =
                _liveFeaturesFacts is { } facts
                && facts.Blockers.Count == 0
                && facts.Status is ZmkModuleStatus.NotSetUp or ZmkModuleStatus.NeedsFork
                && !_settingsService.Load().DismissedLiveFeatureHints.Contains(_liveFeaturesKey!);
        }

        OnPropertyChanged(nameof(CanSetUpLiveFeatures));
        OnPropertyChanged(nameof(ShowLiveFeaturesHint));
        SetUpLiveFeaturesCommand.NotifyCanExecuteChanged();
        DismissLiveFeaturesHintCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the module setup wizard on the found config, then
    /// re-reads the facts always — the wizard may have saved, and the hint
    /// depends on them.</summary>
    [RelayCommand(CanExecute = nameof(CanSetUpLiveFeatures))]
    private async Task SetUpLiveFeaturesAsync()
    {
        if (_liveFeaturesContext is not { } context) return;
        await _shell.RunModuleSetupAsync(context);
        RefreshLiveFeatures();
    }

    /// <summary>Hide the hint for this config: remember its key (once) and
    /// refresh — the hint stays away across loads and restarts.</summary>
    [RelayCommand]
    private void DismissLiveFeaturesHint()
    {
        if (_liveFeaturesKey is not { } key) return;
        PersistSetting(s => s.DismissedLiveFeatureHints.Contains(key)
            ? s
            : s with { DismissedLiveFeatureHints = [.. s.DismissedLiveFeatureHints, key] });
        RefreshLiveFeatures();
    }
}
