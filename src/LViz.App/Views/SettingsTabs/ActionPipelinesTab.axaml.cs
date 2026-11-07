using Avalonia.Controls;
using LViz.App.ViewModels;

namespace LViz.App.Views.SettingsTabs;

public partial class ActionPipelinesTab : UserControl
{
    public ActionPipelinesTab()
    {
        InitializeComponent();
    }

    /// <summary>"New pipeline" button → open the editor popup with a blank pipeline.</summary>
    private void OnNewPipelineClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => EditPipeline(null);

    /// <summary>A pipeline card click → open the editor popup for that pipeline.</summary>
    private void OnEditPipelineClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { Tag: PipelineEditRow row })
            EditPipeline(row);
    }

    /// <summary>
    /// Shows the single-screen pipeline editor as a modal and feeds the outcome
    /// (Save / Delete / Cancel) back into the Action Pipelines VM, which applies it
    /// to the in-memory library. Persistence still happens on window close, when
    /// <see cref="SettingsWindow"/> commits the VM.
    /// </summary>
    private async void EditPipeline(PipelineEditRow? source)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var editor = vm.ActionPipelines.CreateEditor(source);
        var dlg = new EditPipelineDialog { DataContext = editor };
        var outcome = await dlg.ShowDialog<PipelineEditorOutcome>(owner);
        vm.ActionPipelines.ApplyEditorResult(source, outcome, editor);
        editor.Dispose();
    }
}
