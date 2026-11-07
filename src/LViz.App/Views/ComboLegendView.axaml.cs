using Avalonia.Controls;
using Avalonia.Input;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// The combo legend tile strip. Its DataContext is the
/// <see cref="ComboLegendViewModel"/>; the tile handlers below are the
/// pointer side of the tile/key hover highlight and the label/combo editors.
/// </summary>
public partial class ComboLegendView : UserControl
{
    public ComboLegendView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Pointer enters a legend tile → highlight just that combo's number.
    /// Symmetric with the board-side handler in <see cref="BoardView"/>.
    /// </summary>
    private void OnComboTilePointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not ComboViewModel combo) return;
        if (DataContext is not ComboLegendViewModel vm) return;
        vm.SetHighlighted(new[] { combo.Number });
    }

    private void OnComboTilePointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is not ComboLegendViewModel vm) return;
        vm.SetHighlighted(Array.Empty<int>());
    }

    /// <summary>
    /// Right-click on a combo legend tile opens the combo-label editor. Editing
    /// is intentionally scoped to the legend only — on-key combo pills are
    /// already crowded with the number badge.
    /// </summary>
    private void OnComboTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not ComboViewModel combo) return;
        if (DataContext is not ComboLegendViewModel vm) return;
        if (!e.GetCurrentPoint(c).Properties.IsRightButtonPressed) return;
        vm.RequestEdit(combo.KeyPositionsKey);
        e.Handled = true;
    }

    /// <summary>
    /// Double-click on a combo legend tile opens the combo dialog on that tile
    /// while a keymap edit is open; outside edit mode it does nothing. A single
    /// click never opens an editor, and right-click stays the label editor.
    /// </summary>
    private void OnComboTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not ComboViewModel combo) return;
        if (DataContext is not ComboLegendViewModel { Edit: { } edit }) return;

        edit.EditComboCommand.Execute(combo.Number);
        e.Handled = true;
    }
}
