using LViz.App.Localization;
using LViz.App.ViewModels;
using LViz.Core.Layout;
using LViz.Core.Layout.Dtsi;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// <see cref="LayoutEditWindowViewModel"/>: the Save command's can-execute
/// following the editor's modified state (including through reset), saving a
/// single moved key back as normalised geometry under the unchanged id, name
/// and device name, refreshing vs. never creating the generated <c>.dtsi</c>,
/// the IO error path, cancel, and the title.
/// </summary>
public sealed class LayoutEditWindowViewModelTests : IDisposable
{
    private readonly string _layouts;

    public LayoutEditWindowViewModelTests()
    {
        _layouts = Path.Combine(Path.GetTempPath(), $"lviz-edit-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_layouts);
    }

    public void Dispose()
    {
        if (Directory.Exists(_layouts))
            Directory.Delete(_layouts, recursive: true);
    }

    // --- save can-execute ---

    [Fact]
    public void SaveCommand_FollowsEditorModified()
    {
        var vm = CreateVm();
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.Editor.SelectAll();
        vm.Editor.Nudge(1, 0, free: false);
        Assert.True(vm.SaveCommand.CanExecute(null));

        vm.Editor.ResetCommand.Execute(null);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    // --- save ---

    [Fact]
    public void Save_MovedKey0_WritesNormalisedFile_AndClosesWithIt()
    {
        var vm = CreateVm();
        // Key 0 alone: press its centre (50, 50), release, nudge 4 snap steps
        // right — it lands on X 100, exactly where key 1 already sits.
        vm.Editor.PointerPressed(50, 50, additive: false, tolerance: 1);
        vm.Editor.PointerReleased();
        vm.Editor.Nudge(4, 0, free: false);

        var calls = new List<UserLayoutFile?>();
        vm.CloseRequested += calls.Add;
        vm.SaveCommand.Execute(null);

        var path = Path.Combine(_layouts, "edit-me.layout.json");
        Assert.True(UserLayoutStore.TryLoadFile(path, out var loaded, out var reason), reason);
        Assert.Equal("edit-me", loaded!.Id);
        Assert.Equal("Edit Me", loaded.Name);
        Assert.Equal("EM", loaded.HidName);
        // Normalised: the leftmost key is now key 1 at X 100, so everything
        // shifted by −100.
        Assert.Equal([0, 0, 300], loaded.Keys.Select(k => k.X));

        var closed = Assert.Single(calls);
        Assert.NotNull(closed);
        Assert.Equal("edit-me", closed!.Id);
        Assert.Equal([0, 0, 300], closed.Keys.Select(k => k.X));
    }

    [Fact]
    public void Save_ExistingGeneratedDtsi_IsRewritten()
    {
        ZmkPhysicalLayoutWriter.WriteNextTo(Layout(), _layouts, createIfMissing: true);

        var vm = CreateVm();
        vm.Editor.PointerPressed(50, 50, additive: false, tolerance: 1);
        vm.Editor.PointerReleased();
        vm.Editor.Nudge(4, 0, free: false);
        vm.SaveCommand.Execute(null);

        var keyLines = File.ReadAllLines(Path.Combine(_layouts, "edit-me.dtsi"))
            .Where(line => line.Contains("key_physical_attrs"))
            .ToList();
        Assert.Equal(3, keyLines.Count);
        // Third key: X 300, Y 0 after normalisation.
        Assert.Contains(" 300 0 ", keyLines[2]);
    }

    [Fact]
    public void Save_WithoutExistingDtsi_CreatesNone()
    {
        var vm = CreateVm();
        vm.Editor.PointerPressed(50, 50, additive: false, tolerance: 1);
        vm.Editor.PointerReleased();
        vm.Editor.Nudge(4, 0, free: false);
        vm.SaveCommand.Execute(null);

        Assert.True(File.Exists(Path.Combine(_layouts, "edit-me.layout.json")));
        Assert.Empty(Directory.GetFiles(_layouts, "*.dtsi"));
    }

    [Fact]
    public void Save_LayoutsDirectoryIsAFile_ReportsErrorAndStaysOpen()
    {
        var blocked = Path.Combine(_layouts, "blocked");
        File.WriteAllText(blocked, "not a directory");
        var vm = new LayoutEditWindowViewModel(
            new LayoutEditContext(Layout(), ["A", "B", "C"], blocked));
        vm.Editor.SelectAll();
        vm.Editor.Nudge(1, 0, free: false);

        var calls = new List<UserLayoutFile?>();
        vm.CloseRequested += calls.Add;
        vm.SaveCommand.Execute(null);

        Assert.Empty(calls);
        Assert.Contains(
            Loc.Instance["Wizard_SaveFailedFormat"].Split('{')[0], vm.ErrorMessage!);
    }

    [Fact]
    public void Save_FailedDtsiRefresh_RestoresTheLayoutFile()
    {
        UserLayoutStore.Save(Layout(), _layouts);
        ZmkPhysicalLayoutWriter.WriteNextTo(Layout(), _layouts, createIfMissing: true);
        var jsonPath = Path.Combine(_layouts, "edit-me.layout.json");
        var before = File.ReadAllText(jsonPath);
        // A directory where the atomic write puts its temp file: the .dtsi
        // refresh fails after the .layout.json was already replaced.
        Directory.CreateDirectory(Path.Combine(_layouts, "edit-me.dtsi.tmp"));

        var vm = CreateVm();
        vm.Editor.SelectAll();
        vm.Editor.Nudge(0, 1, free: false);
        var calls = new List<UserLayoutFile?>();
        vm.CloseRequested += calls.Add;
        vm.SaveCommand.Execute(null);

        Assert.Empty(calls);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(before, File.ReadAllText(jsonPath));
    }

    // --- cancel / title ---

    [Fact]
    public void Cancel_ClosesWithNull_TitleNamesTheLayout()
    {
        var vm = CreateVm();
        var calls = new List<UserLayoutFile?>();
        vm.CloseRequested += calls.Add;

        vm.CancelCommand.Execute(null);

        var received = Assert.Single(calls);
        Assert.Null(received);
        Assert.Equal(Loc.Instance.Format("EditLayout_TitleFormat", "Edit Me"), vm.Title);
    }

    // --- helpers ---

    private LayoutEditWindowViewModel CreateVm() =>
        new(new LayoutEditContext(Layout(), ["A", "B", "C"], _layouts));

    /// <summary>The fixture board: three 100×100 unrotated keys at X 0, 100,
    /// 400, hands Left/Left/Right.</summary>
    private static UserLayoutFile Layout() => new(
        UserLayoutFile.CurrentSchemaVersion, "edit-me", "Edit Me", "EM",
        [
            new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Left),
            new UserLayoutKey(100, 0, 100, 100, 0, 0, 0, Hand.Left),
            new UserLayoutKey(400, 0, 100, 100, 0, 0, 0, Hand.Right),
        ]);
}
