using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LViz.App.Views;

/// <summary>
/// Read-only "Keyboard shortcuts" window: every shortcut from
/// <see cref="ViewModels.ShortcutCatalog"/>, listed by group. Opened from the
/// Help menu; Esc or the Close button dismisses it.
/// </summary>
public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
