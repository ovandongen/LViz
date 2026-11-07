using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.Hotkeys;
using LViz.App.ViewModels;
using LViz.App.Views;
using LViz.Core.Diagnostics;
using LViz.Core.Layout;
using LViz.Core.Settings;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;

namespace LViz.App;

/// <summary>The composition step: builds the services, the main window and
/// the long-lived helpers around it, in startup order.</summary>
public partial class App : Application
{
    public override void Initialize()
    {
        // Register Font Awesome icon provider before XAML is loaded so any
        // <i:Icon> controls inside templates resolve fa-* identifiers.
        IconProvider.Current.Register<FontAwesomeIconProvider>();

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            LogUnhandledExceptions();

            // Composition root. AppServices owns construction of every
            // service that the container can manage; per-service Exit
            // disposal handlers are gone — the provider disposes its
            // IDisposable singletons on the single Exit hook below.
            var services = AppServices.BuildProvider();
            desktop.Exit += (_, _) => services.Dispose();

            var settingsService = services.GetRequiredService<ISettingsService>();
            var initialSettings = settingsService.Load();
            InitializeSettingsAndLogging(initialSettings);
            LoadUserLayouts();

            DiagnosticLog.Info("Startup", "Creating MainWindowViewModel...");
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            var mainWindow = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = mainWindow;

            var lifecycle = services.GetRequiredService<MainWindowLifecycle>();
            lifecycle.Attach(mainWindow, initialSettings);

            if (TrayIconController.Attach(this, mainWindow, viewModel) is { } detachTray)
                desktop.Exit += (_, _) => detachTray();

            StartGlobalHotkey(services, viewModel, lifecycle, initialSettings);

            // Restore the last-loaded layout (or show a "pick a file" prompt).
            Dispatcher.UIThread.Post(() => viewModel.InitializeAsync(), DispatcherPriority.Background);

            if (initialSettings.AutoCheckForUpdates)
                ScheduleUpdateCheck(services.GetRequiredService<UpdateService>());

            DataContext = viewModel;
            DiagnosticLog.Info("Startup", "OnFrameworkInitializationCompleted done");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void LogUnhandledExceptions()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DiagnosticLog.Error("Unhandled", $"AppDomain unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DiagnosticLog.Error("Unhandled", $"Unobserved task: {e.Exception}");
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            DiagnosticLog.Error("Unhandled", $"UI dispatcher: {e.Exception}");
            e.Handled = true;
        };
    }

    private static void InitializeSettingsAndLogging(UserSettings settings)
    {
        Loc.Instance.SetCulture(settings.Language);

        var envLevel = Environment.GetEnvironmentVariable("LVIZ_LOG_LEVEL");
        if (!string.IsNullOrEmpty(envLevel) && Enum.TryParse<LogLevel>(envLevel, true, out var envLogLevel))
            DiagnosticLog.SetMinimumLevel(envLogLevel);
        else if (Enum.TryParse<LogLevel>(settings.LogLevel, true, out var logLevel))
            DiagnosticLog.SetMinimumLevel(logLevel);
    }

    /// <summary>User layouts must be in the registry before any view-model
    /// resolves a saved keyboard id or builds a picker list.</summary>
    private static void LoadUserLayouts()
    {
        var userLayouts = UserLayoutStore.LoadAll(UserLayoutStore.DefaultDirectory);
        KeyboardProfileRegistry.SetUserProfiles(userLayouts.Select(f => new UserLayoutProfile(f)));
        DiagnosticLog.Info("Layouts", $"Loaded {userLayouts.Count} user layout(s)");
    }

    /// <summary>Global show/hide hotkey. The registry's Linux stub returns
    /// failure on TryRegister, so the service no-ops on Linux without a
    /// special case here.</summary>
    private void StartGlobalHotkey(
        IServiceProvider services, MainWindowViewModel viewModel, MainWindowLifecycle lifecycle,
        UserSettings initialSettings)
    {
        var hotkeyService = services.GetRequiredService<IGlobalHotkeyService>();
        hotkeyService.HotkeyPressed = lifecycle.Toggle;
        hotkeyService.UpdateHotkey(initialSettings.HotkeyKey, initialSettings.HotkeyModifiers);
        hotkeyService.Start();
        viewModel.Preferences.HotkeyKeyChanged += newKey =>
            hotkeyService.UpdateHotkey(newKey, viewModel.Preferences.HotkeyModifiers);
    }

    /// <summary>One-shot background update check ~5s after launch. No-ops when
    /// not running as a Velopack install (dev / dotnet run); silently 404s
    /// while the source repo is private.</summary>
    private static void ScheduleUpdateCheck(UpdateService updateService)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await updateService.CheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn("Update", $"Background check threw: {ex.Message}");
            }
        });
    }
}
