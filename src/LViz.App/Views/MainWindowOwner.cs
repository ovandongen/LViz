using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace LViz.App.Views;

/// <summary>
/// The application's main window, looked up at call time from the application
/// lifetime: the container builds its services (and the view-model holding
/// them) before the window exists, and a dialog can only be asked for once the
/// window is up.
/// </summary>
internal static class MainWindowOwner
{
    public static MainWindow Current =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow as MainWindow
        ?? throw new InvalidOperationException("The main window is not available to own a dialog.");
}
