using CommunityToolkit.Mvvm.Input;
using LViz.Core.Models;

namespace LViz.App.ViewModels;

// The edit's dialog-driven actions: the key editor and the combo dialog, and
// the one place that decides "one dialog at a time, and drop an answer that
// arrives after the edit was closed".
public sealed partial class KeymapEditViewModel
{
    /// <summary>True while a dialog of this edit is open; a second request is
    /// ignored until it closes.</summary>
    private bool _dialogOpen;

    /// <summary>
    /// Run <paramref name="body"/> as the only open dialog of this edit.
    /// Returns without running it while another dialog is open.
    /// </summary>
    private async Task ExclusiveAsync(Func<Task> body)
    {
        if (_dialogOpen) return;

        _dialogOpen = true;
        try
        {
            await body();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>
    /// Ask one dialog and apply what it answers: the only open dialog of this
    /// edit, a cancelled dialog (null) changes nothing, and neither does an
    /// answer that arrives after the main window closed this edit (replaced or
    /// left it) underneath the await.
    /// </summary>
    private Task AskAsync<T>(Func<Task<T?>> ask, Action<T> apply) where T : class =>
        ExclusiveAsync(async () =>
        {
            var answer = await ask();
            if (answer is not null && IsOpen) apply(answer);
        });

    /// <summary>
    /// Clear the selected key on the shown layer to <c>&amp;none</c>
    /// (<paramref name="toNone"/>) or <c>&amp;trans</c> — Delete / Shift+Delete.
    /// Does nothing when nothing is selected; the session's change repaints the
    /// board and the flags.
    /// </summary>
    public void ClearSelectedKey(bool toNone)
    {
        if (SelectedKeyIndex is not int selected) return;
        ClearKey(ShownLayerIndex, selected, toNone);
    }

    /// <summary>
    /// Open the key editor on the selected key of the shown layer and apply
    /// what it answers as one undo step. Does nothing when nothing is
    /// selected, when the selection sits outside the shown layer, or while
    /// another dialog is open. A cancelled editor (null) changes nothing; so
    /// does a result once the edit was closed underneath the await.
    /// </summary>
    public Task EditSelectedKeyAsync()
    {
        if (!IsOpen) return Task.CompletedTask;
        if (SelectedKeyIndex is not int key) return Task.CompletedTask;

        var layer = ShownLayerIndex;
        var layers = Session.Current.Layers;
        if (layer < 0 || layer >= layers.Count) return Task.CompletedTask;
        if (key < 0 || key >= layers[layer].Bindings.Count) return Task.CompletedTask;

        return AskAsync(
            () => _dialogs.EditKeyAsync(
                new KeyDialogRequest(Session.Current, Defines, layer, key) { LayerDefines = LayerDefines }),
            binding => SetKey(layer, key, binding));
    }

    /// <summary>
    /// Open the combo dialog for a new combo and append what it answers as one
    /// undo step. Does nothing while a save is in flight or another dialog is
    /// open.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditLayers))]
    private Task NewComboAsync() =>
        !CanEditLayers
            ? Task.CompletedTask
            : AskAsync<ZmkCombo>(
                () => _dialogs.EditComboAsync(ComboRequest(null)),
                combo => Session.AddCombo(combo));

    /// <summary>
    /// Open the combo dialog on tile <paramref name="number"/> (the 1-based
    /// tile number) and replace that combo with what it answers, as one undo
    /// step. An out-of-range number, a save in flight or an open dialog do
    /// nothing; so does a result once the edit was closed underneath the await
    /// — or once the tile it was opened on no longer exists.
    /// </summary>
    [RelayCommand]
    private Task EditComboAsync(int number)
    {
        var index = number - 1;
        if (!CanEditLayers) return Task.CompletedTask;
        if (index < 0 || index >= Session.Current.Combos.Count) return Task.CompletedTask;

        return AskAsync<ZmkCombo>(
            () => _dialogs.EditComboAsync(ComboRequest(index)),
            combo =>
            {
                if (index < Session.Current.Combos.Count) Session.SetCombo(index, combo);
            });
    }

    /// <summary>
    /// Remove the combo on tile <paramref name="number"/> (the 1-based tile
    /// number) as one undo step — no confirmation, like deleting a layer. An
    /// out-of-range number, a closed edit or a save in flight does nothing
    /// (the plan of a running save is already built, so the delete would be
    /// lost).
    /// </summary>
    [RelayCommand]
    private void DeleteCombo(int number)
    {
        var index = number - 1;
        if (!CanEditLayers) return;
        if (index < 0 || index >= Session.Current.Combos.Count) return;

        Session.DeleteCombo(index);
    }

    private ComboDialogRequest ComboRequest(int? comboIndex) =>
        new(_profile, Session.Current, Defines, LayerDefines, comboIndex, ShownLayerIndex);
}
