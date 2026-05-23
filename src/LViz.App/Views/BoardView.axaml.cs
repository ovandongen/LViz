using Avalonia.Controls;
using Avalonia.Input;
using LViz.App.ViewModels;

namespace LViz.App.Views;

public partial class BoardView : UserControl
{
    public BoardView()
    {
        InitializeComponent();
    }

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
