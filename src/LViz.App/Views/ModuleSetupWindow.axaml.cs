using Avalonia.Controls;
using Avalonia.Threading;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// Modal four-step module setup wizard window (check → choose → review → done)
/// for adding the live-feature firmware modules to a zmk-config. Its
/// <c>DataContext</c> must be a <see cref="ModuleSetupViewModel"/>; the VM
/// closes the dialog by raising
/// <see cref="ModuleSetupViewModel.CloseRequested"/> (the saved flag), which
/// this code-behind turns into <c>Close(saved)</c> — so <c>ShowDialog</c>
/// returns exactly that, and closing with the window's ✕ gives false. While
/// the window is open, a one-second timer keeps the Done step's board status
/// fresh, because <see cref="ModuleSetupViewModel.IsBoardConnected"/> never
/// updates by itself.
/// </summary>
public partial class ModuleSetupWindow : Window
{
    private ModuleSetupViewModel? _boundVm;
    private readonly DispatcherTimer _boardTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ModuleSetupWindow()
    {
        InitializeComponent();

        _boardTimer.Tick += (_, _) =>
        {
            if (_boundVm is { IsDoneStep: true } vm)
                vm.RefreshBoardStatus();
        };
        Opened += (_, _) => _boardTimer.Start();

        DataContextChanged += (_, _) => BindViewModel();
    }

    private void BindViewModel()
    {
        if (_boundVm is { } old)
        {
            old.CloseRequested -= OnCloseRequested;
            _boundVm = null;
        }

        if (DataContext is not ModuleSetupViewModel vm)
            return;

        vm.CloseRequested += OnCloseRequested;
        _boundVm = vm;
    }

    private void OnCloseRequested(bool saved) => Close(saved);

    protected override void OnClosed(EventArgs e)
    {
        _boardTimer.Stop();
        if (_boundVm is { } vm)
            vm.CloseRequested -= OnCloseRequested;
        _boundVm = null;
        base.OnClosed(e);
    }
}
