using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services.Flashing;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// The flashing entry points: the keymap flyout's "Flash keyboard…" and the
/// status-bar line that follows the firmware build of the commit LViz just
/// saved to a GitHub zmk-config. Both open the flash window through
/// <see cref="IMainWindowShell.RunFlashAsync"/>; the status line reads the
/// <see cref="FirmwareBuildWatcher"/>, which only ever watches the loaded
/// config's key and never opens anything by itself.
/// </summary>
public partial class MainWindowViewModel
{
    // The status line as RefreshFirmwareStatus() last read it. Written on the
    // UI thread only: the watcher raises Changed on a background thread and
    // the constructor marshals every raise through the dispatcher.
    [ObservableProperty] private string? _firmwareStatusText;
    [ObservableProperty] private bool _showFirmwareStatus;
    [ObservableProperty] private bool _isFirmwareStatusLink;

    // The watch whose SignInNeeded link RefreshFirmwareStatus decided to show.
    // The decision is made once per watch, because showing it records the
    // config as reminded (FirmwareSignInHintsShown), and a later repaint of the
    // same watch must keep showing it.
    private FirmwareWatchStatus? _shownSignInWatch;

    /// <summary>A zmk-config was found for the loaded keymap and no keymap
    /// edit is open — the flyout item shows and the command can run. A local
    /// config opens the flash window in file mode (no GitHub target).</summary>
    public bool CanFlashKeyboard => _liveFeaturesContext is not null && !IsEditing;

    /// <summary>Opens the flash window on the loaded config: its newest
    /// branch build when the config is on GitHub, file mode when it is local.
    /// A finished flash (the window closed as saved) clears the build watch —
    /// what was built is now on the board.</summary>
    [RelayCommand(CanExecute = nameof(CanFlashKeyboard))]
    private async Task FlashKeyboardAsync()
    {
        if (_liveFeaturesContext is null) return;
        if (await RunFlashWindowAsync(new FlashContext(
                _liveFeaturesKey!, _liveFeaturesRoot!, _liveFeaturesFlashTarget, null)))
        {
            _firmwareWatcher?.Clear();
        }
    }

    /// <summary>
    /// Opens the flash window and returns whether it flashed. The window needs
    /// the keyboard connection (the bootloader command, the central's report
    /// in), which live highlighting owns: with highlighting off, the device
    /// stack runs for the window's lifetime only. A flash marks the config as
    /// one the user flashes from LViz (<see cref="UserSettings.FlashedConfigs"/>).
    /// </summary>
    private async Task<bool> RunFlashWindowAsync(FlashContext context)
    {
        var startedForFlash = !IsLiveHighlightingEnabled;
        if (startedForFlash) StartKeyEventTracking();
        bool flashed;
        try
        {
            flashed = await _shell.RunFlashAsync(context);
        }
        finally
        {
            // No layer reset as the toggle does: with highlighting off the
            // board view never followed the keyboard, so there is nothing to undo.
            if (startedForFlash && !IsLiveHighlightingEnabled) StopKeyEventTracking();
        }

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
        var current = _firmwareWatcher?.Current;
        if (current is null
            || !string.Equals(current.ConfigKey, _liveFeaturesKey, StringComparison.Ordinal)
            || IsEditing
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
        var settings = _settingsService.Load();
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
        if (_firmwareWatcher?.Current is not { } current) return;
        if (!string.Equals(current.ConfigKey, _liveFeaturesKey, StringComparison.Ordinal)
            || IsEditing)
        {
            return;
        }

        switch (current.State)
        {
            case FirmwareWatchState.Ready or FirmwareWatchState.SignInNeeded:
                if (_liveFeaturesKey is not { } key || _liveFeaturesRoot is not { } root) return;
                if (await RunFlashWindowAsync(
                        new FlashContext(key, root, _liveFeaturesFlashTarget, current.Sha)))
                {
                    _firmwareWatcher?.Clear();
                }
                break;
            case FirmwareWatchState.Failed:
                if (current.RunUrl is { } url) _hostActionExecutor.Launch(url);
                break;
        }
    }

    /// <summary>✕ next to the status line: stop watching and hide it.</summary>
    [RelayCommand]
    private void DismissFirmwareStatus() => _firmwareWatcher?.Clear();
}
