using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
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
    /// board, via <see cref="MainWindowViewModel.SetHighlightedCombos"/>).
    /// Inert if the hosting surface isn't the main VM (e.g. the exit-key
    /// picker).
    /// </summary>
    private void OnComboBadgePointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not KeyComboBadgeViewModel badge) return;
        if (TryGetMainVm(c) is not MainWindowViewModel mainVm) return;
        mainVm.SetHighlightedCombos(new[] { badge.Number });
    }

    private void OnComboBadgePointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c) return;
        if (TryGetMainVm(c) is not MainWindowViewModel mainVm) return;
        mainVm.SetHighlightedCombos(EmptyComboNumbers);
    }

    private static MainWindowViewModel? TryGetMainVm(Control source) =>
        source.FindAncestorOfType<MainWindow>()?.DataContext as MainWindowViewModel;

    /// <summary>
    /// Routes a cap pointer event to the hosting <see cref="IBoardSurface"/>:
    /// left-clicks fire <c>OnKeyTapped</c> (selection in the exit-key picker;
    /// inert in the main window), right-clicks fire <c>OnKeyRightTapped</c>
    /// (per-key label-override editor in the main window; inert in the picker).
    /// </summary>
    private void OnKeyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not KeyViewModel keyVm) return;
        if (DataContext is not IBoardSurface surface) return;

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
            var handler = surface.OnKeyTapped;
            if (handler is null) return;
            handler(keyVm.Position.Index);
            e.Handled = true;
        }
    }
}
