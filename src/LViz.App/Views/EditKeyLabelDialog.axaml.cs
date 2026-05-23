using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LViz.App.ViewModels;
using LViz.Core.Settings;

namespace LViz.App.Views;

/// <summary>
/// Modal editor for a single (layer, key) label override. <c>ShowDialog</c>
/// returns a <see cref="Result"/> tuple — <c>Saved=true</c> with
/// <c>Value=null</c> means "remove the override"; <c>Saved=true</c> with a
/// non-null value means "apply this override"; <c>Saved=false</c> means the
/// user cancelled and nothing should change.
/// </summary>
public partial class EditKeyLabelDialog : Window
{
    public readonly record struct Result(bool Saved, KeyLabelOverride? Value);

    public EditKeyLabelDialog()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(new Result(false, null));
            e.Handled = true;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditKeyLabelDialogViewModel vm)
        {
            Close(new Result(false, null));
            return;
        }
        var ov = vm.ToOverride();
        // Treat "save with all-defaults" the same as "clear" so the persistence
        // layer doesn't store a no-op entry.
        Close(new Result(true, ov.IsEmpty ? null : ov));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => Close(new Result(false, null));

    private void OnClearOverrideClick(object? sender, RoutedEventArgs e)
        => Close(new Result(true, null));

    private void OnResetFontSizeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EditKeyLabelDialogViewModel vm)
            vm.ResetFontSize();
    }

    private void OnClearIconClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EditKeyLabelDialogViewModel vm)
            vm.ClearIcon();
    }

    private async void OnPickIconClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditKeyLabelDialogViewModel vm) return;
        var picker = new IconPickerDialog { DataContext = new IconPickerViewModel(vm.Icon) };
        var picked = await picker.ShowDialog<string?>(this);
        // picker returns: null = cancel, "" = explicit clear, otherwise the fa-* name.
        if (picked is not null)
            vm.Icon = picked;
    }
}
