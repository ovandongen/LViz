using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.Core.Layout;

namespace LViz.App.ViewModels;

/// <summary>What the pointer is doing in the layout editor.</summary>
public enum EditorDragKind
{
    None,
    Move,
    Rotate,
    Box,
    SplitLine,
}

/// <summary>
/// Interaction state of the shared layout editor: selection, pointer drags
/// (move, rotate, box select, split line), nudging, mirroring one half onto the
/// other and reset. Everything is in ZMK units (centi-u, centi-degrees) and
/// every geometry question is answered by <see cref="UserLayoutGeometry"/> —
/// this class owns no geometry of its own.
///
/// <para>Drag results are always computed from the snapshot taken when the drag
/// began, never from the previous move, so a drag is idempotent per pointer
/// position and cannot accumulate rounding error. Hands follow the split line
/// live: <see cref="Keys"/> is <i>always</i> the result of
/// <see cref="UserLayoutGeometry.ApplyHands"/>, so a key dragged across the
/// line changes hand mid-drag.</para>
///
/// <para>A move drag pulls its selection onto neighbouring keys when an edge or
/// centre comes within the press tolerance (see <see cref="SnapGuideX"/> for
/// what to draw); the arrow-key nudge stays on the plain 0.25u grid.</para>
///
/// <para>There is no undo — <see cref="ResetCommand"/> restores the state the
/// editor was created with. Keys can be moved, rotated and mirrored, never
/// added or removed: the key order is the keymap binding order.</para>
/// </summary>
public sealed partial class LayoutEditorViewModel : ObservableObject
{
    /// <summary>Distance from the selection's top edge to the rotation handle,
    /// centi-u.</summary>
    public const double RotationHandleGap = 60;

    /// <summary>How far <see cref="ViewBounds"/> grows past the board and the
    /// split line, centi-u.</summary>
    public const double ViewPadding = 100;

    private readonly IReadOnlyList<UserLayoutKey> _startKeys;
    private readonly int _startSplitLineX;

    // Drag scratch: the press point and the grab tolerance it came with (a move
    // drag reuses that as its snap distance), the state as it was when the drag
    // began, and (for a rotation) the pivot the selection turns about.
    private double _pressX;
    private double _pressY;
    private double _pressTolerance;
    private IReadOnlyList<UserLayoutKey> _snapshotKeys = [];
    private IReadOnlyCollection<int> _snapshotSelection = new HashSet<int>();
    private (double X, double Y) _pivot;
    private bool _pressAdditive;

    [ObservableProperty] private IReadOnlyList<UserLayoutKey> _keys;
    [ObservableProperty] private int _splitLineX;
    [ObservableProperty] private IReadOnlySet<int> _selection = new HashSet<int>();
    [ObservableProperty] private EditorDragKind _currentDrag;
    [ObservableProperty] private (double X1, double Y1, double X2, double Y2)? _selectionBox;
    [ObservableProperty] private (double X, double Y)? _rotateHandle;
    [ObservableProperty] private (double MinX, double MinY, double MaxX, double MaxY) _viewBounds;
    [ObservableProperty] private bool _isModified;
    [ObservableProperty] private double? _snapGuideX;
    [ObservableProperty] private double? _snapGuideY;

    /// <param name="isSplit">Whether this is a split board — only then can a half
    /// be mirrored. Null when the host doesn't know (an imported or saved layout
    /// carries no flag): then <see cref="UserLayoutGeometry.HasSplitGap"/>
    /// guesses from the keys.</param>
    public LayoutEditorViewModel(
        IReadOnlyList<UserLayoutKey> keys, IReadOnlyList<string> labels, int splitLineX,
        bool? isSplit = null)
    {
        if (keys.Count == 0)
            throw new ArgumentException("A layout needs at least one key.", nameof(keys));
        if (labels.Count != keys.Count)
            throw new ArgumentException(
                $"Got {labels.Count} labels for {keys.Count} keys.", nameof(labels));

        Labels = labels;
        _startSplitLineX = splitLineX;
        _startKeys = UserLayoutGeometry.ApplyHands(keys, splitLineX);
        IsSplit = isSplit ?? UserLayoutGeometry.HasSplitGap(_startKeys);

        // Seeded through the fields: nothing can be subscribed yet, so the
        // derived state is computed here rather than through the change hooks
        // firing mid-construction.
        _splitLineX = splitLineX;
        _keys = _startKeys;
        _rotateHandle = ComputeRotateHandle();
        _viewBounds = ComputeViewBounds();
    }

    /// <summary>Caption of every key, index-aligned with <see cref="Keys"/> and
    /// fixed for the editor's lifetime (it comes from the keymap, not the
    /// layout).</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>
    /// Begin a press. Ignored while a drag is already active. The first match
    /// wins: rotation handle, key, split line, then empty space.
    /// </summary>
    /// <param name="additive">Shift held: toggles a key into the selection, or
    /// adds to it for a box drag; on empty space without it the selection
    /// clears.</param>
    /// <param name="tolerance">How close to the rotation handle or the split line
    /// the press has to land to grab it, centi-u — the canvas scales this by its
    /// zoom so the grab zone stays a constant size on screen. A move drag uses
    /// the same value as its distance to a neighbouring key.</param>
    public void PointerPressed(double x, double y, bool additive, double tolerance)
    {
        if (CurrentDrag != EditorDragKind.None)
            return;

        if (RotateHandle is { } handle
            && Math.Abs(x - handle.X) <= tolerance && Math.Abs(y - handle.Y) <= tolerance)
        {
            _pivot = SelectionBoundsCentre();
            BeginDrag(EditorDragKind.Rotate, x, y, additive, tolerance);
            return;
        }

        var hit = UserLayoutGeometry.HitTest(Keys, x, y);
        if (hit >= 0)
        {
            if (additive)
            {
                var toggled = new HashSet<int>(Selection);
                if (!toggled.Remove(hit))
                    toggled.Add(hit);
                Selection = toggled;
                // Toggling a key off selects nothing new — no drag to start.
                if (!toggled.Contains(hit))
                    return;
            }
            else if (!Selection.Contains(hit))
            {
                Selection = new HashSet<int> { hit };
            }

            BeginDrag(EditorDragKind.Move, x, y, additive, tolerance);
            return;
        }

        if (Math.Abs(x - SplitLineX) <= tolerance)
        {
            BeginDrag(EditorDragKind.SplitLine, x, y, additive, tolerance);
            return;
        }

        if (!additive)
            Selection = new HashSet<int>();

        BeginDrag(EditorDragKind.Box, x, y, additive, tolerance);
    }

    /// <summary>
    /// Continue the active drag; a no-op when nothing is being dragged.
    /// <paramref name="free"/> (Option/Alt) drops all snapping — 1 centi-u for
    /// positions, 1° for rotation, and no neighbour alignment or guides.
    /// </summary>
    public void PointerMoved(double x, double y, bool free)
    {
        switch (CurrentDrag)
        {
            case EditorDragKind.None:
                return;

            case EditorDragKind.Move:
            {
                int dx, dy;
                if (free)
                {
                    dx = Round1(x - _pressX);
                    dy = Round1(y - _pressY);
                    SnapGuideX = null;
                    SnapGuideY = null;
                }
                else
                {
                    var snap = UserLayoutGeometry.SnapMove(
                        _snapshotKeys, _snapshotSelection, x - _pressX, y - _pressY,
                        _pressTolerance);
                    dx = snap.Dx;
                    dy = snap.Dy;
                    SnapGuideX = snap.GuideX;
                    SnapGuideY = snap.GuideY;
                }

                Keys = UserLayoutGeometry.ApplyHands(
                    UserLayoutGeometry.Move(_snapshotKeys, _snapshotSelection, dx, dy), SplitLineX);
                break;
            }

            case EditorDragKind.Rotate:
            {
                var delta = RotationDelta(x, y, free);
                Keys = UserLayoutGeometry.ApplyHands(
                    UserLayoutGeometry.Rotate(
                        _snapshotKeys, _snapshotSelection, delta, _pivot.X, _pivot.Y),
                    SplitLineX);
                break;
            }

            case EditorDragKind.Box:
            {
                SelectionBox = (_pressX, _pressY, x, y);
                var hits = new HashSet<int>(
                    UserLayoutGeometry.InBox(_snapshotKeys, _pressX, _pressY, x, y));
                if (_pressAdditive)
                    hits.UnionWith(_snapshotSelection);
                Selection = hits;
                break;
            }

            case EditorDragKind.SplitLine:
                SplitLineX = free
                    ? Round1(x)
                    : UserLayoutGeometry.Snap(x, UserLayoutGeometry.PositionStep);
                Keys = UserLayoutGeometry.ApplyHands(Keys, SplitLineX);
                break;
        }
    }

    /// <summary>End the drag: the selection box and the snap guides go away and
    /// the viewport is refitted to wherever the keys ended up.</summary>
    public void PointerReleased()
    {
        SelectionBox = null;
        SnapGuideX = null;
        SnapGuideY = null;
        CurrentDrag = EditorDragKind.None;
    }

    /// <summary>Shift the selection by whole steps — 0.25u each, or 1 centi-u
    /// when <paramref name="free"/> is set (arrow keys with and without
    /// modifiers). Nothing is selected, nothing moves. Unlike a move drag this
    /// stays on the grid — it never pulls onto a neighbour.</summary>
    public void Nudge(int dxSteps, int dySteps, bool free)
    {
        if (Selection.Count == 0)
            return;

        var step = free ? 1 : UserLayoutGeometry.PositionStep;
        Keys = UserLayoutGeometry.ApplyHands(
            UserLayoutGeometry.Move(Keys, Selection, dxSteps * step, dySteps * step),
            SplitLineX);
    }

    public void SelectAll() => Selection = Enumerable.Range(0, Keys.Count).ToHashSet();

    public void ClearSelection() => Selection = new HashSet<int>();

    // ------------------------------------------------------------- mirror

    /// <summary>A split board, fixed for the editor's lifetime. A unibody has no
    /// halves to mirror, so the mirror toolbar is hidden for it.</summary>
    public bool IsSplit { get; }

    /// <summary>Keys on the left of the split line — what
    /// <see cref="MirrorLeftToRightCommand"/> copies from.</summary>
    public int LeftKeyCount => Keys.Count(key => key.Hand == Hand.Left);

    /// <summary>Keys on the right of the split line — what
    /// <see cref="MirrorRightToLeftCommand"/> copies from.</summary>
    public int RightKeyCount => Keys.Count(key => key.Hand == Hand.Right);

    /// <summary>Mirroring pairs every key of one half with a key of the other,
    /// so it needs equally full halves — and it waits for the pointer to come
    /// up, because mid-drag the counts are still moving.</summary>
    public bool CanMirror =>
        IsSplit && LeftKeyCount > 0 && LeftKeyCount == RightKeyCount
        && CurrentDrag == EditorDragKind.None;

    /// <summary>Why mirroring is unavailable, or null when it is available,
    /// while a drag is running (there the disabled buttons say enough) and on a
    /// unibody (no toolbar to explain).</summary>
    public string? MirrorHint =>
        CanMirror || !IsSplit || CurrentDrag != EditorDragKind.None
            ? null
            : Loc.Instance.Format("Editor_MirrorUnavailableFormat", LeftKeyCount, RightKeyCount);

    /// <summary>Back to the keys and the split the editor was created with: no
    /// undo stack, just the original state.</summary>
    [RelayCommand]
    private void Reset()
    {
        CurrentDrag = EditorDragKind.None;
        SelectionBox = null;
        SnapGuideX = null;
        SnapGuideY = null;
        SplitLineX = _startSplitLineX;
        Keys = _startKeys;
        Selection = new HashSet<int>();
    }

    /// <summary>Drop the left half's placement onto the right half: every
    /// right-hand key takes the position mirrored from its partner across the
    /// middle between the halves (not the split line, which only decides the
    /// hands and may sit off-centre). The selection clears — what was picked on the source side
    /// means nothing on the target side any more.</summary>
    [RelayCommand(CanExecute = nameof(CanMirror))]
    private void MirrorLeftToRight() => ApplyMirror(Hand.Left);

    /// <summary>Drop the right half's placement onto the left half.</summary>
    [RelayCommand(CanExecute = nameof(CanMirror))]
    private void MirrorRightToLeft() => ApplyMirror(Hand.Right);

    private void ApplyMirror(Hand from)
    {
        var mirrored = UserLayoutGeometry.Mirror(Keys, from);
        if (mirrored is null)
            return;

        Keys = UserLayoutGeometry.ApplyHands(mirrored, SplitLineX);
        Selection = new HashSet<int>();
    }

    partial void OnKeysChanged(IReadOnlyList<UserLayoutKey> value)
    {
        RotateHandle = ComputeRotateHandle();
        IsModified = ComputeIsModified();
        RefitView();
        NotifyMirrorState();
    }

    partial void OnSelectionChanged(IReadOnlySet<int> value) =>
        RotateHandle = ComputeRotateHandle();

    partial void OnSplitLineXChanged(int value)
    {
        IsModified = ComputeIsModified();
        RefitView();
    }

    // The viewport only refits between drags — mid-drag it holds still so the
    // scale doesn't wobble under the pointer. Mirror availability follows the
    // same rule, so it is refreshed here too.
    partial void OnCurrentDragChanged(EditorDragKind value)
    {
        if (value == EditorDragKind.None)
            ViewBounds = ComputeViewBounds();
        NotifyMirrorState();
    }

    /// <summary>Everything derived from the hands and the drag state. Mirroring
    /// reads live state rather than a field, so the commands have to be asked to
    /// re-check <see cref="CanMirror"/> whenever it moves.</summary>
    private void NotifyMirrorState()
    {
        OnPropertyChanged(nameof(LeftKeyCount));
        OnPropertyChanged(nameof(RightKeyCount));
        OnPropertyChanged(nameof(CanMirror));
        OnPropertyChanged(nameof(MirrorHint));
        MirrorLeftToRightCommand.NotifyCanExecuteChanged();
        MirrorRightToLeftCommand.NotifyCanExecuteChanged();
    }

    private void BeginDrag(EditorDragKind kind, double x, double y, bool additive, double tolerance)
    {
        _pressX = x;
        _pressY = y;
        _pressTolerance = tolerance;
        _pressAdditive = additive;
        _snapshotKeys = Keys;
        _snapshotSelection = new HashSet<int>(Selection);
        if (kind == EditorDragKind.Box)
            SelectionBox = (x, y, x, y);
        CurrentDrag = kind;
    }

    private void RefitView()
    {
        if (CurrentDrag == EditorDragKind.None)
            ViewBounds = ComputeViewBounds();
    }

    private (double MinX, double MinY, double MaxX, double MaxY) ComputeViewBounds()
    {
        var (minX, minY, maxX, maxY) = UserLayoutGeometry.RotatedBounds(Keys);
        var split = (double)SplitLineX;
        if (split < minX)
            minX = split;
        if (split > maxX)
            maxX = split;
        return (minX - ViewPadding, minY - ViewPadding, maxX + ViewPadding, maxY + ViewPadding);
    }

    private (double X, double Y)? ComputeRotateHandle()
    {
        if (Selection.Count == 0)
            return null;

        var (minX, minY, maxX, _) = SelectionRotatedBounds();
        return ((minX + maxX) / 2.0, minY - RotationHandleGap);
    }

    private (double X, double Y) SelectionBoundsCentre()
    {
        var (minX, minY, maxX, maxY) = SelectionRotatedBounds();
        return ((minX + maxX) / 2.0, (minY + maxY) / 2.0);
    }

    private (double MinX, double MinY, double MaxX, double MaxY) SelectionRotatedBounds()
    {
        var selected = new List<UserLayoutKey>(Selection.Count);
        for (var i = 0; i < Keys.Count; i++)
        {
            if (Selection.Contains(i))
                selected.Add(Keys[i]);
        }

        return UserLayoutGeometry.RotatedBounds(selected);
    }

    /// <summary>Signed angle swept about the pivot between press and current
    /// position, centi-degrees, snapped unless <paramref name="free"/>.</summary>
    private int RotationDelta(double x, double y, bool free)
    {
        var pressed = Math.Atan2(_pressY - _pivot.Y, _pressX - _pivot.X) * 180 / Math.PI;
        var current = Math.Atan2(y - _pivot.Y, x - _pivot.X) * 180 / Math.PI;
        var degrees = current - pressed;
        if (degrees > 180)
            degrees -= 360;
        else if (degrees <= -180)
            degrees += 360;

        return free
            ? (int)Math.Round(degrees, MidpointRounding.AwayFromZero) * 100
            : UserLayoutGeometry.Snap(degrees * 100, UserLayoutGeometry.RotationStep);
    }

    private bool ComputeIsModified() =>
        SplitLineX != _startSplitLineX || !Keys.SequenceEqual(_startKeys);

    private static int Round1(double value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
