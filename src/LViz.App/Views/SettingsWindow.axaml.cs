using Avalonia.Controls;
using LViz.App.ViewModels;

namespace LViz.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Apply-on-close for the edit buffers (app-layer rules, action pipelines).
    /// The tab VMs own the editable copies; only on close do we push them back,
    /// which persists and re-fires the engine. Catches all exit paths
    /// (X button, Esc, alt-F4) since they all funnel through OnClosing.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is SettingsViewModel vm)
        {
            vm.AutoSwitch.CommitAppLayerRules();
            vm.ActionPipelines.Commit();
        }
    }
}
