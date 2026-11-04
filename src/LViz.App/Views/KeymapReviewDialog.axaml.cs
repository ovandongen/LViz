using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LViz.App.Views;

/// <summary>
/// Modal "review changes before saving" dialog. Its result is the user's answer:
/// <c>true</c> = save the plan, <c>false</c> = keep editing. Esc and the window's
/// close button both answer <c>false</c> (a window closed without a result
/// reads back as <c>default(bool)</c>).
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
            Close(false);
            e.Handled = true;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
