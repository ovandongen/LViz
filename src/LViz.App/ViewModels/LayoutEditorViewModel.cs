using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// (move, rotate, box select, split line), nudging and reset. Everything is in
/// ZMK units (centi-u, centi-degrees) and every geometry question is answered
/// by <see cref="UserLayoutGeometry"/> — this class owns no geometry of its own.
///
/// <para>Drag results are always computed from the snapshot taken when the drag
/// began, never from the previous move, so a drag is idempotent per pointer
/// position and cannot accumulate rounding error. Hands follow the split line
/// live: <see cref="Keys"/> is <i>always</i> the result of
/// <see cref="UserLayoutGeometry.ApplyHands"/>, so a key dragged across the
/// line changes hand mid-drag.</para>
///
/// <para>There is no undo — <see cref="ResetCommand"/> restores the state the
/// editor was created with. Keys can be moved and rotated, never added or
/// removed: the key order is the keymap binding order.</para>
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

    // Drag scratch: the press point, the state as it was when the drag began,
    // and (for a rotation) the pivot the selection turns about.
    private double _pressX;
    private double _pressY;
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

    public LayoutEditorViewModel(
        IReadOnlyList<UserLayoutKey> keys, IReadOnlyList<string> labels, int splitLineX)
    {
        if (keys.Count == 0)
            throw new ArgumentException("A layout needs at least one key.", nameof(keys));
        if (labels.Count != keys.Count)
            throw new ArgumentException(
                $"Got {labels.Count} labels for {keys.Count} keys.", nameof(labels));

        Labels = labels;
        _startSplitLineX = splitLineX;
        _startKeys = UserLayoutGeometry.ApplyHands(keys, splitLineX);

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
    /// zoom so the grab zone stays a constant size on screen.</param>
    public void PointerPressed(double x, double y, bool additive, double tolerance)
    {
        if (CurrentDrag != EditorDragKind.None)
            return;

        if (RotateHandle is { } handle
            && Math.Abs(x - handle.X) <= tolerance && Math.Abs(y - handle.Y) <= tolerance)
        {
            _pivot = SelectionBoundsCentre();
            BeginDrag(EditorDragKind.Rotate, x, y, additive);
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

            BeginDrag(EditorDragKind.Move, x, y, additive);
            return;
        }

        if (Math.Abs(x - SplitLineX) <= tolerance)
        {
            BeginDrag(EditorDragKind.SplitLine, x, y, additive);
            return;
        }

        if (!additive)
            Selection = new HashSet<int>();

        BeginDrag(EditorDragKind.Box, x, y, additive);
    }

    /// <summary>
    /// Continue the active drag; a no-op when nothing is being dragged.
    /// <paramref name="free"/> (Option/Alt) drops the snapping: 1 centi-u for
    /// positions, 1° for rotation.
    /// </summary>
    public void PointerMoved(double x, double y, bool free)
    {
        switch (CurrentDrag)
        {
            case EditorDragKind.None:
                return;

            case EditorDragKind.Move:
            {
                var dx = free
                    ? Round1(x - _pressX)
                    : UserLayoutGeometry.Snap(x - _pressX, UserLayoutGeometry.PositionStep);
                var dy = free
                    ? Round1(y - _pressY)
                    : UserLayoutGeometry.Snap(y - _pressY, UserLayoutGeometry.PositionStep);
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

    /// <summary>End the drag: the selection box goes away and the viewport is
    /// refitted to wherever the keys ended up.</summary>
    public void PointerReleased()
    {
        SelectionBox = null;
        CurrentDrag = EditorDragKind.None;
    }

    /// <summary>Shift the selection by whole steps — 0.25u each, or 1 centi-u
    /// when <paramref name="free"/> is set (arrow keys with and without
    /// modifiers). Nothing is selected, nothing moves.</summary>
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

    /// <summary>Back to the keys and the split the editor was created with: no
    /// undo stack, just the original state.</summary>
    [RelayCommand]
    private void Reset()
    {
        CurrentDrag = EditorDragKind.None;
        SelectionBox = null;
        SplitLineX = _startSplitLineX;
        Keys = _startKeys;
        Selection = new HashSet<int>();
    }

    partial void OnKeysChanged(IReadOnlyList<UserLayoutKey> value)
    {
        RotateHandle = ComputeRotateHandle();
        IsModified = ComputeIsModified();
        RefitView();
    }

    partial void OnSelectionChanged(IReadOnlySet<int> value) =>
        RotateHandle = ComputeRotateHandle();

    partial void OnSplitLineXChanged(int value)
    {
        IsModified = ComputeIsModified();
        RefitView();
    }

    // The viewport only refits between drags — mid-drag it holds still so the
    // scale doesn't wobble under the pointer.
    partial void OnCurrentDragChanged(EditorDragKind value)
    {
        if (value == EditorDragKind.None)
            ViewBounds = ComputeViewBounds();
    }

    private void BeginDrag(EditorDragKind kind, double x, double y, bool additive)
    {
        _pressX = x;
        _pressY = y;
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
