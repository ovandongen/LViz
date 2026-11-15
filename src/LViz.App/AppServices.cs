using Microsoft.Extensions.DependencyInjection;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.Flashing;
using LViz.App.Services.GitHub;
using LViz.App.Services.Hotkeys;
using LViz.App.Services.MouseIdle;
using LViz.App.Services.Secrets;
using LViz.App.ViewModels;
using LViz.App.Views;
using LViz.Core.Diagnostics;
using LViz.Core.Firmware;
using LViz.Core.GitHub;
using LViz.Core.Layout;
using LViz.Core.Persistence;
using LViz.Core.Settings;
using ZmkHidProtocol.ActiveWindow;

namespace LViz.App;

/// <summary>
/// Composition root for App-level services. Anything new gets registered
/// here as the canonical pattern; no inline <c>new</c> in <c>App.axaml.cs</c>.
///
/// <para>Every service the app uses is registered here, including
/// <see cref="ShownLayer"/> and the <see cref="DeviceStack"/> built once as
/// a singleton through <see cref="DeviceStack.Create"/> (nothing is started
/// at registration: <c>App.axaml.cs</c> calls
/// <see cref="DeviceStack.StartIpc"/> once on the resolved stack).
/// <see cref="MainWindowViewModel"/> is registered as a singleton and
/// <see cref="SettingsViewModel"/> as transient;
/// <see cref="AvaloniaMainWindowShell"/> gets its services by name and the
/// view-models of the windows it opens as factories, each running only when
/// its window opens.</para>
///
/// <para>Disposal: the provider owns the singletons it constructs.
/// App.axaml disposes the provider on <c>desktop.Exit</c>; individual
/// per-service Exit handlers are no longer required. The main view-model
/// also disposes the device stack at quit;
/// <see cref="DeviceStack.Dispose"/> is idempotent, so the provider's later
/// pass is a no-op.</para>
/// </summary>
internal static class AppServices
{
    public static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<INativeHotkeyRegistry>(_ => NativeHotkeyRegistryFactory.Create());
        services.AddSingleton<IMouseIdleMonitor>(_ => MouseIdleMonitorFactory.Create());
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<UpdateService>();

        // App-originated capability control (layer + pointing actions, confirm
        // plane). Stateless, so it lives in the container — unlike the capability
        // *router*, which needs the live keyboard pipeline + device bus and is
        // therefore built inside the DeviceStack singleton below.
        services.AddSingleton<ICapabilityControl>(_ => new CapabilityControl());

        // Host-process spawner for the Launch/Shell action-pipeline steps.
        // Stateless and platform-branching, so it lives in the container.
        services.AddSingleton<IHostActionExecutor, HostActionExecutor>();

        // Installs the `lviz` CLI symlink onto PATH (Settings button). Stateless +
        // platform-branching, same rationale as the host executor above.
        services.AddSingleton<ICliToolInstaller, CliToolInstaller>();

        // Active-window monitor can fail to construct on some platforms
        // (missing permissions, headless test runners, …). We register it
        // only when construction succeeds; consumers resolve via
        // GetService (nullable) so the absence path stays exercised.
        IActiveWindowMonitor? activeWindowMonitor = null;
        try
        {
            activeWindowMonitor = ActiveWindowMonitorFactory.Create();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Startup", $"ActiveWindowMonitor unavailable: {ex.Message}");
        }
        if (activeWindowMonitor is not null)
            services.AddSingleton(activeWindowMonitor);

        // The device stack: HID pipeline, capability bus + router, action
        // pipelines, push coordinators and IPC. The container builds it once;
        // the main view-model receives it and keeps disposing it on quit
        // (Dispose is idempotent, so the provider's Exit disposal is a
        // no-op after that). Nothing is started here — App.axaml calls
        // StartIpc explicitly, so a stack built in a test never listens.
        // The start profile is resolved with the same expression the view-
        // model uses, so both begin on the same board.
        services.AddSingleton<ShownLayer>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            return DeviceStack.Create(
                KeyboardProfileRegistry.ResolveOrDefault(settings.Current.Keyboard),
                settings,
                sp.GetRequiredService<ShownLayer>(),
                sp.GetService<IActiveWindowMonitor>(),
                sp.GetService<IMouseIdleMonitor>(),
                sp.GetRequiredService<ICapabilityControl>(),
                sp.GetRequiredService<IHostActionExecutor>());
        });

        // Both registered against the same instance the static facade
        // delegates to, so DI consumers and the existing XAML / converter
        // call sites see identical state.
        services.AddSingleton<ILocalization>(Loc.Instance);
        services.AddSingleton<ILayerColorService>(LayerColorPalette.Service);
        services.AddSingleton<IKeyLabelOverrideService>(KeyLabelOverrides.Service);
        services.AddSingleton<IComboLabelOverrideService>(ComboLabelOverrides.Service);

        // GitHub zmk-config: device-flow sign-in, repo browsing and the local
        // repo cache, tied into the one service the windows talk to. The
        // secret store is the platform credential store, falling back to
        // in-memory where none exists.
        services.AddSingleton<ISecretStore>(_ => SecretStoreFactory.Create());
        services.AddSingleton(_ => new HttpClient());
        services.AddSingleton(sp => new GitHubClient(
            sp.GetRequiredService<HttpClient>(),
            GitHubAppInfo.ClientId,
            $"LViz/{typeof(GitHubKeymapService).Assembly.GetName().Version}"));
        services.AddSingleton(sp => new GitHubSession(
            sp.GetRequiredService<GitHubClient>(),
            sp.GetRequiredService<ISecretStore>()));
        services.AddSingleton(_ => new GitHubRepoCache(GitHubRepoCache.DefaultRoot));
        services.AddSingleton<IGitHubKeymapService>(sp => new GitHubKeymapService(
            sp.GetRequiredService<GitHubClient>(),
            sp.GetRequiredService<GitHubSession>(),
            sp.GetRequiredService<GitHubRepoCache>()));

        services.AddSingleton<IKeymapStateService, KeymapStateService>();
        services.AddSingleton(_ => new KeymapBackupStore(KeymapBackupStore.DefaultRoot));
        services.AddSingleton<IKeymapEditDialogs, AvaloniaKeymapEditDialogs>();

        // Firmware flashing: the local firmware cache and flash history, the
        // builds service with its own login for the second App (its client
        // and session are internal to the service — never registered as
        // their own types, and its login lives under its own secret key),
        // the watcher the status-bar firmware line reads, and the pieces the
        // flash window runs on. IKeyboardFlashAccess takes the device stack
        // straight from the container; it is only ever resolved when the
        // flash window opens.
        services.AddSingleton(_ => new FirmwareCache(FirmwareCache.DefaultRoot));
        services.AddSingleton(_ => new FirmwareFlashHistory(FirmwareFlashHistory.DefaultPath));
        services.AddSingleton<IFirmwareBuildService>(sp =>
        {
            var buildsClient = new GitHubClient(
                sp.GetRequiredService<HttpClient>(),
                GitHubBuildsAppInfo.ClientId,
                $"LViz/{typeof(GitHubKeymapService).Assembly.GetName().Version}");
            return new FirmwareBuildService(
                buildsClient,
                new GitHubSession(
                    buildsClient,
                    sp.GetRequiredService<ISecretStore>(),
                    secretKey: GitHubBuildsAppInfo.SecretKey),
                sp.GetRequiredService<GitHubClient>(),
                sp.GetRequiredService<GitHubSession>(),
                sp.GetRequiredService<FirmwareCache>());
        });
        services.AddSingleton(sp => new FirmwareBuildWatcher(sp.GetRequiredService<IFirmwareBuildService>()));
        services.AddSingleton<IKeyboardFlashAccess>(sp =>
        {
            var devices = sp.GetRequiredService<DeviceStack>();
            return new KeyboardFlashAccess(
                devices.Hid, devices.Router, sp.GetRequiredService<ICapabilityControl>());
        });
        services.AddTransient(sp => new FlashRun(
            sp.GetRequiredService<IKeyboardFlashAccess>(),
            new Uf2DriveWatcher(new SystemUf2DriveSource())));

        // Window lifecycle + the shell the view-model opens windows through.
        // The lifecycle looks the main window and view-model up at call time;
        // the shell takes named services, and the Settings, module-setup and
        // flash view-models as factories — each runs only when its window
        // opens, and Settings must stay lazy because it needs the main
        // view-model, which needs the shell.
        services.AddSingleton(sp => new MainWindowLifecycle(
            sp.GetRequiredService<ISettingsService>(),
            () => sp.GetRequiredService<MainWindowViewModel>()));
        services.AddSingleton<IMainWindowShell>(sp => new AvaloniaMainWindowShell(
            sp.GetRequiredService<MainWindowLifecycle>(),
            sp.GetRequiredService<IGitHubKeymapService>(),
            sp.GetRequiredService<IHostActionExecutor>(),
            () => sp.GetRequiredService<SettingsViewModel>(),
            context => new ModuleSetupViewModel(
                context,
                sp.GetRequiredService<IGitHubKeymapService>(),
                sp.GetRequiredService<KeymapBackupStore>(),
                Guid.NewGuid),
            context => new FlashViewModel(
                context,
                sp.GetRequiredService<IFirmwareBuildService>(),
                sp.GetRequiredService<FirmwareFlashHistory>(),
                sp.GetRequiredService<IKeyboardFlashAccess>(),
                sp.GetRequiredService<FlashRun>())));

        // Every dependency is passed explicitly; the device stack and the
        // shown layer come from the container. The monitors are genuinely
        // optional and are resolved by the stack's factory: no active-window
        // monitor disables app-focus switching, no mouse-idle monitor leaves
        // MouseLayer null.
        services.AddSingleton<MainWindowViewModel>(sp =>
            new MainWindowViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IKeymapStateService>(),
                sp.GetRequiredService<KeymapBackupStore>(),
                sp.GetRequiredService<IKeymapEditDialogs>(),
                sp.GetRequiredService<IMainWindowShell>(),
                sp.GetRequiredService<IHostActionExecutor>(),
                sp.GetRequiredService<IGitHubKeymapService>(),
                sp.GetRequiredService<FirmwareBuildWatcher>(),
                sp.GetRequiredService<DeviceStack>(),
                sp.GetRequiredService<ShownLayer>()));

        services.AddTransient<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<MainWindowViewModel>(),
                sp.GetRequiredService<DeviceStack>(),
                sp.GetRequiredService<UpdateService>(),
                sp.GetRequiredService<ICapabilityControl>(),
                sp.GetRequiredService<ICliToolInstaller>()));

        return services.BuildServiceProvider();
    }
}
