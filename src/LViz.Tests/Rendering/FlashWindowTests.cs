using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.Flashing;
using LViz.App.ViewModels;
using LViz.App.Views;
using LViz.Core.Firmware;
using LViz.Core.GitHub;
using LViz.Tests.ZmkConfig;
using Xunit;

namespace LViz.Tests.Rendering;

/// <summary>
/// The flash window over the real cosmos82 fixture root with a local context
/// (so it opens straight in file mode; the services are fakes as in
/// <see cref="FlashViewModelTests"/>): the Source step renders real pixels
/// for review at <see cref="SnapshotPaths"/> and shows the file-mode notice,
/// and the Close command closes the modal dialog with the VM's flashed flag —
/// false when nothing was flashed.
/// </summary>
public class FlashWindowTests
{
    [AvaloniaFact]
    public async Task SourceStep_FileMode_Renders_AndShowsTheFileNotice()
    {
        var vm = LocalVm();
        await vm.StartAsync();
        Assert.True(vm.IsFileMode);

        var window = ShowSettled(vm);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(SnapshotPaths.File("flash-source-file.png"));

        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == Loc.Instance["Flash_FileNotice"]);

        window.Close();
    }

    [AvaloniaFact]
    public async Task CloseCommand_ClosesTheDialog_WithTheFlashedFlagFalse()
    {
        var parent = new Window();
        try
        {
            var vm = LocalVm();
            await vm.StartAsync();
            parent.Show();

            var dialog = new FlashWindow { DataContext = vm };
            var closed = dialog.ShowDialog<bool>(parent);
            vm.CloseCommand.Execute(null);

            Assert.False(await closed);
        }
        finally
        {
            parent.Close();
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    /// <summary>A local (no GitHub target) context on the fixture root: the
    /// same VM the <see cref="FlashViewModelTests"/> local case builds, so
    /// <see cref="FlashViewModel.StartAsync"/> lands in file mode.</summary>
    private static FlashViewModel LocalVm()
    {
        var root = ZmkConfigFixtures.Root("cosmos82");
        var keyboard = new FakeKeyboard();
        return new FlashViewModel(
            new FlashContext(root, root, null, null),
            new NullBuilds(),
            new FirmwareFlashHistory(Path.Combine(Path.GetTempPath(), $"lviz-flashwin-{Guid.NewGuid():N}.json")),
            keyboard,
            new FlashRun(keyboard, new Uf2DriveWatcher(new NoDrives())), new FakeDesktopInteraction());
    }

    /// <summary>Shows the window on the VM and lets the first layout and
    /// render pass finish, so frames and the visual tree see the shown step.</summary>
    private static FlashWindow ShowSettled(FlashViewModel vm)
    {
        var window = new FlashWindow { DataContext = vm };
        window.Show();
        Settle();
        return window;
    }

    /// <summary>Advances layout, render ticks and dispatcher jobs a few times
    /// (as in <see cref="ModuleSetupWindowTests"/>) so step panels and item
    /// containers are realized before capture or a visual-tree scan.</summary>
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        for (var i = 0; i < 4; i++)
        {
            Thread.Sleep(25);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class NoDrives : IUf2DriveSource
    {
        public IReadOnlyList<Uf2Drive> List() => [];
    }

    private sealed class FakeKeyboard : IKeyboardFlashAccess
    {
        public bool IsConnected => false;

        public KeyboardBus Bus => KeyboardBus.None;

        public int? BootloaderPeripheralSlots => null;

        public Task<ControlResult> EnterBootloaderAsync(byte targetHalf, CancellationToken ct = default) =>
            Task.FromResult(new ControlResult(ControlOutcome.Failed, "keyboard not connected"));

        public Task<string?> QueryConfigIdAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>File mode never touches the build service; any call is a bug.</summary>
    private sealed class NullBuilds : IFirmwareBuildService
    {
        public string? Login => throw new NotSupportedException();

        public bool IsSignedIn => throw new NotSupportedException();

        public bool IsLoginPersisted => throw new NotSupportedException();

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task EnsureRestoredAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GitHubDeviceCode> StartSignInAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SignOutAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FirmwareBuild> FindBuildAsync(
            string owner, string repo, string? headSha, string? branch, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FirmwareDownloadResult> DownloadAsync(
            string owner, string repo, string sha, long artifactId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PeripheralAdvice> AdvisePeripheralAsync(
            string owner, string repo, string? lastFlashedSha, string buildSha, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
