using LViz.App.Services;
using LViz.App.Services.GitHub;
using LViz.App.Services.MouseIdle;
using LViz.App.ViewModels;
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
        Func<string, bool>? fileExists = null)
    {
        var vm = new MainWindowViewModel(
            settings,
            keymapState ?? new KeymapStateService(),
            keymapBackups ?? new KeymapBackupStore(
                Path.Combine(Path.GetTempPath(), $"lviz-test-backups-{Guid.NewGuid():N}")),
            editDialogs ?? new FakeKeymapEditDialogs(),
            shell ?? new FakeMainWindowShell(),
            hostActionExecutor ?? new HostActionExecutor(),
            capabilityControl ?? new CapabilityControl(),
            gitHub,
            activeWindowMonitor,
            mouseIdleMonitor);
        if (fileExists is not null)
            vm.Library.FileExists = fileExists;
        return vm;
    }
}
