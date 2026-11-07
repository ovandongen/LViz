using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using LViz.App.ViewModels;
using LViz.Core.Diagnostics;
using LViz.Core.Settings;

namespace LViz.App.Views;

/// <summary>
/// The main window's life: restoring and saving its position and size, show /
/// hide, and the quit guard that every way out (window close, tray Quit, the
/// Quit command) goes through. Window and view-model are looked up at call
/// time, so the container can build this before either exists.
/// </summary>
internal sealed class MainWindowLifecycle
{
    private readonly ISettingsService _settings;
    private readonly Func<MainWindowViewModel> _viewModel;
    private bool _closingHandled;

    // Both quit paths await the unsaved-edits prompt, so a second close click
    // or tray Quit lands while the first prompt is still open. Without this
    // flag that queues a second prompt, and with no edits two quick quits run
    // Shutdown() twice.
    private bool _quitInProgress;

    public MainWindowLifecycle(ISettingsService settings, Func<MainWindowViewModel> viewModel)
    {
        _settings = settings;
        _viewModel = viewModel;
    }

    private static MainWindow Window => MainWindowOwner.Current;

    /// <summary>Applies the saved position/size (or centres on first launch)
    /// and routes the window's close through the quit guard.</summary>
    public void Attach(MainWindow window, UserSettings settings)
    {
        if (settings.WindowX.HasValue && settings.WindowY.HasValue)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint((int)settings.WindowX.Value, (int)settings.WindowY.Value);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (settings.WindowWidth.HasValue)
            window.Width = settings.WindowWidth.Value;
        if (settings.WindowHeight.HasValue)
            window.Height = settings.WindowHeight.Value;

        DiagnosticLog.Info("Startup", $"Window position: {window.Position.X},{window.Position.Y} size: {window.Width}x{window.Height}");

        // Validate restored position is on a visible screen
        window.Opened += (_, _) =>
        {
            if (window.Screens.ScreenFromWindow(window) is null)
                window.Position = new PixelPoint(0, 0);
        };

        window.Closing += (_, e) =>
        {
            if (_closingHandled) return;
            e.Cancel = true;
            QuitAfterConfirm(() =>
            {
                _closingHandled = true;
                window.Close();
            });
        };
    }

    /// <summary>Confirm discarding unsaved keymap edits, then save the window
    /// state, shut the view-model down and run <paramref name="finish"/>. With
    /// no edits the prompt is skipped. The in-progress flag resets however this
    /// ends (cancel, or a throw before <paramref name="finish"/> exits), so a
    /// later quit can still ask again.</summary>
    public void QuitAfterConfirm(Action finish)
    {
        if (_quitInProgress) return;
        _quitInProgress = true;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (!await ConfirmDiscardEditsAsync()) return;
                SaveWindowState();
                _viewModel().Shutdown();
                finish();
            }
            finally
            {
                _quitInProgress = false;
            }
        });
    }

    public void Show()
    {
        var window = Window;
        window.Show();
        window.Activate();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
    }

    public void Toggle()
    {
        if (Window.IsVisible)
            Window.Hide();
        else
            Show();
    }

    private void SaveWindowState()
    {
        var window = Window;
        if (window.WindowState == WindowState.Minimized) return;
        _settings.Save(_settings.Load() with
        {
            WindowX = window.Position.X,
            WindowY = window.Position.Y,
            WindowWidth = window.Width,
            WindowHeight = window.Height,
        });
    }

    private async Task<bool> ConfirmDiscardEditsAsync()
    {
        var unsaved = _viewModel().Edit?.ChangeCount ?? 0;
        if (unsaved == 0) return true;
        // The global hotkey can hide the window while the user keeps
        // editing, and a modal dialog on a hidden owner never shows.
        var window = Window;
        window.Show();
        window.Activate();
        var dlg = new ChoiceDialog
        {
            DataContext = ChoiceDialogViewModel.QuitWithUnsavedEdits(unsaved),
        };
        var result = await dlg.ShowDialog<string?>(window);
        return result == "quit";
    }
}
