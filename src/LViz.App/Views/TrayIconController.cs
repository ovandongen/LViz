using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// The tray icon declared in <c>App.axaml</c>: tints it (and the window icon,
/// for the Windows taskbar) by the active layer and keeps its menu headers in
/// the current culture. The menu items' commands are bound in XAML.
/// </summary>
internal sealed class TrayIconController
{
    private static readonly (string Key, string ResKey)[] TrayMenuKeys =
    [
        ("Show", "Tray_ShowLayers"),
        ("Quit", "Tray_Quit"),
    ];

    private readonly TrayIcon _trayIcon;
    private readonly MainWindow _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly TrayIconTinter _tinter = new();

    private TrayIconController(TrayIcon trayIcon, MainWindow window, MainWindowViewModel viewModel)
    {
        _trayIcon = trayIcon;
        _window = window;
        _viewModel = viewModel;
    }

    /// <summary>Wires the application's first tray icon, if it declares one;
    /// returns an action that detaches the culture handler (call on Exit).</summary>
    public static Action? Attach(Application app, MainWindow window, MainWindowViewModel viewModel)
    {
        var trayIcons = TrayIcon.GetIcons(app);
        if (trayIcons is not { Count: > 0 }) return null;
        return new TrayIconController(trayIcons[0], window, viewModel).Start();
    }

    private Action Start()
    {
        ApplyIcon();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainWindowViewModel.ActiveLayerTintColor)
                or nameof(MainWindowViewModel.ColorTrayIconByActiveLayer)
                or nameof(MainWindowViewModel.SelectedKeyboard))
            {
                Dispatcher.UIThread.Post(ApplyIcon);
            }
        };

        LocalizeMenu();
        // Named delegate so we can detach on Exit. Without the -=, every
        // runtime culture switch leaks the prior handler's tray-icon
        // capture and re-localizes already-collected closures.
        Action onCultureChanged = () => Dispatcher.UIThread.Post(LocalizeMenu);
        Loc.CultureChanged += onCultureChanged;
        return () => Loc.CultureChanged -= onCultureChanged;
    }

    private void ApplyIcon()
    {
        var icon = _viewModel.ColorTrayIconByActiveLayer
            ? _tinter.GetTinted(_viewModel.ActiveLayerTintColor)
            : _tinter.GetOriginal();
        _trayIcon.Icon = icon;
        // Mirror onto the main window so the Windows taskbar entry
        // tracks too. macOS Dock ignores Window.Icon (bundle-bound).
        _window.Icon = icon;
    }

    private void LocalizeMenu()
    {
        _trayIcon.ToolTipText = Loc.Instance["Tray_Tooltip"];
        if (_trayIcon.Menu is not { } menu) return;

        var menuItems = menu.Items.OfType<NativeMenuItem>().ToList();
        for (var i = 0; i < menuItems.Count && i < TrayMenuKeys.Length; i++)
            menuItems[i].Header = Loc.Instance[TrayMenuKeys[i].ResKey];
    }
}
