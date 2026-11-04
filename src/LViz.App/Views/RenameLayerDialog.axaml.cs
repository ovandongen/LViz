using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// Modal rename-layer prompt. Its result is the new name, or <c>null</c> when
/// the user declined — Esc, the window's close button, or Cancel. Enter acts
/// as Rename (the default button), which is only reachable while a usable new
/// name is typed.
/// </summary>
public partial class RenameLayerDialog : Window
{
    public RenameLayerDialog()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Focus with the current name selected, so typing replaces it at
        // once. Deferred one dispatcher turn: initial focus lands after
        // Opened and would override an immediate call.
        Dispatcher.UIThread.Post(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        });
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void OnRenameClick(object? sender, RoutedEventArgs e) =>
        Close(DataContext is RenameLayerDialogViewModel vm ? vm.Answer() : null);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
