using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LViz.App.ViewModels;
using LViz.Core.Settings;

namespace LViz.App.Views;

/// <summary>
/// Modal editor for a single combo-pill label override. Mirrors
/// <see cref="EditKeyLabelDialog"/>'s Result semantics —
/// <c>Saved=true, Value=null</c> means "remove the override",
/// <c>Saved=true, Value=non-null</c> means "apply this override",
/// <c>Saved=false</c> means "cancelled".
/// </summary>
public partial class EditComboLabelDialog : Window
{
    public readonly record struct Result(bool Saved, ComboLabelOverride? Value);

    public EditComboLabelDialog()
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
        if (DataContext is not EditComboLabelDialogViewModel vm)
        {
            Close(new Result(false, null));
            return;
        }
        var ov = vm.ToOverride();
        Close(new Result(true, ov.IsEmpty ? null : ov));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => Close(new Result(false, null));

    private void OnClearOverrideClick(object? sender, RoutedEventArgs e)
        => Close(new Result(true, null));
}
