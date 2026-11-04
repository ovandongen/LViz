using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// Modal "review changes before saving" dialog. Its result is a
/// <see cref="KeymapReviewAnswer"/> carrying the (edited) commit message to
/// save the plan, or <c>null</c> to keep editing: Esc, Cancel and the window's
/// close button all answer <c>null</c>.
/// </summary>
public partial class KeymapReviewDialog : Window
{
    public KeymapReviewDialog()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) =>
        Close(DataContext is KeymapReviewDialogViewModel vm ? vm.Answer() : null);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
