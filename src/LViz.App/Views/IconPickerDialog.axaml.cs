using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LViz.App.Views;

/// <summary>
/// Catalog-driven Font Awesome icon picker. <c>ShowDialog&lt;string?&gt;</c>
/// returns: <c>null</c> when the user cancelled, an empty string when the
/// user explicitly cleared the selection, otherwise the chosen
/// <c>fa-{name}</c> string suitable for the projektanker
/// <c>&lt;i:Icon Value="…"/&gt;</c> control.
/// </summary>
public partial class IconPickerDialog : Window
{
    public IconPickerDialog()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close((string?)null);
            e.Handled = true;
        }
    }

    private void OnIconButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
            Close((string?)name);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close((string?)null);

    private void OnClearClick(object? sender, RoutedEventArgs e) => Close((string?)"");
}
