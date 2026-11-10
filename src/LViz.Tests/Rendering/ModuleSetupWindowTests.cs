using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LViz.App.Localization;
using LViz.App.ViewModels;
using LViz.App.Views;
using LViz.Core.Persistence;
using LViz.Tests.ZmkConfig;
using Xunit;

namespace LViz.Tests.Rendering;

/// <summary>
/// The module setup wizard window over a local temp copy of the cosmos82
/// fixture (the same set-up as <see cref="ModuleSetupViewModelTests"/>):
/// every step renders real pixels, the snapshots go to
/// <see cref="SnapshotPaths"/> for review, the Check step shows the fact
/// lines, Review shows the plan's diff lines, and the Close command closes
/// the modal dialog with the VM's saved flag — false when nothing was saved.
/// </summary>
public class ModuleSetupWindowTests
{
    private static readonly Guid FixedGuid = Guid.Parse("123e4567-e89b-12d3-a456-426614174000");

    private static ModuleSetupViewModel CosmosVm(string root) =>
        new(
            new ModuleSetupContext(root, null, () => false),
            gitHub: null,
            new KeymapBackupStore(Path.Combine(Path.GetTempPath(), $"lviz-modsetup-win-{Guid.NewGuid():N}")),
            () => FixedGuid);

    [AvaloniaFact]
    public async Task CheckStep_Renders_AndShowsTheFactLines()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            var vm = CosmosVm(root);
            await vm.StartAsync();
            var window = ShowSettled(vm);

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(SnapshotPaths.File("module-setup-check.png"));

            var firstFact = vm.FactLines[0];
            Assert.Contains(
                window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == firstFact);

            window.Close();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Next_WalksToChoose_ThenReviewShowsTheDiff()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            var vm = CosmosVm(root);
            await vm.StartAsync();
            var window = ShowSettled(vm);

            vm.NextCommand.Execute(null); // -> Choose
            Settle();
            var choose = window.CaptureRenderedFrame();
            Assert.NotNull(choose);
            choose!.Save(SnapshotPaths.File("module-setup-choose.png"));
            var chooseHeader = Loc.Instance["ModuleSetup_ChooseHeader"];
            Assert.Contains(
                window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == chooseHeader);

            vm.NextCommand.Execute(null); // -> Review
            Settle();
            var review = window.CaptureRenderedFrame();
            Assert.NotNull(review);
            review!.Save(SnapshotPaths.File("module-setup-review.png"));
            Assert.Contains(
                window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "+ CONFIG_RAW_HID=y");

            window.Close();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task CloseCommand_ClosesTheDialog_WithTheSavedFlagFalse()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        var parent = new Window();
        try
        {
            var vm = CosmosVm(root);
            await vm.StartAsync();
            parent.Show();

            var dialog = new ModuleSetupWindow { DataContext = vm };
            var closed = dialog.ShowDialog<bool>(parent);
            vm.CloseCommand.Execute(null);

            Assert.False(await closed);
        }
        finally
        {
            parent.Close();
            Directory.Delete(root, true);
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    /// <summary>Shows the window on the VM and lets the first layout and
    /// render pass finish, so frames and the visual tree see the shown step.</summary>
    private static ModuleSetupWindow ShowSettled(ModuleSetupViewModel vm)
    {
        var window = new ModuleSetupWindow { DataContext = vm };
        window.Show();
        Settle();
        return window;
    }

    /// <summary>Advances layout, render ticks and dispatcher jobs a few times
    /// (as in the ring snapshot tests) so step panels and item containers are
    /// realized before capture or a visual-tree scan.</summary>
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
}
