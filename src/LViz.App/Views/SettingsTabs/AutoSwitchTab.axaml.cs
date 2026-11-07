using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using LViz.App.ViewModels;

namespace LViz.App.Views.SettingsTabs;

public partial class AutoSwitchTab : UserControl
{
    public AutoSwitchTab()
    {
        InitializeComponent();
    }

    private AutoSwitchSettingsViewModel? Vm => (DataContext as SettingsViewModel)?.AutoSwitch;

    /// <summary>
    /// Refreshes the running-process snapshot right before the picker
    /// flyout opens so the bound ListBox shows fresh data. Wired in XAML
    /// via <c>Flyout.Opening</c>.
    /// </summary>
    private void OnPickProcessFlyoutOpening(object? sender, System.EventArgs e) =>
        Vm?.RefreshRunningProcesses();

    // ListBox row selection → drop the process name into the new-rule textbox
    // and close the flyout. Using SelectionChanged (not a per-item
    // PointerPressed) makes the whole row hit-testable, not just the text glyphs.
    private void OnProcessPickerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb || lb.SelectedItem is not string name) return;
        if (Vm is not { } vm) return;
        vm.PickProcessCommand.Execute(name);
        lb.SelectedItem = null;
        if (this.FindControl<Button>("PickProcessButton") is { } btn &&
            btn.Flyout is FlyoutBase flyout)
        {
            flyout.Hide();
        }
    }

    /// <summary>
    /// Opens the exit-tap key picker as a modal dialog. On OK, hands the
    /// resulting (nullable) firmware key index back to the auto-switch VM,
    /// which persists it via the engine (which re-arms the
    /// <c>MultiTapDetector</c>). Cancel = no-op.
    /// </summary>
    private async void OnPickExitKeysClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var profile = vm.ActiveKeyboardProfile;
        if (profile is null) return;

        var pickerVm = new ExitKeyPickerViewModel(profile, vm.ExitTapKey);
        var window = new ExitKeyPickerWindow { DataContext = pickerVm };
        var result = await window.ShowDialog<(bool ok, int? selection)>(owner);
        if (!result.ok) return;
        vm.ApplyExitTapKey(result.selection);
    }
}
