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

/// <summary>Keymap edit lifecycle: start, leave, discard, save follow-up and the layer-settings notices.</summary>
public partial class MainWindowViewModel
{
    // --- Keymap edit mode (starting, leaving and the follow-up to a save; the
    // edit's own actions live on KeymapEditViewModel) ---

    /// <summary>
    /// The open keymap edit, or null. Bind to it directly for everything the
    /// edit offers (<c>Edit.SaveEditCommand</c>, <c>Edit.NewLayerCommand</c>,
    /// ...). Setting it (from <see cref="StartEdit"/> and <see cref="LeaveEdit"/>)
    /// drives <see cref="IsEditing"/>, <see cref="CanStartEdit"/> and the Start
    /// command's enable state.
    /// </summary>
    [ObservableProperty]
    private KeymapEditViewModel? _edit;

    /// <summary>True while a keymap edit is open — locks keymap/board switching
    /// and routes key taps into the editor.</summary>
    public bool IsEditing => Edit is not null;

    /// <summary>Only a loaded <c>.keymap</c> file can be edited (the Moergo
    /// <c>.json</c> export stays import-only), and only one edit at a time.</summary>
    public bool CanStartEdit =>
        !IsEditing
        && HasLayoutLoaded
        && LoadedLayoutPath is string path
        && path.EndsWith(".keymap", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tooltip for the edit button; over a <c>.json</c> keymap it says
    /// that format cannot be edited.</summary>
    public string EditButtonTooltip =>
        LoadedLayoutPath is string path && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? Loc.Instance["Tooltip_EditKeymapJson"]
            : Loc.Instance["Tooltip_EditKeymap"];

    partial void OnEditChanged(KeymapEditViewModel? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(CanStartEdit));
        Board.ComboLegend.SetEditing(value);
        SyncKeyCallbacks();
        LayerTabs.SetEditing(value);
        StartEditCommand.NotifyCanExecuteChanged();
        DiscardEditCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Open an edit on the loaded keymap: re-read the file, parse it, and show
    /// the edit view — every layer tab visible (hidden ones included, in memory
    /// only). A read or parse failure leaves the view unedited and toasts why.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartEdit))]
    private void StartEdit()
    {
        KeymapEditViewModel edit;
        try
        {
            edit = KeymapEditViewModel.Start(
                LoadedLayoutPath!, _loadedEntry ?? LoadedLayoutPath!, _gitHub, _keymapBackups,
                _editDialogs, _profile, ActiveLayerIndex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ZmkKeymapParseException)
        {
            Toast.Show(Loc.Instance.Format(
                "Edit_StartFailedFormat", Path.GetFileName(LoadedLayoutPath!), ex.Message));
            DiagnosticLog.Warn("MainVM", $"Starting the keymap edit failed: {ex}");
            return;
        }

        Edit = edit;
        edit.Changed += OnEditChanged;
        edit.ShownLayerChanged += OnEditShownLayerChanged;
        edit.PropertyChanged += OnEditPropertyChanged;
        edit.Reported += OnEditReported;
        edit.Completed += OnEditCompletedAsync;

        // The tabs rebuild off the edit: every layer shows (hidden ones
        // included, purely in memory) and each tab carries the per-layer
        // actions; LeaveEdit rebuilds them from the loaded keymap and the
        // persisted setting.
        LayerTabs.Rebuild(_keymap.Current);

        ApplyActiveLayer(edit.ShownLayerIndex);

        // The legend follows the edit too: it shows the edited combos, and
        // every tile becomes editable with its pending-change mark.
        Board.ComboLegend.Rebuild(_keymap.Current);
    }

    /// <summary>One edit change: repaint the board off the edited keymap. The
    /// edit has already moved its shown layer along (a layer is followed by id
    /// through undo and redo). Structural changes (add/delete/move/rename) also
    /// rebuild the tab strip — a plain key edit leaves the tabs alone, since
    /// rebuilding replaces the tab buttons.</summary>
    private void OnEditChanged(object? sender, EventArgs e)
    {
        if (Edit is not { } edit) return;

        if (!LayerTabs.MatchesEdit()) LayerTabs.Rebuild(_keymap.Current);
        ApplyActiveLayer(edit.ShownLayerIndex);
        // A tab color can move to another position while the shown index (and
        // with it the NotifyPropertyChangedFor above) stays put.
        OnPropertyChanged(nameof(ActiveLayerTintColor));

        // The legend re-reads the edited combos — a key edit can retake a
        // tile's labels, an undo can add or drop tiles entirely.
        Board.ComboLegend.Rebuild(_keymap.Current);
    }

    /// <summary>The edit showed another layer on purpose (an added or moved
    /// layer): repaint it. One direction only — painting never writes back to
    /// the edit.</summary>
    private void OnEditShownLayerChanged(object? sender, EventArgs e)
    {
        if (Edit is { } edit) ApplyActiveLayer(edit.ShownLayerIndex);
    }

    private void OnEditPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(KeymapEditViewModel.SelectedKeyIndex):
                RefreshEditSelectionFlags();
                break;
            case nameof(KeymapEditViewModel.IsSaving):
                DiscardEditCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    private void OnEditReported(KeymapEditReport report)
    {
        if (report.Kind == KeymapEditReportKind.Error) Toast.Show(report.Message);
        else StatusMessage = report.Message;
    }

    /// <summary>
    /// What follows a finished edit action. A save that was written: carry the
    /// per-layer settings and combo labels along, close the edit, reload what
    /// was written, stay on the layer the editor was on and report it in the
    /// status bar. A conflict answered with "reload": drop the edit and show
    /// the target as it stands. Every reload happens <em>after</em>
    /// <see cref="LeaveEdit"/>: the edit-mode locks refuse
    /// <see cref="LoadLayoutFromPath"/> and <see cref="OpenGitHubKeymapAsync"/>
    /// while an edit is open.
    /// </summary>
    private async Task OnEditCompletedAsync(KeymapEditCompletion completion)
    {
        if (Edit is not { } edit) return;

        switch (completion)
        {
            case KeymapEditCompletion.Saved saved:
            {
                // A reload always lands on layer 0; a save must not move the
                // user off the layer they were editing — unless that layer's
                // tab is hidden, then the nearest shown one takes over.
                var layer = ActiveLayerIndex;

                // The layers just moved on disk for good — renumber this
                // keyboard's per-layer settings with them and collect what
                // the move could not carry over.
                var notices = edit.Session.PendingStructureEdits.Count > 0
                    ? MoveLayerSettings(edit.Session.BaselineLayerMap())
                    : Array.Empty<LayerSettingsNotice>();

                // Combo labels follow their combo the same way layer
                // settings follow their layers — before the edit closes.
                Board.ComboLegend.MoveLabelOverrides(saved.Plan.ComboChanges);

                LeaveEdit();
                LoadLayoutFromPath(saved.ReloadPath, edit.Source.Entry);
                ApplyActiveLayer(LayerTabs.NearestShownLayer(layer));
                StatusMessage = Loc.Instance.Format(
                    "Status_EditSavedFormat", Path.GetFileName(saved.ReloadPath));

                if (notices.Count > 0)
                    await _editDialogs.ShowLayerSettingsNoticesAsync(notices.Select(NoticeLine).ToList());
                break;
            }

            case KeymapEditCompletion.ReloadRequested:
                LeaveEdit();
                if (edit.Source.GitHub is { } gitHubRef)
                    await OpenGitHubKeymapAsync(gitHubRef);
                else
                    LoadLayoutFromPath(edit.Source.LocalPath, edit.Source.Entry);
                break;
        }
    }

    /// <summary>
    /// Close the open edit without saving: unsubscribe, rebuild the tabs from
    /// settings (hidden layers hide again), and repaint from the loaded keymap.
    /// The save path reuses this once the bytes are on disk, and always before
    /// reloading anything — the edit-mode locks refuse every load path.
    /// </summary>
    private void LeaveEdit()
    {
        if (Edit is { } edit)
        {
            edit.Changed -= OnEditChanged;
            edit.ShownLayerChanged -= OnEditShownLayerChanged;
            edit.PropertyChanged -= OnEditPropertyChanged;
            edit.Reported -= OnEditReported;
            edit.Completed -= OnEditCompletedAsync;
            edit.Close();
        }
        Edit = null;
        LayerTabs.Rebuild(_keymap.Current);
        ApplyActiveLayer(LayerTabs.NearestShownLayer(ActiveLayerIndex));

        // Back to the loaded keymap's combos, and the tiles lose their
        // edit-mode flags with it.
        Board.ComboLegend.Rebuild(_keymap.Current);
    }

    /// <summary>Drop the edit and its changes.</summary>
    [RelayCommand(CanExecute = nameof(CanDiscardEdit))]
    private void DiscardEdit()
    {
        if (IsEditing) LeaveEdit();
    }

    private bool CanDiscardEdit => Edit is { IsSaving: false };

    /// <summary>Left-click on a key while editing selects it for the BoardView.</summary>
    private void OnEditKeyTapped(int keyIndex) => Edit!.SelectKey(keyIndex);

    /// <summary>Double-click on a key while editing selects it and opens the
    /// key editor on it — fire and forget, the editor owns its own errors.</summary>
    private void OnEditKeyDoubleTapped(int keyIndex)
    {
        OnEditKeyTapped(keyIndex);
        _ = Edit!.EditSelectedKeyAsync();
    }

    /// <summary>Mark the editor's current selection on every key.</summary>
    private void RefreshEditSelectionFlags()
    {
        if (Edit is not { } edit) return;
        for (var i = 0; i < Board.Keys.Count; i++)
            Board.Keys[i].IsEditSelected = edit.SelectedKeyIndex == i;
    }

    /// <summary>
    /// Carries this keyboard's persisted per-layer settings across a save that
    /// added, deleted or moved layers: <paramref name="map"/> is the just-closed
    /// edit's <see cref="KeymapEditSession.BaselineLayerMap"/>, applied to the
    /// settings as they stand at that moment. Every in-memory copy is refreshed
    /// from the rewritten settings so the UI reads the new numbers — palette and
    /// key labels wholesale, the auto-switch rules and the mouse layer through
    /// their engines (which persist their own slice). Returns what the remap
    /// dropped or cannot fix; see <see cref="LayerSettingsRemap"/>.
    /// </summary>
    private IReadOnlyList<LayerSettingsNotice> MoveLayerSettings(IReadOnlyList<int?> map)
    {
        LayerSettingsRemapResult? remapped = null;
        PersistSetting(s =>
        {
            remapped = LayerSettingsRemap.Apply(s, _profile.Id, map);
            return remapped.Settings;
        });
        if (remapped is null) return Array.Empty<LayerSettingsNotice>();

        var settings = remapped.Settings;
        LayerColorPalette.SetOverrides(settings.LayerColors);
        KeyLabelOverrides.SetOverrides(settings.KeyLabelOverrides);
        AutoSwitch.ApplyAppLayerRules(settings.AppLayerRules.TryGetValue(_profile.Id, out var rules)
            ? rules
            : Array.Empty<AppLayerRule>());
        if (settings.MouseLayer.TryGetValue(_profile.Id, out var mouse))
            MouseLayer?.ApplySettings(mouse);

        return remapped.Notices;
    }

    /// <summary>One remap notice as its translated report line.</summary>
    private static string NoticeLine(LayerSettingsNotice notice) => notice.Kind switch
    {
        LayerSettingsNoticeKind.AppRuleRemoved => Loc.Instance.Format(
            "Edit_NoticeAppRuleRemovedFormat", notice.Name, notice.OldLayerIndex),
        LayerSettingsNoticeKind.MouseLayerCleared => Loc.Instance.Format(
            "Edit_NoticeMouseLayerClearedFormat", notice.OldLayerIndex),
        // PipelineStepCheck — pipelines are global, nothing was rewritten.
        _ => Loc.Instance.Format(
            "Edit_NoticePipelineCheckFormat", notice.Name, notice.OldLayerIndex),
    };
}
