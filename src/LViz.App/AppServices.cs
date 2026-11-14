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
using LViz.Core.Persistence;
using LViz.Core.Settings;
using ZmkHidProtocol.ActiveWindow;

namespace LViz.App;

/// <summary>
/// Composition root for App-level services. Anything new gets registered
/// here as the canonical pattern; no inline <c>new</c> in <c>App.axaml.cs</c>.
///
/// <para>Phase B (interface extraction) is complete for
/// <see cref="IGlobalHotkeyService"/>, <see cref="ILayerPushCoordinator"/>,
/// <see cref="IMouseLayerEngine"/>, and <see cref="IKeyHighlightTracker"/>.
/// Those four are not registered in the container because they each need
/// state owned by <see cref="ViewModels.MainWindowViewModel"/> at
/// construction time (initial profile, <c>getRenderedLayer</c> closure, or
/// the live <c>Keys</c> collection). They remain MainVM-scoped, but the
/// interface extraction means anything that <i>consumes</i> them
/// (SettingsViewModel, tests, future split-out engines) can hold the
/// abstraction.</para>
///
/// <para>Phase D registers <see cref="MainWindowViewModel"/> as a
/// singleton and <see cref="SettingsViewModel"/> as transient — App.axaml
/// resolves both through the container instead of new'ing them inline.</para>
///
/// <para>Disposal: the provider owns the singletons it constructs.
/// App.axaml disposes the provider on <c>desktop.Exit</c>; individual
/// per-service Exit handlers are no longer required.</para>
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
        // therefore MainWindowViewModel-scoped (same reason as the four
        // interfaces noted below).
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
        // flash window runs on. IKeyboardFlashAccess reaches the device stack
        // through the main view-model; it is only ever resolved when the
        // flash window opens, which is after the view-model exists.
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
            var vm = sp.GetRequiredService<MainWindowViewModel>();
            return new KeyboardFlashAccess(
                vm.Devices.Hid, vm.Devices.Router, sp.GetRequiredService<ICapabilityControl>());
        });
        services.AddTransient(sp => new FlashRun(
            sp.GetRequiredService<IKeyboardFlashAccess>(),
            new Uf2DriveWatcher(new SystemUf2DriveSource())));

        // Window lifecycle + the shell the view-model opens windows through;
        // both look the main window and view-model up at call time.
        services.AddSingleton(sp => new MainWindowLifecycle(
            sp.GetRequiredService<ISettingsService>(),
            () => sp.GetRequiredService<MainWindowViewModel>()));
        services.AddSingleton<IMainWindowShell>(sp => new AvaloniaMainWindowShell(
            sp, sp.GetRequiredService<MainWindowLifecycle>()));

        // Every dependency is passed explicitly. The monitors are genuinely
        // optional: no active-window monitor disables app-focus switching, no
        // mouse-idle monitor leaves MouseLayer null.
        services.AddSingleton<MainWindowViewModel>(sp =>
            new MainWindowViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IKeymapStateService>(),
                sp.GetRequiredService<KeymapBackupStore>(),
                sp.GetRequiredService<IKeymapEditDialogs>(),
                sp.GetRequiredService<IMainWindowShell>(),
                sp.GetRequiredService<IHostActionExecutor>(),
                sp.GetRequiredService<ICapabilityControl>(),
                sp.GetRequiredService<IGitHubKeymapService>(),
                sp.GetRequiredService<FirmwareBuildWatcher>(),
                sp.GetService<IActiveWindowMonitor>(),
                sp.GetService<IMouseIdleMonitor>()));

        services.AddTransient<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<MainWindowViewModel>(),
                sp.GetRequiredService<UpdateService>(),
                sp.GetRequiredService<ICapabilityControl>(),
                sp.GetRequiredService<ICliToolInstaller>()));

        return services.BuildServiceProvider();
    }
}
