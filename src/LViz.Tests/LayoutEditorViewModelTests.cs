using LViz.App.Localization;
using LViz.App.ViewModels;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// <see cref="LayoutEditorViewModel"/> interaction: hand application and the
/// derived state (viewport, rotation handle, modified flag), selection by
/// click / shift-click / box, the move, rotate and split-line drags with and
/// without snapping, neighbour snapping and its guides, nudging, mirroring one
/// half onto the other, reset, and the press precedence that decides which of
/// them a pointer press starts. Plus <see cref="EditorLabels"/>.
///
/// <para>The main fixture is the card's: three 1u keys in a row — K0 (0,0),
/// K1 (100,0), K2 (400,0) — labelled a, b, c, split line at 300, grab
/// tolerance 10 centi-u. The snapping tests use their own two-key row
/// (<see cref="SnapKeys"/>), the mirror tests a four-key board with two keys
/// per half (<see cref="MirrorKeys"/>) and an uneven 3/1 board
/// (<see cref="UnevenKeys"/>).</para>
/// </summary>
public sealed class LayoutEditorViewModelTests : IDisposable
{
    private const double Tolerance = 10;

    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
            File.Delete(path);
    }

    private static IReadOnlyList<UserLayoutKey> Keys() =>
    [
        new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Left),
        new UserLayoutKey(100, 0, 100, 100, 0, 0, 0, Hand.Left),
        new UserLayoutKey(400, 0, 100, 100, 0, 0, 0, Hand.Left),
    ];

    /// <summary><see cref="Keys"/> as the editor stores them: hands applied
    /// against the split line, so K2 belongs to the right hand.</summary>
    private static IReadOnlyList<UserLayoutKey> HandedKeys() =>
    [
        new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Left),
        new UserLayoutKey(100, 0, 100, 100, 0, 0, 0, Hand.Left),
        new UserLayoutKey(400, 0, 100, 100, 0, 0, 0, Hand.Right),
    ];

    private static IReadOnlyList<string> Labels() => ["a", "b", "c"];

    private static LayoutEditorViewModel CreateVm(int splitLineX = 300) =>
        new(Keys(), Labels(), splitLineX);

    /// <summary>Two 1u keys with a 2u gap — the pair the snapping tests close:
    /// K1 (300..400) dragged left can bring its edge onto K0's at 100.</summary>
    private static IReadOnlyList<UserLayoutKey> SnapKeys() =>
    [
        new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Left),
        new UserLayoutKey(300, 0, 100, 100, 0, 0, 0, Hand.Left),
    ];

    private static LayoutEditorViewModel CreateSnapVm() =>
        new(SnapKeys(), ["a", "b"], 600);

    /// <summary>A 1u row of four with the split at 300: two keys per half, so
    /// the mirror commands can pair them one-to-one.</summary>
    private static IReadOnlyList<UserLayoutKey> MirrorKeys() =>
    [
        new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Left),
        new UserLayoutKey(100, 0, 100, 100, 0, 0, 0, Hand.Left),
        new UserLayoutKey(400, 0, 100, 100, 0, 0, 0, Hand.Right),
        new UserLayoutKey(500, 0, 100, 100, 0, 0, 0, Hand.Right),
    ];

    private static LayoutEditorViewModel CreateMirrorVm() =>
        new(MirrorKeys(), ["a", "b", "c", "d"], 300);

    /// <summary>Three keys left of the split at 300 and one right of it — a
    /// board that cannot be mirrored.</summary>
    private static IReadOnlyList<UserLayoutKey> UnevenKeys() =>
    [
        new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Left),
        new UserLayoutKey(100, 0, 100, 100, 0, 0, 0, Hand.Left),
        new UserLayoutKey(200, 0, 100, 100, 0, 0, 0, Hand.Left),
        new UserLayoutKey(400, 0, 100, 100, 0, 0, 0, Hand.Right),
    ];

    private static LayoutEditorViewModel CreateUnevenVm() =>
        new(UnevenKeys(), ["a", "b", "c", "d"], 300);

    /// <summary>A point <paramref name="degrees"/> clockwise from straight
    /// above (50, 50) at radius 110 — the position a rotation drag has to reach
    /// to sweep exactly that angle from the handle press.</summary>
    private static (double X, double Y) PointAt(double degrees)
    {
        var radians = (-90 + degrees) * Math.PI / 180.0;
        return (50 + 110 * Math.Cos(radians), 50 + 110 * Math.Sin(radians));
    }

    // ---------------------------------------------------------------- ctor

    [Fact]
    public void Ctor_AppliesHandsAroundTheSplit()
    {
        var vm = CreateVm();

        Assert.Equal(Hand.Left, vm.Keys[0].Hand);
        Assert.Equal(Hand.Left, vm.Keys[1].Hand);
        Assert.Equal(Hand.Right, vm.Keys[2].Hand);
        Assert.Equal(300, vm.SplitLineX);
        Assert.Equal(Labels(), vm.Labels);
        Assert.False(vm.IsModified);
        Assert.Empty(vm.Selection);
        Assert.Equal(EditorDragKind.None, vm.CurrentDrag);
        Assert.Null(vm.SelectionBox);
        Assert.Null(vm.RotateHandle);
    }

    [Fact]
    public void Ctor_ViewBoundsPadsTheBoardAndTheSplitLine()
    {
        var vm = CreateVm();

        // The three keys span (0, 0)-(500, 100); padding 100 on every side.
        // The split line at 300 sits inside that span, so it widens nothing.
        Assert.Equal((-100.0, -100.0, 600.0, 200.0), vm.ViewBounds);
    }

    [Fact]
    public void Ctor_WidensToBoundsWhenTheSplitLineSitsOutsideTheKeys()
    {
        var vm = CreateVm(splitLineX: 900);

        Assert.Equal((-100.0, -100.0, 1000.0, 200.0), vm.ViewBounds);
        Assert.All(vm.Keys, key => Assert.Equal(Hand.Left, key.Hand));
    }

    [Fact]
    public void Ctor_RejectsAnEmptyKeyList() =>
        Assert.Throws<ArgumentException>(
            () => new LayoutEditorViewModel([], Labels(), 300));

    [Fact]
    public void Ctor_RejectsLabelCountMismatch() =>
        Assert.Throws<ArgumentException>(
            () => new LayoutEditorViewModel(Keys(), ["a"], 300));

    // ------------------------------------------------------- click select

    [Fact]
    public void Press_OnKey_SelectsItAndStartsMove()
    {
        var vm = CreateVm();

        vm.PointerPressed(50, 50, additive: false, Tolerance);

        Assert.Equal([0], vm.Selection);
        Assert.Equal(EditorDragKind.Move, vm.CurrentDrag);
    }

    [Fact]
    public void Press_OnSecondKey_ReplacesSelectionAndDragsOnlyIt()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.PointerPressed(150, 50, additive: false, Tolerance);
        vm.PointerMoved(175, 50, free: false);

        Assert.Equal([1], vm.Selection);
        Assert.Equal(125, vm.Keys[1].X);
        Assert.Equal(0, vm.Keys[0].X);
    }

    [Fact]
    public void Press_OnEmptySpace_ClearsTheSelection()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.PointerPressed(250, 150, additive: false, Tolerance);

        Assert.Empty(vm.Selection);
        Assert.Equal(EditorDragKind.Box, vm.CurrentDrag);
    }

    [Fact]
    public void Press_AdditiveOnEmptySpace_KeepsTheSelection()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.PointerPressed(250, 150, additive: true, Tolerance);

        Assert.Equal([0], vm.Selection);
        Assert.Equal(EditorDragKind.Box, vm.CurrentDrag);
    }

    [Fact]
    public void Press_DuringActiveDrag_IsIgnored()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);

        vm.PointerPressed(150, 50, additive: false, Tolerance);
        vm.PointerPressed(300, 50, additive: false, Tolerance);
        vm.PointerPressed(250, 150, additive: false, Tolerance);

        Assert.Equal([0], vm.Selection);
        Assert.Equal(EditorDragKind.Move, vm.CurrentDrag);
        Assert.Equal(0, vm.Keys[0].X);
    }

    [Fact]
    public void Press_PrefersAKeyOverTheSplitLine()
    {
        // Split at 110 is both a hit on K1 (100..200) and within tolerance of
        // the line; the key wins.
        var vm = CreateVm(splitLineX: 110);

        vm.PointerPressed(110, 50, additive: false, Tolerance);

        Assert.Equal(EditorDragKind.Move, vm.CurrentDrag);
        Assert.Equal([1], vm.Selection);
    }

    // ------------------------------------------------------- shift-click

    [Fact]
    public void ShiftPress_OnUnselectedKey_AddsItToTheSelection()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.PointerPressed(150, 50, additive: true, Tolerance);

        Assert.Equal([0, 1], vm.Selection.OrderBy(i => i));
        Assert.Equal(EditorDragKind.Move, vm.CurrentDrag);
    }

    [Fact]
    public void ShiftPress_OnSelectedKey_RemovesItWithoutStartingADrag()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();
        vm.PointerPressed(150, 50, additive: true, Tolerance);
        vm.PointerReleased();

        vm.PointerPressed(150, 50, additive: true, Tolerance);

        Assert.Equal([0], vm.Selection);
        Assert.Equal(EditorDragKind.None, vm.CurrentDrag);
    }

    [Fact]
    public void ShiftPress_TouchesNothingElse()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.PointerPressed(150, 50, additive: true, Tolerance);
        vm.PointerReleased();

        Assert.Equal([0, 1], vm.Selection.OrderBy(i => i));
        Assert.False(vm.IsModified);
        Assert.Equal(0, vm.Keys[0].X);
        Assert.Equal(100, vm.Keys[1].X);
        Assert.Equal(400, vm.Keys[2].X);
    }

    [Fact]
    public void SelectAll_AndClearSelection()
    {
        var vm = CreateVm();

        vm.SelectAll();
        Assert.Equal([0, 1, 2], vm.Selection.OrderBy(i => i));

        vm.ClearSelection();
        Assert.Empty(vm.Selection);
    }

    // ---------------------------------------------------------------- move

    [Fact]
    public void Move_SnapsToQuarterU()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);

        // Leftwards: K1 touches K0 on the right, so a step that way would
        // overlap it and be pushed back out.
        vm.PointerMoved(13, 50, free: false);

        Assert.Equal(-25, vm.Keys[0].X);
    }

    [Fact]
    public void Move_DroppedOntoANeighbour_IsPushedFlush_UnlessFree()
    {
        // K1 (100..200) dragged 60 left lands on K0: pushed back flush to
        // K0's right edge. With Option held it may overlap.
        var vm = CreateVm();
        vm.PointerPressed(150, 50, additive: false, Tolerance);
        vm.PointerMoved(90, 50, free: false);
        Assert.Equal(100, vm.Keys[1].X);
        Assert.Equal(100.0, vm.SnapGuideX);

        vm.PointerMoved(90, 50, free: true);
        Assert.Equal(40, vm.Keys[1].X);
    }

    [Fact]
    public void Move_Free_MovesSingleCentiU()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);

        vm.PointerMoved(87, 50, free: true);

        Assert.Equal(37, vm.Keys[0].X);
    }

    [Fact]
    public void Move_RecomputesFromTheSnapshot_NotTheLastMove()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);

        vm.PointerMoved(13, 50, free: false);
        var afterFirst = vm.Keys[0].X;
        vm.PointerMoved(13, 50, free: false);

        Assert.Equal(-25, afterFirst);
        Assert.Equal(afterFirst, vm.Keys[0].X);
    }

    [Fact]
    public void Move_LeavesUnselectedKeysAlone()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);

        vm.PointerMoved(0, 50, free: false);

        Assert.Equal(-50, vm.Keys[0].X);
        Assert.Equal(100, vm.Keys[1].X);
        Assert.Equal(400, vm.Keys[2].X);
    }

    [Fact]
    public void Move_WithBothKeysSelected_MovesBoth()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();
        vm.PointerPressed(150, 50, additive: true, Tolerance);

        // The press point of the drag is the second press, so the offset is
        // measured from (150, 50).
        vm.PointerMoved(200, 50, free: false);

        Assert.Equal(50, vm.Keys[0].X);
        Assert.Equal(150, vm.Keys[1].X);
        Assert.Equal(400, vm.Keys[2].X);
    }

    [Fact]
    public void Release_EndsTheDragAndMarksModified()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerMoved(13, 50, free: false);

        vm.PointerReleased();

        Assert.Equal(EditorDragKind.None, vm.CurrentDrag);
        Assert.True(vm.IsModified);
        Assert.Null(vm.SelectionBox);
    }

    [Fact]
    public void MovedWithoutADrag_DoesNothing()
    {
        var vm = CreateVm();

        vm.PointerMoved(500, 500, free: false);
        vm.PointerReleased();

        Assert.Equal(HandedKeys(), vm.Keys);
        Assert.Equal((-100.0, -100.0, 600.0, 200.0), vm.ViewBounds);
        Assert.False(vm.IsModified);
    }

    // ------------------------------------------------------- view bounds

    [Fact]
    public void ViewBounds_StaysFrozenDuringMoveAndRefitsOnRelease()
    {
        var vm = CreateVm();
        var before = vm.ViewBounds;

        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerMoved(50, 150, free: false);
        Assert.Equal(before, vm.ViewBounds);

        vm.PointerReleased();
        Assert.NotEqual(before, vm.ViewBounds);
        // K0 dropped a row (sideways K1 is in the way), so the board's bottom
        // edge is 200 now.
        Assert.Equal((-100.0, -100.0, 600.0, 300.0), vm.ViewBounds);
    }

    [Fact]
    public void ViewBounds_RefitsAfterNudge()
    {
        var vm = CreateVm();
        vm.SelectAll();

        vm.Nudge(4, 0, free: false);

        // Every key moved 100 right: the board now spans 100..600.
        Assert.Equal((0.0, -100.0, 700.0, 200.0), vm.ViewBounds);
    }

    // ------------------------------------------------ neighbour snapping

    [Fact]
    public void Move_SnapsOntoANeighbourEdge_AndReportsTheGuide()
    {
        var vm = CreateSnapVm();
        vm.PointerPressed(350, 50, additive: false, Tolerance);
        Assert.Equal(EditorDragKind.Move, vm.CurrentDrag);

        vm.PointerMoved(153, 50, free: false);

        // K1's left edge would land at 103 — 3 units past K0's right edge at
        // 100, well inside the 10-unit press tolerance, so the drag closes the
        // gap and says which edge it closed to.
        Assert.Equal(100, vm.Keys[1].X);
        Assert.Equal(100.0, vm.SnapGuideX!.Value);
        // Nothing moved vertically and the row was already flush, so the Y axis
        // "snaps" onto K0's top edge too.
        Assert.Equal(0.0, vm.SnapGuideY!.Value);
        Assert.Equal(0, vm.Keys[1].Y);
        Assert.Equal(0, vm.Keys[0].X);
    }

    [Fact]
    public void Move_Free_SkipsNeighbourSnappingAndGuides()
    {
        var vm = CreateSnapVm();
        vm.PointerPressed(350, 50, additive: false, Tolerance);
        vm.PointerMoved(153, 50, free: false);
        Assert.Equal(100.0, vm.SnapGuideX!.Value);

        vm.PointerMoved(153, 50, free: true);

        // The same pointer position as the snapped move above: 103 stays 103,
        // and the guide from that move goes away.
        Assert.Equal(103, vm.Keys[1].X);
        Assert.Null(vm.SnapGuideX);
        Assert.Null(vm.SnapGuideY);
    }

    [Fact]
    public void Move_WithNoNeighbourInTolerance_FallsBackToTheGrid()
    {
        var vm = CreateSnapVm();
        vm.PointerPressed(350, 50, additive: false, Tolerance);
        vm.PointerMoved(153, 50, free: false);
        Assert.Equal(100.0, vm.SnapGuideX!.Value);

        vm.PointerMoved(200, 50, free: false);

        // K1's left edge at 150 is 50 units clear of K0 — out of tolerance, so
        // it lands on the 0.25u grid and the guide from the earlier move goes.
        Assert.Equal(150, vm.Keys[1].X);
        Assert.Null(vm.SnapGuideX);
    }

    [Fact]
    public void Release_ClearsTheSnapGuides()
    {
        var vm = CreateSnapVm();
        vm.PointerPressed(350, 50, additive: false, Tolerance);
        vm.PointerMoved(153, 50, free: false);
        Assert.NotNull(vm.SnapGuideX);

        vm.PointerReleased();

        Assert.Null(vm.SnapGuideX);
        Assert.Null(vm.SnapGuideY);
    }

    [Fact]
    public void Reset_ClearsTheSnapGuides()
    {
        var vm = CreateSnapVm();
        vm.PointerPressed(350, 50, additive: false, Tolerance);
        vm.PointerMoved(153, 50, free: false);

        vm.ResetCommand.Execute(null);

        Assert.Null(vm.SnapGuideX);
        Assert.Null(vm.SnapGuideY);
        Assert.Equal(300, vm.Keys[1].X);
    }

    [Fact]
    public void Nudge_StaysOnTheGrid_EvenNextToANeighbour()
    {
        // Key 1 sits 5 units past a flush quarter-u spot: a move drag would
        // close that gap onto K0's right edge, the arrow keys don't.
        var vm = new LayoutEditorViewModel(
            [
                new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Left),
                new UserLayoutKey(130, 0, 100, 100, 0, 0, 0, Hand.Left),
            ],
            ["a", "b"], 600);
        vm.PointerPressed(180, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.Nudge(-1, 0, free: false);

        Assert.Equal(105, vm.Keys[1].X);
        Assert.Null(vm.SnapGuideX);
    }

    // ------------------------------------------------------------- rotate

    [Fact]
    public void RotateHandle_SitsAboveTheSelectionAndFollowsIt()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);

        Assert.NotNull(vm.RotateHandle);
        Assert.Equal((50.0, -60.0), vm.RotateHandle!.Value);

        vm.PointerReleased();
        vm.PointerPressed(150, 50, additive: false, Tolerance);
        Assert.Equal((150.0, -60.0), vm.RotateHandle!.Value);
    }

    [Fact]
    public void RotateHandle_IsCentredOverTheWholeSelection()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.PointerPressed(150, 50, additive: true, Tolerance);

        Assert.NotNull(vm.RotateHandle);
        Assert.Equal((100.0, -60.0), vm.RotateHandle!.Value);
    }

    [Fact]
    public void RotateHandle_ClearedWithTheSelection()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.ClearSelection();

        Assert.Null(vm.RotateHandle);
    }

    [Fact]
    public void Rotate_DragStartsFromTheHandle()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.PointerPressed(50, -60, additive: false, Tolerance);

        Assert.Equal(EditorDragKind.Rotate, vm.CurrentDrag);
    }

    [Fact]
    public void Rotate_SnapsToFiveDegrees()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.PointerPressed(50, -60, additive: false, Tolerance);
        vm.PointerMoved(160, 50, free: false);

        var key = vm.Keys[0];
        Assert.Equal(9000, key.R);
        Assert.Equal(0, key.X);
        Assert.Equal(0, key.Y);
        Assert.Equal(50, key.Rx);
        Assert.Equal(50, key.Ry);
    }

    [Fact]
    public void Rotate_SmallAngle_SnapsDownToFiveDegrees()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();
        vm.PointerPressed(50, -60, additive: false, Tolerance);

        vm.PointerMoved(PointAt(7).X, PointAt(7).Y, free: false);

        Assert.Equal(500, vm.Keys[0].R);
    }

    [Fact]
    public void Rotate_Free_UsesWholeDegrees()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();
        vm.PointerPressed(50, -60, additive: false, Tolerance);

        vm.PointerMoved(PointAt(7).X, PointAt(7).Y, free: true);

        Assert.Equal(700, vm.Keys[0].R);
    }

    [Fact]
    public void Rotate_LeavesUnselectedKeysAlone()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();
        vm.PointerPressed(50, -60, additive: false, Tolerance);

        vm.PointerMoved(160, 50, free: false);

        Assert.Equal(0, vm.Keys[1].R);
        Assert.Equal(100, vm.Keys[1].X);
        Assert.Equal(400, vm.Keys[2].X);
    }

    [Fact]
    public void Rotate_KeepsTheKeysHandOnItsSideOfTheSplit()
    {
        var vm = CreateVm();
        vm.SelectAll();

        // The whole board swung 180° about its centre (250, 50): the outer
        // keys swap sides, so their hands follow their new centres.
        vm.PointerPressed(250, -60, additive: false, Tolerance);
        Assert.Equal(EditorDragKind.Rotate, vm.CurrentDrag);
        vm.PointerMoved(250, 160, free: true);

        Assert.Equal(18000, vm.Keys[0].R);
        Assert.Equal(Hand.Right, vm.Keys[0].Hand);
        Assert.Equal(Hand.Left, vm.Keys[2].Hand);
    }

    // ---------------------------------------------------------- box select

    [Fact]
    public void BoxDrag_SelectsByCentresAndSetsTheBox()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.PointerPressed(250, 150, additive: false, Tolerance);
        vm.PointerMoved(-10, -10, free: false);

        Assert.Equal((250.0, 150.0, -10.0, -10.0), vm.SelectionBox);
        Assert.Equal([0, 1], vm.Selection.OrderBy(i => i));
        Assert.Equal(EditorDragKind.Box, vm.CurrentDrag);
    }

    [Fact]
    public void BoxDrag_ReleaseClearsTheBox()
    {
        var vm = CreateVm();
        vm.PointerPressed(250, 150, additive: false, Tolerance);
        vm.PointerMoved(-10, -10, free: false);

        vm.PointerReleased();

        Assert.Null(vm.SelectionBox);
        Assert.Equal(EditorDragKind.None, vm.CurrentDrag);
    }

    [Fact]
    public void BoxDrag_Additive_UnionsWithTheKeysSelectedBeforeThePress()
    {
        var vm = CreateVm();
        vm.PointerPressed(450, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.PointerPressed(250, 150, additive: true, Tolerance);
        vm.PointerMoved(-10, -10, free: false);

        Assert.Equal([0, 1, 2], vm.Selection.OrderBy(i => i));
    }

    // ------------------------------------------------------------ split

    [Fact]
    public void SplitLine_PressStartsTheDragAndMoveRehands()
    {
        var vm = CreateVm();

        vm.PointerPressed(300, 50, additive: false, Tolerance);
        Assert.Equal(EditorDragKind.SplitLine, vm.CurrentDrag);

        vm.PointerMoved(60, 50, free: false);

        Assert.Equal(50, vm.SplitLineX);
        Assert.All(vm.Keys, key => Assert.Equal(Hand.Right, key.Hand));
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void SplitLine_Free_MovesSingleCentiU()
    {
        var vm = CreateVm();
        vm.PointerPressed(300, 50, additive: false, Tolerance);

        vm.PointerMoved(63, 50, free: true);

        Assert.Equal(63, vm.SplitLineX);
    }

    [Fact]
    public void SplitLine_DoesNotMoveTheKeys()
    {
        var vm = CreateVm();
        vm.PointerPressed(300, 50, additive: false, Tolerance);

        vm.PointerMoved(60, 50, free: false);

        Assert.Equal(0, vm.Keys[0].X);
        Assert.Equal(100, vm.Keys[1].X);
        Assert.Equal(400, vm.Keys[2].X);
    }

    [Fact]
    public void DraggingAKeyAcrossTheSplitFlipsItsHand()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);

        vm.PointerMoved(450, 50, free: false);
        Assert.Equal(Hand.Right, vm.Keys[0].Hand);

        vm.PointerMoved(150, 50, free: false);
        Assert.Equal(Hand.Left, vm.Keys[0].Hand);
    }

    // ------------------------------------------------------------ mirror

    [Fact]
    public void MirrorLeftToRight_CopiesThePlacementAndClearsTheSelection()
    {
        var vm = CreateMirrorVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();
        vm.Nudge(0, 2, free: false);
        Assert.Equal(50, vm.Keys[0].Y);
        Assert.True(vm.CanMirror);

        vm.MirrorLeftToRightCommand.Execute(null);

        // K0 (0,50) mirrors to centre (550,100) → key 3; K1 (100,0) mirrors to
        // (450,50) → key 2, so the right half picks up the drop without moving
        // sideways.
        Assert.Equal(50, vm.Keys[3].Y);
        Assert.Equal(500, vm.Keys[3].X);
        Assert.Equal(0, vm.Keys[2].Y);
        Assert.Equal(400, vm.Keys[2].X);
        // The source half keeps its own placement.
        Assert.Equal(50, vm.Keys[0].Y);
        Assert.Equal(0, vm.Keys[1].Y);
        Assert.Equal(2, vm.LeftKeyCount);
        Assert.Equal(2, vm.RightKeyCount);
        Assert.Empty(vm.Selection);
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void MirrorRightToLeft_CopiesThePlacementTheOtherWay()
    {
        var vm = CreateMirrorVm();
        vm.PointerPressed(450, 50, additive: false, Tolerance);
        vm.PointerReleased();
        vm.Nudge(0, 1, free: false);
        Assert.Equal(25, vm.Keys[2].Y);

        vm.MirrorRightToLeftCommand.Execute(null);

        // K2 (400,25) mirrors to centre (150,75) → key 1.
        Assert.Equal(25, vm.Keys[1].Y);
        Assert.Equal(100, vm.Keys[1].X);
        Assert.Equal(0, vm.Keys[0].Y);
        Assert.Equal(0, vm.Keys[0].X);
        Assert.Empty(vm.Selection);
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void Mirror_IgnoresAnOffCentreSplitLine()
    {
        // The split at 300 only decides the hands; the halves' middle is 500,
        // so mirroring keeps the right half at 800/900 instead of pulling it
        // towards the line.
        var vm = new LayoutEditorViewModel(
        [
            new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Left),
            new UserLayoutKey(100, 0, 100, 100, 0, 0, 0, Hand.Left),
            new UserLayoutKey(800, 0, 100, 100, 0, 0, 0, Hand.Right),
            new UserLayoutKey(900, 0, 100, 100, 0, 0, 0, Hand.Right),
        ], ["a", "b", "c", "d"], 300);
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();
        vm.Nudge(0, 2, free: false);

        vm.MirrorLeftToRightCommand.Execute(null);

        Assert.Equal((900, 50), (vm.Keys[3].X, vm.Keys[3].Y));
        Assert.Equal((800, 0), (vm.Keys[2].X, vm.Keys[2].Y));
    }

    [Fact]
    public void Unibody_HidesMirroring_DetectedOrTold()
    {
        // Halves that touch (centres 1u apart across the split) are one board.
        IReadOnlyList<UserLayoutKey> touching =
        [
            new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Left),
            new UserLayoutKey(100, 0, 100, 100, 0, 0, 0, Hand.Left),
            new UserLayoutKey(200, 0, 100, 100, 0, 0, 0, Hand.Right),
            new UserLayoutKey(300, 0, 100, 100, 0, 0, 0, Hand.Right),
        ];
        var detected = new LayoutEditorViewModel(touching, ["a", "b", "c", "d"], 200);
        // A gap is no proof when the host knows better.
        var told = new LayoutEditorViewModel(MirrorKeys(), ["a", "b", "c", "d"], 300, isSplit: false);

        foreach (var vm in new[] { detected, told })
        {
            Assert.False(vm.IsSplit);
            Assert.False(vm.CanMirror);
            Assert.False(vm.MirrorLeftToRightCommand.CanExecute(null));
            Assert.Null(vm.MirrorHint);
        }
        Assert.True(CreateMirrorVm().IsSplit);
    }

    [Fact]
    public void UnevenHalves_DisableBothMirrorCommands_AndSayWhy()
    {
        var vm = CreateUnevenVm();

        Assert.Equal(3, vm.LeftKeyCount);
        Assert.Equal(1, vm.RightKeyCount);
        Assert.False(vm.CanMirror);
        Assert.False(vm.MirrorLeftToRightCommand.CanExecute(null));
        Assert.False(vm.MirrorRightToLeftCommand.CanExecute(null));
        Assert.Equal(
            Loc.Instance.Format("Editor_MirrorUnavailableFormat", 3, 1), vm.MirrorHint);
        // Culture-proof extras: the key really resolves (a missing one would
        // read "[Editor_MirrorUnavailableFormat]"), and the counts come out
        // left-then-right — a Loc-vs-Loc comparison can't tell a swapped pair
        // apart.
        Assert.DoesNotContain("[Editor_", vm.MirrorHint);
        Assert.Equal(
            ['3', '1'], vm.MirrorHint!.Where(char.IsDigit).ToList());
    }

    [Fact]
    public void UnevenHalves_RunningAMirrorCommandChangesNothing()
    {
        var vm = CreateUnevenVm();

        // Execute ignores CanExecute, so the command itself has to survive a
        // half pair that cannot be matched one-to-one.
        vm.MirrorLeftToRightCommand.Execute(null);
        vm.MirrorRightToLeftCommand.Execute(null);

        Assert.Equal(UnevenKeys(), vm.Keys);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void MirrorCommands_StayDisabledWhenAHalfIsEmpty()
    {
        // Split at 900 puts every key left of the line, at -100 every key on it
        // or right of it. An empty half has no partner for anything, and the
        // 0 == 0 comparison would otherwise read as a match.
        var allLeft = CreateVm(splitLineX: 900);
        Assert.Equal(3, allLeft.LeftKeyCount);
        Assert.Equal(0, allLeft.RightKeyCount);
        Assert.False(allLeft.CanMirror);
        Assert.False(allLeft.MirrorLeftToRightCommand.CanExecute(null));

        var allRight = CreateVm(splitLineX: -100);
        Assert.Equal(0, allRight.LeftKeyCount);
        Assert.Equal(3, allRight.RightKeyCount);
        Assert.False(allRight.CanMirror);
        Assert.False(allRight.MirrorLeftToRightCommand.CanExecute(null));
    }

    [Fact]
    public void SplitLineDragToEvenHalves_EnablesMirroring_AndRaisesTheChange()
    {
        var vm = CreateUnevenVm();
        var raised = new HashSet<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                raised.Add(e.PropertyName);
        };
        var requeries = 0;
        vm.MirrorLeftToRightCommand.CanExecuteChanged += (_, _) => requeries++;

        // (300, 150) is below the keys, which end at Y 100, so the press grabs
        // the line rather than key 2.
        vm.PointerPressed(300, 150, additive: false, Tolerance);
        Assert.Equal(EditorDragKind.SplitLine, vm.CurrentDrag);

        vm.PointerMoved(250, 150, free: false);
        vm.PointerReleased();

        // Key 2's centre is now exactly on the line, which counts as right-hand:
        // two keys per half.
        Assert.Equal(250, vm.SplitLineX);
        Assert.True(vm.CanMirror);
        Assert.Null(vm.MirrorHint);
        Assert.True(vm.MirrorLeftToRightCommand.CanExecute(null));
        Assert.Contains("CanMirror", raised);
        Assert.Contains("MirrorHint", raised);
        Assert.Contains("LeftKeyCount", raised);
        Assert.Contains("RightKeyCount", raised);
        // Without this the bound button would stay greyed out on a board that
        // can be mirrored now.
        Assert.True(requeries > 0);
    }

    [Fact]
    public void Mirroring_IsUnavailableDuringADragAndBackAfterwards()
    {
        var vm = CreateMirrorVm();
        var raised = new HashSet<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                raised.Add(e.PropertyName);
        };
        Assert.True(vm.CanMirror);

        vm.PointerPressed(50, 50, additive: false, Tolerance);

        Assert.Equal(EditorDragKind.Move, vm.CurrentDrag);
        Assert.False(vm.CanMirror);
        Assert.False(vm.MirrorRightToLeftCommand.CanExecute(null));
        // No reason text mid-drag — the counts only look wrong until the
        // pointer comes up.
        Assert.Null(vm.MirrorHint);

        vm.PointerReleased();

        Assert.True(vm.CanMirror);
        Assert.Null(vm.MirrorHint);
        Assert.Contains("CanMirror", raised);
    }

    // ------------------------------------------------------------- nudge

    [Fact]
    public void Nudge_StepsByQuarterU()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();

        vm.Nudge(1, 0, free: false);

        Assert.Equal(25, vm.Keys[0].X);
        Assert.Equal(0, vm.Keys[0].Y);
    }

    [Fact]
    public void Nudge_Free_StepsByCentiU()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerReleased();
        vm.Nudge(1, 0, free: false);

        vm.Nudge(0, -1, free: true);

        Assert.Equal(25, vm.Keys[0].X);
        Assert.Equal(-1, vm.Keys[0].Y);
    }

    [Fact]
    public void Nudge_WithoutASelection_ChangesNothing()
    {
        var vm = CreateVm();

        vm.Nudge(3, 3, free: false);

        Assert.Equal(HandedKeys(), vm.Keys);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void Nudge_MovesEverySelectedKey()
    {
        var vm = CreateVm();
        vm.SelectAll();

        vm.Nudge(0, 2, free: false);

        Assert.All(vm.Keys, key => Assert.Equal(50, key.Y));
    }

    // ------------------------------------------------------------- reset

    [Fact]
    public void Reset_RestoresTheStartState()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerMoved(300, 200, free: false);
        vm.PointerReleased();
        vm.PointerPressed(300, 50, additive: false, Tolerance);
        vm.PointerMoved(60, 50, free: false);
        vm.PointerReleased();
        Assert.True(vm.IsModified);

        vm.ResetCommand.Execute(null);

        Assert.Equal(HandedKeys(), vm.Keys);
        Assert.Equal(300, vm.SplitLineX);
        Assert.Empty(vm.Selection);
        Assert.False(vm.IsModified);
        Assert.Null(vm.SelectionBox);
        Assert.Equal(EditorDragKind.None, vm.CurrentDrag);
    }

    [Fact]
    public void Reset_CancelsAnActiveDrag()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerMoved(200, 50, free: false);

        vm.ResetCommand.Execute(null);

        Assert.Equal(EditorDragKind.None, vm.CurrentDrag);
        Assert.Equal(0, vm.Keys[0].X);
        Assert.Null(vm.RotateHandle);
    }

    // ----------------------------------------------------- notifications

    [Fact]
    public void StateChanges_RaisePropertyChanged()
    {
        var vm = CreateVm();
        var raised = new HashSet<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                raised.Add(e.PropertyName);
        };

        vm.PointerPressed(50, 50, additive: false, Tolerance);
        vm.PointerMoved(87, 50, free: false);
        vm.PointerMoved(250, 150, free: false);
        vm.PointerReleased();

        // A box press is the only path that draws a selection box, and it has
        // to land clear of the key the move just parked at (200, 100).
        vm.PointerPressed(600, 400, additive: false, Tolerance);
        vm.PointerMoved(700, 500, free: false);
        vm.PointerReleased();

        Assert.Contains("Keys", raised);
        Assert.Contains("Selection", raised);
        Assert.Contains("CurrentDrag", raised);
        Assert.Contains("SelectionBox", raised);
        Assert.Contains("RotateHandle", raised);
        Assert.Contains("ViewBounds", raised);
        Assert.Contains("IsModified", raised);
    }

    [Fact]
    public void Selection_IsANewSetInstanceEveryChange()
    {
        var vm = CreateVm();
        var first = vm.Selection;

        vm.ClearSelection();
        var second = vm.Selection;

        Assert.Empty(second);
        Assert.NotSame(first, second);
    }

    // ------------------------------------------------------ EditorLabels

    [Fact]
    public void EditorLabels_WithoutAKeymap_AreIndices() =>
        Assert.Equal(["0", "1", "2"], EditorLabels.For(null, 3, "p"));

    [Fact]
    public void EditorLabels_WithoutAKeymap_AndNoKeys_AreEmpty() =>
        Assert.Empty(EditorLabels.For(null, 0, "p"));

    [Fact]
    public void EditorLabels_UseTheBaseLayerBindingAndFallBackToTheIndex()
    {
        Assert.Equal(["A", "A", "2"], EditorLabels.For(KeymapWithBindings(2), 3, "p"));
    }

    [Fact]
    public void EditorLabels_StopAtKeyCount()
    {
        Assert.Equal(["A"], EditorLabels.For(KeymapWithBindings(2), 1, "p"));
    }

    /// <summary>Round-trips a two-layer-free base keymap through the real
    /// parser, same shape as <c>LayoutWizardViewModelTests.KeymapText</c>.</summary>
    private LoadedKeymap KeymapWithBindings(int bindings)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lviz-editor-{Guid.NewGuid():N}.keymap");
        _tempFiles.Add(path);
        var cells = string.Join(" ", Enumerable.Repeat("&kp A", bindings));
        File.WriteAllText(
            path,
            $"/ {{ keymap {{ compatible = \"zmk,keymap\"; base {{ bindings = <{cells}>; }}; }}; }};");
        return new LoadedKeymap(ZmkKeymapLoader.Load(path, "editor-tests"));
    }
}
