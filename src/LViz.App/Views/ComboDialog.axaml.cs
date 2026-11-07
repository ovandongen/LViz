using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// Modal combo editor. Its result is the answered <c>ZmkCombo</c>, or
/// <c>null</c> when the user declined — Esc, the window's close button, or
/// Cancel. Enter acts as OK (the default button), which is only reachable
/// while nothing blocks saving.
/// </summary>
public partial class ComboDialog : Window
{
    public ComboDialog()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    /// <summary>The control the key editor (ring or key dialog) anchors on.</summary>
    public Control BindingAnchor => BindingButton;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) =>
        Close(DataContext is ComboDialogViewModel vm ? vm.Result() : null);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
