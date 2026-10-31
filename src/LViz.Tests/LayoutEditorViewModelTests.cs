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
/// without snapping, nudging, reset, and the press precedence that decides
/// which of them a pointer press starts. Plus <see cref="EditorLabels"/>.
///
/// <para>The fixture is the card's: three 1u keys in a row — K0 (0,0),
/// K1 (100,0), K2 (400,0) — labelled a, b, c, split line at 300, grab
/// tolerance 10 centi-u.</para>
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

        vm.PointerMoved(87, 50, free: false);

        Assert.Equal(25, vm.Keys[0].X);
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

        vm.PointerMoved(87, 50, free: false);
        var afterFirst = vm.Keys[0].X;
        vm.PointerMoved(87, 50, free: false);

        Assert.Equal(25, afterFirst);
        Assert.Equal(afterFirst, vm.Keys[0].X);
    }

    [Fact]
    public void Move_LeavesUnselectedKeysAlone()
    {
        var vm = CreateVm();
        vm.PointerPressed(50, 50, additive: false, Tolerance);

        vm.PointerMoved(100, 50, free: false);

        Assert.Equal(50, vm.Keys[0].X);
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
        vm.PointerMoved(87, 50, free: false);

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
        vm.PointerMoved(150, 50, free: false);
        Assert.Equal(before, vm.ViewBounds);

        vm.PointerReleased();
        Assert.NotEqual(before, vm.ViewBounds);
        // K0 slid to X = 100, so the board's left edge is 100; the split line
        // at 300 still sits inside the span.
        Assert.Equal((0.0, -100.0, 600.0, 200.0), vm.ViewBounds);
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
