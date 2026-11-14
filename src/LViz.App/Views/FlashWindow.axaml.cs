using Avalonia.Controls;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// Modal four-step flash window (source → halves → flash → done) for putting
/// firmware on a ZMK board. Its <c>DataContext</c> must be a
/// <see cref="FlashViewModel"/>; the VM closes the dialog by raising
/// <see cref="FlashViewModel.CloseRequested"/> (the flashed flag), which this
/// code-behind turns into <c>Close(flashed)</c> — so <c>ShowDialog</c>
/// returns exactly that, and closing with the window's ✕ gives false.
/// </summary>
public partial class FlashWindow : Window
{
    private FlashViewModel? _boundVm;

    public FlashWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel();
    }

    private void BindViewModel()
    {
        if (_boundVm is { } old)
        {
            old.CloseRequested -= OnCloseRequested;
            _boundVm = null;
        }

        if (DataContext is not FlashViewModel vm)
            return;

        vm.CloseRequested += OnCloseRequested;
        _boundVm = vm;
    }

    private void OnCloseRequested(bool flashed) => Close(flashed);

    protected override void OnClosed(EventArgs e)
    {
        if (_boundVm is { } vm)
        {
            // Close button or the window's ✕: nothing may keep flashing behind it.
            vm.CancelWork();
            vm.CloseRequested -= OnCloseRequested;
        }
        _boundVm = null;
        base.OnClosed(e);
    }
}
