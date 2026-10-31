using Avalonia.Controls;
using Avalonia.Threading;
using LViz.App.ViewModels;
using LViz.Core.Layout;

namespace LViz.App.Views;

/// <summary>
/// Stand-alone "Edit layout…" window for a user layout: the shared
/// <see cref="LayoutEditorCanvas"/> over the saved keys, with save and cancel
/// in the bottom bar. Its <c>DataContext</c> must be a
/// <see cref="LayoutEditWindowViewModel"/>; the VM closes the dialog by raising
/// <see cref="LayoutEditWindowViewModel.CloseRequested"/> (null = cancel, the
/// written <see cref="UserLayoutFile"/> = saved), which this code-behind turns
/// into <c>Close(result)</c> — so <c>ShowDialog</c> returns exactly that.
/// </summary>
public partial class LayoutEditWindow : Window
{
    private LayoutEditWindowViewModel? _boundVm;

    public LayoutEditWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel();
        // Focus so the arrow keys nudge the selection without a click first;
        // posted so the canvas has its Editor (and thus its bounds) by then.
        Opened += (_, _) => Dispatcher.UIThread.Post(() => Canvas.Focus());
    }

    private void BindViewModel()
    {
        if (_boundVm is { } old)
        {
            old.CloseRequested -= OnCloseRequested;
            _boundVm = null;
        }

        if (DataContext is not LayoutEditWindowViewModel vm)
            return;

        vm.CloseRequested += OnCloseRequested;
        _boundVm = vm;
    }

    private void OnCloseRequested(UserLayoutFile? result) => Close(result);

    protected override void OnClosed(EventArgs e)
    {
        if (_boundVm is { } vm)
            vm.CloseRequested -= OnCloseRequested;
        _boundVm = null;
        base.OnClosed(e);
    }
}
