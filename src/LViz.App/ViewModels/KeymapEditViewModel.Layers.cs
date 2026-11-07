using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.Core.Diagnostics;
using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;

namespace LViz.App.ViewModels;

// The layer shown while editing, and the layer commands.
public sealed partial class KeymapEditViewModel
{
    private int _shownLayerIndex;

    /// <summary>The session's id of the shown layer, so the board follows the
    /// layer — not its position — through undo, redo and moves; null with no
    /// layers.</summary>
    private int? _shownLayerId;

    /// <summary>Which layer the board shows while editing. The board follows the
    /// shown <em>layer</em>: after an undo or redo of a move it shows that layer
    /// where it went; when the layer is gone (an undone add) the position is
    /// kept, or the last layer when that position no longer exists.</summary>
    public int ShownLayerIndex => _shownLayerIndex;

    /// <summary>
    /// Raised when the shown layer was changed on purpose (<see cref="ShowLayer"/>
    /// — a tab click, an added or moved layer): the main window repaints the
    /// board. A shown layer that merely followed a session change is reported
    /// through <see cref="Changed"/> instead. The main window never writes
    /// back from this event, so the two cannot loop.
    /// </summary>
    public event EventHandler? ShownLayerChanged;

    /// <summary>
    /// Show the layer at <paramref name="index"/>. An index outside the layer
    /// list, or the layer already shown, does nothing. A selection is a key on
    /// the layer it was made on, so showing another layer drops it.
    /// </summary>
    public void ShowLayer(int index)
    {
        var layers = Session.Current.Layers;
        if (index < 0 || index >= layers.Count) return;

        var id = Session.LayerIdAt(index);
        if (index == _shownLayerIndex && id == _shownLayerId) return;

        MoveShownLayerTo(index);
        NotifyCommandState();
        ShownLayerChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>After a session change: keep showing the same layer id; when it
    /// is gone keep the position, clamped into the list.</summary>
    private void FollowShownLayer()
    {
        var count = Session.Current.Layers.Count;
        var index = _shownLayerIndex;
        if (_shownLayerId is int id && Session.IndexOfLayerId(id) is int followed)
            index = followed;
        if (count > 0 && (index < 0 || index >= count)) index = count - 1;
        MoveShownLayerTo(index);
    }

    private void MoveShownLayerTo(int index)
    {
        var count = Session.Current.Layers.Count;
        var previousId = _shownLayerId;
        _shownLayerIndex = index;
        _shownLayerId = index >= 0 && index < count ? Session.LayerIdAt(index) : null;

        // A selection is a key on the layer it was made on. Undo/redo and the
        // repaint after a save keep the layer id, so they keep it.
        if (previousId is not null && previousId != _shownLayerId)
            ClearSelection();
    }

    /// <summary>Layer and combo actions need an open edit and no save in flight.</summary>
    private bool CanEditLayers => IsOpen && !IsSaving;

    /// <summary>
    /// Append a new layer at the end of the layer list — every key
    /// <c>&amp;trans</c>, named "Layer N" for its index (made unique against the
    /// names in use) — as a single undo step.
    /// </summary>
    /// <returns>
    /// The index the new layer sits at (which is then the shown layer), or
    /// <see langword="null"/> when the keymap has no layers at all, so the key
    /// count of a new layer is unknown, or while saving / after the edit closed.
    /// </returns>
    public int? AddLayer()
    {
        if (!CanEditLayers) return null;
        var layers = Session.Current.Layers;
        if (layers.Count == 0) return null;

        var index = layers.Count;
        var edit = new LayerStructureEdit.Add(
            index,
            LayerNames.Unique($"Layer {index}", layers.Select(l => l.Name)),
            [.. Enumerable.Repeat(KeyBinding.Transparent, layers[0].Bindings.Count)]);

        Session.ApplyStructure(edit, LayerReferenceRemapper.Apply(Session.Current, edit));
        ShowLayer(index);
        return index;
    }

    /// <summary>
    /// Insert a copy of the layer at <paramref name="layerIndex"/> right after
    /// it — same bindings, named "&lt;name&gt; copy" (made unique against the
    /// names in use) — as a single undo step. Layer references in the copy point
    /// where the original's pointed, renumbered for the insertion.
    /// </summary>
    /// <returns>
    /// The index the copy sits at (<paramref name="layerIndex"/> + 1, which is
    /// then the shown layer), or
    /// <see langword="null"/> when <paramref name="layerIndex"/> falls outside
    /// <see cref="KeymapEditSession.Current"/>.
    /// </returns>
    public int? DuplicateLayer(int layerIndex)
    {
        if (!CanEditLayers) return null;
        var layers = Session.Current.Layers;
        if (layerIndex < 0 || layerIndex >= layers.Count) return null;

        var source = layers[layerIndex];
        var index = layerIndex + 1;
        var edit = new LayerStructureEdit.Add(
            index,
            LayerNames.Unique($"{source.Name} copy", layers.Select(l => l.Name)),
            [.. source.Bindings]);

        Session.ApplyStructure(edit, LayerReferenceRemapper.Apply(Session.Current, edit));
        ShowLayer(index);
        return index;
    }

    /// <summary>
    /// Move the layer at <paramref name="layerIndex"/> to
    /// <paramref name="toIndex"/> (remove, then insert there in the shortened
    /// list) as a single undo step, renumbering every layer reference.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> — without touching anything — when either index
    /// falls outside <see cref="KeymapEditSession.Current"/>, they are equal, or
    /// a save is running. The moved layer's new position becomes the shown layer.
    /// </returns>
    public bool MoveLayer(int layerIndex, int toIndex)
    {
        if (!CanEditLayers) return false;
        var count = Session.Current.Layers.Count;
        if (layerIndex < 0 || layerIndex >= count) return false;
        if (toIndex < 0 || toIndex >= count) return false;
        if (layerIndex == toIndex) return false;

        var edit = new LayerStructureEdit.Move(layerIndex, toIndex);
        if (!Session.ApplyStructure(edit, LayerReferenceRemapper.Apply(Session.Current, edit))) return false;

        ShowLayer(toIndex);
        return true;
    }

    /// <summary>
    /// Give the layer at <paramref name="layerIndex"/> a new
    /// <c>display-name</c> (<paramref name="name"/> trimmed) as a single undo
    /// step.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> — without touching anything, never throwing —
    /// when <paramref name="layerIndex"/> falls outside
    /// <see cref="KeymapEditSession.Current"/>, the trimmed name is empty or
    /// holds a control character (a quoted devicetree string cannot), or the
    /// layer already has that name.
    /// </returns>
    public bool RenameLayer(int layerIndex, string name)
    {
        var layers = Session.Current.Layers;
        if (layerIndex < 0 || layerIndex >= layers.Count) return false;

        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsControl)) return false;
        if (string.Equals(trimmed, layers[layerIndex].Name, StringComparison.Ordinal)) return false;

        return Session.RenameLayer(layerIndex, trimmed);
    }

    /// <summary>
    /// What deleting the layer at <paramref name="layerIndex"/> would do,
    /// without changing anything: the blockers the <c>.keymap</c> text still
    /// holds and the ones only the session's combo edits hold, and how many
    /// keys would lose their layer target.
    /// </summary>
    /// <remarks>
    /// The text blockers come from the baseline text with the pending
    /// structural edits replayed onto it, so the writer's layer indices line up
    /// with <see cref="KeymapEditSession.Current"/>; the cleared-binding count
    /// comes from <see cref="Current"/> itself, because key edits are never in
    /// that text. Combos edited in this session are not in that text either, so
    /// every combo the current model aims at the layer adds a
    /// <see cref="LayerDeleteBlockerKind.Combo"/> blocker (after the text
    /// blockers, <c>Line</c> 0) unless a text blocker already names it
    /// (Ordinal). A combo fixed in the session but not yet saved therefore
    /// still blocks through the text.
    /// </remarks>
    /// <remarks>
    /// <see cref="ZmkKeymapParseException"/> (the pending edits would not write)
    /// propagates — the caller shows it.
    /// </remarks>
    /// <returns>
    /// The check, or <see langword="null"/> when <paramref name="layerIndex"/>
    /// falls outside <see cref="KeymapEditSession.Current"/>.
    /// </returns>
    public LayerDeleteCheck? CheckDeleteLayer(int layerIndex)
    {
        var layers = Session.Current.Layers;
        if (layerIndex < 0 || layerIndex >= layers.Count) return null;

        var text = KeymapLayerWriter.ApplyAll(Source.BaselineText, Session.PendingStructureEdits);
        var check = KeymapLayerWriter.CheckDelete(text, layerIndex);

        var named = new HashSet<string>(
            check.Blockers.Select(b => b.Name), StringComparer.Ordinal);
        var sessionCombos = new List<LayerDeleteBlocker>();
        foreach (var name in LayerReferenceRemapper.CombosUsingLayer(Session.Current, layerIndex))
            if (named.Add(name))
                sessionCombos.Add(new LayerDeleteBlocker(LayerDeleteBlockerKind.Combo, name, 0));

        return check with
        {
            Blockers = sessionCombos.Count == 0
                ? check.Blockers
                : [.. check.Blockers, .. sessionCombos],
            ClearedBindings = LayerReferenceRemapper.CountClearedBindings(Session.Current, layerIndex),
        };
    }

    /// <summary>
    /// Delete the layer at <paramref name="layerIndex"/> as a single undo step,
    /// clearing the layer bindings that targeted it — only when
    /// <see cref="CheckDeleteLayer"/> finds no blocker (combos, conditional
    /// layers, define usages refuse the delete).
    /// </summary>
    /// <remarks>
    /// <see cref="ZmkKeymapParseException"/> propagates through the check.
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> — without touching anything — when the index
    /// falls outside <see cref="KeymapEditSession.Current"/> or the delete has
    /// blockers.
    /// </returns>
    public bool DeleteLayer(int layerIndex)
    {
        var check = CheckDeleteLayer(layerIndex);
        if (check is null || !check.CanDelete) return false;

        var edit = new LayerStructureEdit.Delete(layerIndex);
        Session.ApplyStructure(edit, LayerReferenceRemapper.Apply(Session.Current, edit));
        return true;
    }

    /// <summary>
    /// Rename the layer at <paramref name="layerIndex"/> through the rename
    /// dialog. Does nothing when closed or saving, when the index falls outside
    /// the edited layers, or while another dialog of this edit is open. A
    /// cancelled dialog (null) changes nothing; so does an answer once the edit
    /// was closed underneath the await.
    /// </summary>
    public async Task RenameLayerAsync(int layerIndex)
    {
        if (!CanEditLayers) return;
        var layers = Session.Current.Layers;
        if (layerIndex < 0 || layerIndex >= layers.Count) return;

        await AskAsync(
            () => _dialogs.RenameLayerAsync(new RenameLayerRequest(layerIndex, layers[layerIndex].Name)),
            answer => RenameLayer(layerIndex, answer));
    }

    /// <summary>
    /// Delete the layer at <paramref name="layerIndex"/>, no confirmation:
    /// when the check allows it, delete it, show the layer before it (layer 0
    /// when layer 0 went), and report it in the status bar — with the cleared
    /// key count when keys pointed at the layer. When it is refused, show the
    /// blockers and change nothing. Does nothing when closed or saving or when
    /// the index falls outside the edited layers. A writer refusal reports an
    /// error and changes nothing.
    /// </summary>
    public async Task DeleteLayerAsync(int layerIndex)
    {
        if (!CanEditLayers) return;
        var layers = Session.Current.Layers;
        if (layerIndex < 0 || layerIndex >= layers.Count) return;
        var name = layers[layerIndex].Name;

        try
        {
            if (CheckDeleteLayer(layerIndex) is not { } check) return;

            if (!check.CanDelete)
            {
                await ExclusiveAsync(() => _dialogs.ShowLayerDeleteBlockedAsync(
                    new LayerDeleteBlockedRequest(name, check.Blockers)
                    {
                        ComboEditsPending = HasPendingComboEdits,
                    }));
                return;
            }

            if (DeleteLayer(layerIndex))
            {
                ShowLayer(Math.Max(layerIndex - 1, 0));
                Report(KeymapEditReportKind.Status, check.ClearedBindings > 0
                    ? Loc.Instance.Format("Edit_LayerDeletedFormat", name, check.ClearedBindings)
                    : Loc.Instance.Format("Edit_LayerDeletedNoRefsFormat", name));
            }
        }
        catch (ZmkKeymapParseException ex)
        {
            // The text refused the change; the edit and its undo history are
            // untouched, so the user stays put.
            Report(KeymapEditReportKind.Error, Loc.Instance.Format("Edit_LayerActionFailedFormat", ex.Message));
            DiagnosticLog.Warn("Edit", $"The layer action on '{name}' failed: {ex}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditLayers))]
    private void NewLayer() => AddLayer();

    [RelayCommand(CanExecute = nameof(CanEditLayers))]
    private void DuplicateShownLayer() => DuplicateLayer(ShownLayerIndex);

    private bool CanMoveShownLayerLeft => CanEditLayers && ShownLayerIndex > 0;

    [RelayCommand(CanExecute = nameof(CanMoveShownLayerLeft))]
    private void MoveShownLayerLeft() => MoveLayer(ShownLayerIndex, ShownLayerIndex - 1);

    private bool CanMoveShownLayerRight =>
        CanEditLayers && ShownLayerIndex < Session.Current.Layers.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveShownLayerRight))]
    private void MoveShownLayerRight() => MoveLayer(ShownLayerIndex, ShownLayerIndex + 1);

    [RelayCommand(CanExecute = nameof(CanEditLayers))]
    private Task RenameShownLayerAsync() => RenameLayerAsync(ShownLayerIndex);

    [RelayCommand(CanExecute = nameof(CanEditLayers))]
    private Task DeleteShownLayerAsync() => DeleteLayerAsync(ShownLayerIndex);

    /// <summary>Re-query every command whose enable state hangs off the open /
    /// saving state or the shown layer. Every site that can move that state
    /// calls this so one of them can't be forgotten.</summary>
    private void NotifyCommandState()
    {
        NewLayerCommand.NotifyCanExecuteChanged();
        DuplicateShownLayerCommand.NotifyCanExecuteChanged();
        MoveShownLayerLeftCommand.NotifyCanExecuteChanged();
        MoveShownLayerRightCommand.NotifyCanExecuteChanged();
        RenameShownLayerCommand.NotifyCanExecuteChanged();
        DeleteShownLayerCommand.NotifyCanExecuteChanged();
        NewComboCommand.NotifyCanExecuteChanged();
        SaveEditCommand.NotifyCanExecuteChanged();
    }
}
