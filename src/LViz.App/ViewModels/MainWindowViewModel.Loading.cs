using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.GitHub;
using LViz.App.Services.MouseIdle;
using LViz.Core.Diagnostics;
using LViz.Core.GitHub;
using LViz.Core.Input;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Layout;
using LViz.Core.Models;
using LViz.Core.Persistence;
using LViz.Core.Settings;
using ZmkHidProtocol.ActiveWindow;

namespace LViz.App.ViewModels;

/// <summary>Keymap loading: local and GitHub loads, the unload paths, and the diagnostics snapshot.</summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Loads the keymap file at <paramref name="path"/> and books
    /// <paramref name="entry"/> (defaulting to the path) as its settings
    /// identity — for a GitHub keymap the path is the local cache file while
    /// the entry stays the <see cref="GitHubKeymapRef"/> string, so recents
    /// and the per-profile last-used slot keep storing the reference, not a
    /// cache path the cache layer may move or wipe.
    /// <c>keepBoard</c> is true for a board's <em>booked</em> keymap — the one
    /// LViz loads by itself for the board that is already showing (startup,
    /// Refresh, the background GitHub check, a board switch's auto-load). Such
    /// a load must never switch the board: a booked keymap that doesn't fit
    /// its board is dropped instead of jumping to the board whose key count
    /// matches. Every explicit load the user picks keeps the auto-switch.
    /// </summary>
    public void LoadLayoutFromPath(string path, string? entry = null, bool keepBoard = false)
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "LoadLayoutFromPath ignored while editing");
            return;
        }

        entry ??= path;

        // Parse + commit happens first and is the only fallible step. On failure
        // nothing else runs, so the profile / settings are never half-mutated.
        var result = _keymap.Load(path, _profile.Id);
        if (result is not KeymapLoadResult.Success loaded)
        {
            var error = ((KeymapLoadResult.Failure)result).Error;
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_LoadErrorFormat", error.Message);
            DiagnosticLog.Error("MainVM", $"Load failed: {error}");
            Toast.Show(Loc.Instance.Format("Toast_LoadFailedFormat", Path.GetFileName(path), error.Message));
            // A keymap that no longer loads shouldn't linger in "recent" —
            // the *entry* is what recents store, so that's what gets pruned.
            // Skip the write entirely when the entry isn't listed.
            Library.RemoveRecent(_profile.Id, entry);
            Recents.Refresh();
            return;
        }

        var bindingCount = loaded.FirstLayerBindingCount;
        var keyCountMismatch = loaded.LayerCount > 0 && bindingCount != _profile.KeyCount;
        // A booked keymap belongs to the board that is already showing — it
        // never gets to move the board elsewhere.
        if (keyCountMismatch && keepBoard)
        {
            DropBookedKeymap(path, entry, bindingCount);
            return;
        }
        IKeyboardProfile? autoSwitchedTo = null;
        if (keyCountMismatch)
        {
            var matching = KeyboardProfileRegistry.FindByKeyCount(bindingCount, Devices.AttachedHidNames());
            if (matching is not null)
            {
                UseProfile(matching);
                PersistSetting(s => s with { Keyboard = matching.Id });
                // Re-scope HID discovery and the bus exclusion, like a picked board.
                Devices.SwitchProfile(matching);
                autoSwitchedTo = matching;
                DiagnosticLog.Info("MainVM",
                    $"Auto-switched profile to {matching.Id} ({bindingCount} keys) on load");
            }
            else
            {
                DiagnosticLog.Warn("MainVM",
                    $"Loaded layout has {bindingCount} keys but no profile matches; staying on {_profile.Id}");
            }
        }
        // An orphaned keymap (mismatch, nothing to switch to) arms the
        // status-bar wizard link; every other successful load disarms it.
        HasUnmatchedKeymap = keyCountMismatch && autoSwitchedTo is null;

        LayerTabs.Rebuild(_keymap.Current);
        ApplyActiveLayer(0);
        Board.ComboLegend.Rebuild(_keymap.Current);
        HasLayoutLoaded = true;

        // Booked for the profile the keymap ended up on (_profile is already
        // the auto-switch target when one ran). The *entry*, not the loaded
        // path — see the method doc.
        _loadedEntry = entry;
        Library.Book(_profile.Id, entry);
        Recents.Refresh();

        var baseMsg = Loc.Instance.Format("Status_Loaded",
            Path.GetFileName(path), loaded.LayerCount);
        if (autoSwitchedTo is not null)
        {
            baseMsg += " — " + Loc.Instance.Format("Status_AutoSwitchedKeyboard",
                autoSwitchedTo.DisplayName);
        }
        else if (keyCountMismatch)
        {
            baseMsg += " — " + Loc.Instance.Format("Status_LoadKeyCountMismatch",
                bindingCount, _profile.DisplayName, _profile.KeyCount);
        }
        SetLoadStatusBase(baseMsg);
        StatusMessage = baseMsg;
        DiagnosticLog.Info("MainVM", $"Loaded '{path}'");

        // The edit button tracks the loaded file: enabled for a fresh .keymap,
        // tooltip wording per extension. HasLayoutLoaded may not have flipped,
        // so raise explicitly on every successful load.
        OnPropertyChanged(nameof(CanStartEdit));
        OnPropertyChanged(nameof(EditButtonTooltip));
        StartEditCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Tabs, layer state and load flags back to a bare board. Shared by the
    /// two paths that unload a keymap (<see cref="DropBookedKeymap"/> and
    /// <see cref="SwitchBoard"/>'s unload branch); each sets its own status
    /// message afterwards.
    /// </summary>
    private void ClearLoadedKeymap()
    {
        _keymap.Clear();
        _loadedEntry = null;
        LayerTabs.Clear();
        ActiveLayerIndex = 0;
        HasLayoutLoaded = false;
        HasUnmatchedKeymap = false;
    }

    /// <summary>
    /// A booked keymap that doesn't fit its board (<see cref="LoadLayoutFromPath"/>
    /// with <c>keepBoard</c>): the keymap is unloaded, the board stays put and
    /// the board's booking is dropped, so neither this session nor the next
    /// startup keeps offering a keymap that can't be shown. The board's recents
    /// list keeps the entry — the user can still open it by hand, where the
    /// auto-switch is allowed.
    /// </summary>
    private void DropBookedKeymap(string path, string entry, int bindingCount)
    {
        ClearLoadedKeymap();

        // Only this load's own entry is unbooked; a booking that already points
        // somewhere else belongs to another keymap.
        Library.Unbook(_profile.Id, entry);

        SetLoadStatusBase(null);
        StatusMessage = Loc.Instance.Format(
            "Status_BookedKeymapDoesNotFitFormat",
            Path.GetFileName(path), bindingCount, _profile.DisplayName, _profile.KeyCount);
        DiagnosticLog.Info("MainVM",
            $"Booked keymap '{path}' has {bindingCount} keys but {_profile.Id} has {_profile.KeyCount}: kept the board, dropped the booking");

        // The board is bare now, so the edit button has to notice.
        OnPropertyChanged(nameof(CanStartEdit));
        OnPropertyChanged(nameof(EditButtonTooltip));
        StartEditCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Opens a GitHub keymap: refreshes its repo through
    /// <see cref="IGitHubKeymapService"/> and loads the resulting local file
    /// under the reference as its settings entry (offline cache copies load
    /// too, with a toast). Failures only toast — the entry stays in recents
    /// because the repo may just be temporarily unreachable. An
    /// <see cref="GitHubErrorKind.Unauthorized"/> additionally raises
    /// the shell's GitHub picker so the sign-in window can open.
    /// </summary>
    public async Task OpenGitHubKeymapAsync(GitHubKeymapRef keymap)
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "OpenGitHubKeymapAsync ignored while editing");
            return;
        }

        if (_gitHub is null)
        {
            Toast.Show(Loc.Instance.Format("Toast_GitHubFailedFormat", keymap.FileName, "GitHub is not available"));
            return;
        }

        // Keep what the bar said before "Loading…" so a failure can put it
        // back instead of leaving a stale progress message behind.
        var statusBefore = StatusMessage;
        SetLoadStatusBase(null);
        StatusMessage = Loc.Instance.Format("Status_GitHubLoadingFormat", keymap.FileName);
        DiagnosticLog.Info("MainVM", $"Opening '{keymap}' from GitHub");

        var result = await _gitHub.OpenAsync(keymap);
        switch (result)
        {
            case GitHubOpenResult.Loaded loaded:
                DiagnosticLog.Info("MainVM",
                    $"Opened '{keymap}' from GitHub ({(loaded.FromCacheOffline ? "offline cache" : "fresh")})");
                LoadLayoutFromPath(loaded.LocalPath, keymap.ToString());
                if (loaded.FromCacheOffline)
                    Toast.Show(Loc.Instance.Format("Toast_GitHubOfflineFormat", keymap.FileName));
                break;

            case GitHubOpenResult.Failed failed:
                DiagnosticLog.Warn("MainVM", $"Opening '{keymap}' from GitHub failed ({failed.Kind}): {failed.Message}");
                StatusMessage = statusBefore;
                switch (failed.Kind)
                {
                    case GitHubErrorKind.Unauthorized:
                        Toast.Show(Loc.Instance.Format("Toast_GitHubSignInNeededFormat", keymap.FileName));
                        _ = OpenFromGitHubAsync();
                        break;
                    case GitHubErrorKind.NotFound:
                        Toast.Show(Loc.Instance.Format("Toast_GitHubNotFoundFormat", keymap.FileName));
                        break;
                    case GitHubErrorKind.Network:
                        Toast.Show(Loc.Instance.Format("Toast_GitHubNetworkFormat", keymap.FileName));
                        break;
                    default:
                        Toast.Show(Loc.Instance.Format("Toast_GitHubFailedFormat", keymap.FileName, failed.Message));
                        break;
                }
                break;
        }
    }

    /// <summary>The last background GitHub check started at startup or Refresh; tests await it.</summary>
    internal Task? PendingGitHubCheck { get; private set; }

    /// <summary>
    /// After a profile's stored GitHub entry was restored (startup, board
    /// switch, Refresh): ask GitHub in the background whether the branch
    /// moved, and if a newer copy came down, reload it and say so. When the
    /// cache was cold (nothing loaded yet) the fetched copy simply loads.
    /// Silent when signed out or offline — a startup shouldn't pop up errors
    /// or the sign-in window. A no-op for local entries.
    /// </summary>
    private async Task CheckGitHubForUpdateAsync(string entry)
    {
        try
        {
            var update = await Library.CheckGitHubAsync(entry, _profile.Id, () => (_profile.Id, _loadedEntry));
            if (update is null) return;

            // An open edit holds the bytes the cache copy was parsed from;
            // reloading underneath it is what the explicit load lock refuses —
            // and no "changed on GitHub" toast may claim otherwise.
            if (IsEditing)
            {
                DiagnosticLog.Info("MainVM", "CheckGitHubForUpdateAsync ignored while editing");
                return;
            }

            LoadLayoutFromPath(update.LocalPath, entry, keepBoard: true);
            if (update.ReplacesLoaded && GitHubKeymapRef.TryParse(entry, out var keymap))
                Toast.Show(Loc.Instance.Format("Toast_GitHubUpdatedFormat", keymap.FileName));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Info("MainVM", $"Background GitHub check for '{entry}' skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a snapshot of runtime state (active settings, loaded layout,
    /// last load error) for inclusion in
    /// <see cref="DiagnosticLog.CollectDiagnosticReport"/>.
    /// </summary>
    private async Task CopyDiagnosticsAsync()
    {
        try
        {
            var report = DiagnosticLog.CollectDiagnosticReport(BuildDiagnosticsSnapshot());
            if (await _shell.CopyTextAsync(report))
                StatusMessage = Loc.Instance["Status_DiagnosticsCopied"];
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not copy diagnostics: {ex.Message}";
        }
    }

    public string BuildDiagnosticsSnapshot()
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Active Settings ---");
        try
        {
            var settings = _settingsService.Load();
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            sb.AppendLine(json);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(could not serialize settings: {ex.Message})");
        }
        sb.AppendLine();

        sb.AppendLine("--- Active Keyboard ---");
        sb.AppendLine($"Profile: {_profile.DisplayName} ({_profile.Id}), {_profile.KeyCount} keys");
        sb.AppendLine($"Loaded layout: {_keymap.LoadedPath ?? "(none)"}");
        sb.AppendLine($"Last load error: {_keymap.LastLoadError ?? "(none)"}");

        return sb.ToString();
    }


    /// <summary>Opens the GitHub keymap picker and loads the chosen keymap.</summary>
    [RelayCommand]
    private async Task OpenFromGitHubAsync()
    {
        var result = await _shell.PickGitHubKeymapAsync(Devices.AttachedHidNames);
        if (result is not null)
            LoadLayoutFromPath(result.LocalPath, result.Keymap.ToString());
    }
}
