using LViz.App.Services;
using LViz.App.Services.Flashing;
using LViz.App.Services.GitHub;
using LViz.App.Services.MouseIdle;
using LViz.App.ViewModels;
using LViz.Core.Layout;
using LViz.Core.Persistence;
using LViz.Core.Settings;
using ZmkHidProtocol.ActiveWindow;

namespace LViz.Tests;

/// <summary>
/// The one way tests build a <see cref="MainWindowViewModel"/>: the production
/// constructor takes every dependency, so this supplies test defaults (fake
/// shell and dialogs, a temp-dir backup store, the real keymap state, no GitHub
/// service, no monitors) and a test overrides only what it exercises.
/// </summary>
internal static class TestMainVm
{
    public static MainWindowViewModel Create(
        ISettingsService settings,
        IKeymapStateService? keymapState = null,
        IGitHubKeymapService? gitHub = null,
        IMainWindowShell? shell = null,
        IKeymapEditDialogs? editDialogs = null,
        KeymapBackupStore? keymapBackups = null,
        IActiveWindowMonitor? activeWindowMonitor = null,
        IMouseIdleMonitor? mouseIdleMonitor = null,
        ICapabilityControl? capabilityControl = null,
        IHostActionExecutor? hostActionExecutor = null,
        Func<string, bool>? fileExists = null,
        FirmwareBuildWatcher? firmwareWatcher = null,
        KeyboardProfileRegistry? profiles = null)
    {
        // The stack is built here exactly as the container builds it, so a
        // test's monitors/control land inside the engines; the registry
        // defaults to bundled boards only.
        var registry = profiles ?? new KeyboardProfileRegistry();
        var shownLayer = new ShownLayer();
        var executor = hostActionExecutor ?? new HostActionExecutor();
        var devices = DeviceStack.Create(
            registry.ResolveOrDefault(settings.Current.Keyboard),
            settings, shownLayer, activeWindowMonitor, mouseIdleMonitor,
            capabilityControl ?? new CapabilityControl(), executor);
        var vm = new MainWindowViewModel(
            settings,
            keymapState ?? new KeymapStateService(),
            keymapBackups ?? new KeymapBackupStore(
                Path.Combine(Path.GetTempPath(), $"lviz-test-backups-{Guid.NewGuid():N}")),
            editDialogs ?? new FakeKeymapEditDialogs(),
            shell ?? new FakeMainWindowShell(),
            executor,
            gitHub,
            firmwareWatcher,
            devices,
            shownLayer,
            registry);
        if (fileExists is not null)
            vm.Library.FileExists = fileExists;
        return vm;
    }

    /// <summary>
    /// The one way tests build a <see cref="SettingsViewModel"/>: the device
    /// stack rides in on the main VM, the control and installer are the plain
    /// implementations (the real ones need platform services the tests don't give).
    /// </summary>
    public static SettingsViewModel CreateSettings(ISettingsService settings, MainWindowViewModel vm) =>
        new(settings, vm, vm.Devices, new UpdateService(settings), new CapabilityControl(), new CliToolInstaller());
}
