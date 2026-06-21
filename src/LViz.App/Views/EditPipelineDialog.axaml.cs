using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// Single-screen modal editor for one action pipeline. <c>ShowDialog</c> returns a
/// <see cref="PipelineEditorOutcome"/>: <c>Save</c> means apply the dialog VM's
/// <see cref="EditPipelineDialogViewModel.Working"/> copy, <c>Delete</c> means drop
/// the pipeline, <c>Cancel</c> means leave it untouched. The owner
/// (<see cref="SettingsWindow"/>) holds the VM and applies the outcome.
/// </summary>
public partial class EditPipelineDialog : Window
{
    public EditPipelineDialog()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(PipelineEditorOutcome.Cancel);
            e.Handled = true;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // The Save button is disabled unless CanSave, but guard anyway.
        if (DataContext is EditPipelineDialogViewModel { CanSave: true })
            Close(PipelineEditorOutcome.Save);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => Close(PipelineEditorOutcome.Cancel);

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
        => Close(PipelineEditorOutcome.Delete);
}
