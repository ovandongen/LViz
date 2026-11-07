using Avalonia.Controls;
using Avalonia.Input;
using LViz.App.ViewModels;

namespace LViz.App.Views;

public partial class BoardView : UserControl
{
    private static readonly int[] EmptyComboNumbers = System.Array.Empty<int>();

    public BoardView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Pointer enters a per-combo pill on a key → highlight that single
    /// combo's legend tile (and every participating-key pill across the
    /// board, via <see cref="ComboLegendViewModel.SetHighlighted"/>).
    /// Acts on the hosting surface's own legend; the picker's has no badges.
    /// </summary>
    private void OnComboBadgePointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { DataContext: KeyComboBadgeViewModel badge }) return;
        if (DataContext is not BoardSurfaceViewModel surface) return;
        surface.ComboLegend.SetHighlighted(new[] { badge.Number });
    }

    private void OnComboBadgePointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is not BoardSurfaceViewModel surface) return;
        surface.ComboLegend.SetHighlighted(EmptyComboNumbers);
    }

    /// <summary>
    /// Routes a cap pointer event to the hosting <see cref="BoardSurfaceViewModel"/>:
    /// left-clicks fire <c>OnKeyTapped</c> (selection in the exit-key picker;
    /// inert in the main window), the second press of a double-click fires
    /// <c>OnKeyDoubleTapped</c> (key dialog on the cap while editing), and
    /// right-clicks fire <c>OnKeyRightTapped</c> (per-key label-override editor
    /// in the main window; inert in the picker).
    /// </summary>
    private void OnKeyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not KeyViewModel keyVm) return;
        if (DataContext is not BoardSurfaceViewModel surface) return;

        var point = e.GetCurrentPoint(c);
        if (point.Properties.IsRightButtonPressed)
        {
            var rightHandler = surface.OnKeyRightTapped;
            if (rightHandler is null) return;
            rightHandler(keyVm.Position.Index);
            e.Handled = true;
            return;
        }
        if (point.Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount >= 2 && surface.OnKeyDoubleTapped is { } doubleHandler)
            {
                doubleHandler(keyVm.Position.Index);
                e.Handled = true;
                return;
            }

            var handler = surface.OnKeyTapped;
            if (handler is null) return;
            handler(keyVm.Position.Index);
            e.Handled = true;
        }
    }
}
