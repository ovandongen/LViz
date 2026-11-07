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

/// <summary>Board selection: switching boards, user-layout profiles and the layout wizard/editor.</summary>
public partial class MainWindowViewModel
{
    // --- Internals ---

    /// <summary>
    /// A board the user picked (toolbar flyout, Settings picker): the picked
    /// board always wins. A keymap opened for another board is unloaded even
    /// when its key count happens to fit the picked one — the board then
    /// shows the keymap booked for it, or nothing.
    /// </summary>
    private void SelectKeyboard(IKeyboardProfile? profile) => SwitchBoard(profile, keepFittingKeymap: false);

    /// <summary>
    /// Switches the shown board to <paramref name="profile"/>: rebuilds the
    /// keys, re-scopes the layer push, HID and the capability bus, persists
    /// the id. The open keymap is kept only when
    /// <paramref name="keepFittingKeymap"/> (the wizard path, which books the
    /// open keymap for the new board right after) and it fits the new board's
    /// key count; otherwise it is unloaded. A board left without a keymap
    /// then loads the entry booked for it.
    /// </summary>
    private void SwitchBoard(IKeyboardProfile? profile, bool keepFittingKeymap)
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "SelectKeyboard ignored while editing");
            return;
        }

        if (profile is null) return;
        if (string.Equals(profile.Id, _profile.Id, StringComparison.OrdinalIgnoreCase)) return;

        UseProfile(profile);

        var km = _keymap.Current;
        var layoutFits = km is not null
            && km.LayerCount > 0
            && km.Layers[0].Bindings.Count == profile.KeyCount;
        // A picked board never wears another board's keymap just because the
        // key counts match; only the wizard path may keep a fitting one.
        var keepKeymap = keepFittingKeymap && layoutFits;

        if (km is not null && !keepKeymap)
        {
            ClearLoadedKeymap();
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitchedUnloaded", profile.DisplayName);
        }
        else if (km is not null)
        {
            // Same keymap, different board: tab colors and hidden buttons are
            // per profile, so the tabs must be rebuilt from the new profile.
            LayerTabs.Rebuild(_keymap.Current);
            ApplyActiveLayer(ActiveLayerIndex);
            HasUnmatchedKeymap = false;
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitched", profile.DisplayName);
        }
        else
        {
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitchedUnloaded", profile.DisplayName);
        }

        PersistSetting(s => s with { Keyboard = profile.Id });
        // Recents are per-profile; the surfaced list changed with the profile.
        Recents.Refresh();
        DiagnosticLog.Info("MainVM", $"Keyboard profile switched to {profile.Id}");

        // Re-scope HID discovery (and the bus exclusion) to the new profile.
        Devices.SwitchProfile(profile);

        // Auto-load whichever entry the user last associated with this
        // keyboard (local path or GitHub reference — resolved via cache).
        // Only a keymap kept through the wizard path skips this.
        if (!HasLayoutLoaded)
        {
            if (Library.TryGetBooked(profile.Id, out var storedPath, out var storedEntry))
                LoadLayoutFromPath(storedPath, storedEntry, keepBoard: true);
            PendingGitHubCheck = CheckGitHubForUpdateAsync(storedEntry);
        }
    }

    // --- Layout wizard ("My own board…") ---

    /// <summary>
    /// Everything the wizard window can't discover itself, read from live VM
    /// state: the open keymap, the user-layout ids already taken (without the
    /// <c>user:</c> prefix), the connected devices no profile claims, the
    /// folder user layouts live in, and the cache folder of the open GitHub
    /// repo + branch (null when the open keymap is local).
    /// </summary>
    public LayoutWizardContext GetLayoutWizardContext()
    {
        // One snapshot: All allocates a fresh array per read.
        var profiles = KeyboardProfileRegistry.All;
        var existingIds = profiles
            .OfType<UserLayoutProfile>()
            .Select(p => p.Source.Id)
            .ToList();
        var deviceNames = Devices.BusDeviceNames
            .Distinct(StringComparer.Ordinal)
            .Where(name => !profiles.Any(p => p.NameMatches(name)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        // A GitHub entry has no local folder the user could browse to; the
        // wizard starts on the repo's cache instead.
        string? gitHubFolder =
            _gitHub is { } gitHub && GitHubKeymapRef.TryParse(_loadedEntry, out var keymap)
                ? gitHub.CacheFolderFor(keymap.Owner, keymap.Repo, keymap.Branch)
                : null;
        return new LayoutWizardContext(
            _keymap.Current, existingIds, deviceNames, UserLayoutStore.DefaultDirectory, gitHubFolder);
    }

    /// <summary>
    /// Everything the "Edit layout…" window can't discover itself: the saved
    /// layout behind the active user profile, the key captions from the open
    /// keymap, and the folder user layouts live in. Null unless a user
    /// layout is the active profile (bundled boards can't be edited).
    /// </summary>
    public LayoutEditContext? GetLayoutEditContext() =>
        _profile is UserLayoutProfile p
            ? new(p.Source, EditorLabels.For(_keymap.Current, p.KeyCount, p.Id), UserLayoutStore.DefaultDirectory)
            : null;

    /// <summary>
    /// Replaces the registry entry with the same id with a fresh profile
    /// built from <paramref name="layout"/> (appending it when the id is
    /// new) and refreshes the picker list.
    /// </summary>
    private UserLayoutProfile ReplaceUserProfile(UserLayoutFile layout)
    {
        var profile = new UserLayoutProfile(layout);
        KeyboardProfileRegistry.SetUserProfiles(
            KeyboardProfileRegistry.All
                .OfType<UserLayoutProfile>()
                .Where(p => !string.Equals(p.Source.Id, layout.Id, StringComparison.Ordinal))
                .Append((IKeyboardProfile)profile));
        OnPropertyChanged(nameof(AvailableKeyboards));
        return profile;
    }

    /// <summary>
    /// Shows a profile the picker could switch to — or one that just replaced
    /// the active profile under the same id. That second case needs an
    /// in-place refresh: <see cref="SelectKeyboard"/> returns early on an
    /// identical id, which would leave the old geometry on screen. No status
    /// message, persistence, or HID/bus re-scope — the device the picker
    /// points at doesn't change.
    /// </summary>
    /// <summary>Make <paramref name="profile"/> the board on screen and in the
    /// push engines (auto-switch rules, mouse layer). Every board change goes
    /// through here; a change to another board also calls
    /// <see cref="DeviceStack.SwitchProfile"/> to re-scope HID and the bus.</summary>
    private void UseProfile(IKeyboardProfile profile)
    {
        _profile = profile;
        SelectedKeyboard = profile;
        Board.SetProfile(profile);
        Devices.Push.SetActiveProfile(profile);
    }

    private void ShowProfile(IKeyboardProfile profile)
    {
        if (!string.Equals(profile.Id, _profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            // The wizard/editor path built the new board around the open
            // keymap, so a fitting keymap rides along for the re-booking.
            SwitchBoard(profile, keepFittingKeymap: true);
            return;
        }

        UseProfile(profile);

        var km = _keymap.Current;
        if (km is not null && km.LayerCount > 0 && km.Layers[0].Bindings.Count == profile.KeyCount)
        {
            // Same keymap, edited board: repaint so the rebuilt keys carry
            // their labels again, exactly like the switch path does.
            LayerTabs.Rebuild(_keymap.Current);
            ApplyActiveLayer(ActiveLayerIndex);
        }
    }

    /// <summary>
    /// Registers a saved wizard layout as a user profile (replacing any
    /// existing layout with the same id), selects it through the normal
    /// picker path, and loads the keymap the wizard picked when it found one.
    /// </summary>
    public void ApplyLayoutWizardResult(LayoutWizardResult result)
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "ApplyLayoutWizardResult ignored while editing");
            return;
        }

        var previousProfileId = _profile.Id;
        var profile = ReplaceUserProfile(result.Layout);
        ShowProfile(profile);

        if (result.KeymapPathToLoad is not null)
            LoadLayoutFromPath(result.KeymapPathToLoad);
        else if (_loadedEntry is string openEntry)
            // The wizard built this board around the keymap already open (it
            // fits, so the switch kept it loaded): remember it for the new
            // board, and drop it from the board it was opened on unmatched.
            {
                Library.Book(_profile.Id, openEntry, takeFromProfileId: previousProfileId);
                Recents.Refresh();
            }
        HasUnmatchedKeymap = false;

        DiagnosticLog.Info("MainVM", $"Added user layout {profile.Id} ({profile.KeyCount} keys)");
    }

    /// <summary>
    /// Applies the file the "Edit layout…" window saved: swaps the registry
    /// entry for the same id and refreshes the board in place, repainting the
    /// open keymap when it still fits the edited geometry.
    /// </summary>
    public void ApplyEditedLayout(UserLayoutFile layout)
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "ApplyEditedLayout ignored while editing");
            return;
        }

        ShowProfile(ReplaceUserProfile(layout));

        DiagnosticLog.Info("MainVM", $"Edited user layout user:{layout.Id} ({layout.Keys.Count} keys)");
    }

    /// <summary>Runs the layout wizard and applies the layout it saved.</summary>
    [RelayCommand]
    private async Task AddOwnBoardAsync()
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "AddOwnBoard ignored while editing");
            return;
        }
        var result = await _shell.RunLayoutWizardAsync(GetLayoutWizardContext());
        if (result is not null)
            ApplyLayoutWizardResult(result);
    }

    /// <summary>Opens the "Edit layout…" window and applies the file it saved.</summary>
    [RelayCommand]
    private async Task EditLayoutAsync()
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "EditLayout ignored while editing");
            return;
        }
        var ctx = GetLayoutEditContext();
        if (ctx is null) return;
        var result = await _shell.EditLayoutAsync(ctx);
        if (result is not null)
            ApplyEditedLayout(result);
    }
}
