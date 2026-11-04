using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LViz.App.Views;

/// <summary>
/// Modal "pick one" dialog driven by <see cref="ChoiceDialogViewModel"/>. Its
/// result is the clicked option's id; <c>null</c> means the user declined —
/// Esc or the window's close button. Callers map <c>null</c> to the safe
/// choice (keep editing, don't quit).
/// </summary>
public partial class ChoiceDialog : Window
{
    public ChoiceDialog()
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

    private void OnOptionClick(object? sender, RoutedEventArgs e) =>
        Close((string?)((Button)sender!).Tag);
}
