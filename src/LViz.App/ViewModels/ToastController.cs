using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LViz.App.ViewModels;

/// <summary>
/// Transient toast banner state. <see cref="Show"/> displays a message that
/// auto-dismisses after <see cref="DurationMs"/>; re-entry cancels the previous
/// timer so a new message gets the full display window, and
/// <see cref="DismissCommand"/> (clicking the toast) dismisses early.
/// </summary>
public partial class ToastController : ObservableObject
{
    private const int DurationMs = 4000;

    // Cancels the auto-dismiss timer on a re-shown toast or manual dismiss.
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isVisible;

    public void Show(string message)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        Message = message;
        IsVisible = true;

        _ = Task.Delay(DurationMs, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_cts == cts)
                {
                    IsVisible = false;
                    _cts = null;
                    cts.Dispose();
                }
            });
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private void Dismiss()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        IsVisible = false;
    }
}
