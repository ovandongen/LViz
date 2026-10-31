using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LViz.App.ViewModels;
using LViz.Core.Layout;

namespace LViz.App.Views;

/// <summary>
/// The drawing and input surface of the layout editor: it renders a
/// <see cref="LayoutEditorViewModel"/> and turns pointer and keyboard events
/// into its calls. All editor logic (hit precedence, snapping, hands) lives in
/// the view-model — this control only maps centi-u to pixels
/// (<see cref="ToCentiU"/>), paints the state, and forwards input.
///
/// <para>The mapping fits <see cref="LayoutEditorViewModel.ViewBounds"/> into
/// <see cref="Bounds"/> with padding, centred, never zooming past
/// <see cref="MaxScale"/> px per centi-u — the same density the main board
/// uses. Grab tolerances are converted from a constant pixel size
/// (<see cref="GrabPixels"/>) through the current scale, so grabbing the
/// rotation handle or the split line feels the same at any zoom.</para>
/// </summary>
public sealed class LayoutEditorCanvas : Control
{
    /// <summary>Upper bound for the scale, px per centi-u — the main board's
    /// density, so the editor never looks coarser-grained than the board.</summary>
    private const double MaxScale = 0.6;

    /// <summary>Empty pixels kept around the fitted view bounds.</summary>
    private const double MappingPadding = 12;

    /// <summary>Grab radius for the rotation handle and split line, in screen
    /// pixels — divided by the scale to reach centi-u.</summary>
    private const double GrabPixels = 8;

    /// <summary>Radius of the rotation handle's dot, in screen pixels.</summary>
    private const double HandleRadiusPx = 6;

    /// <summary>Key corner radius at full scale, centi-u-scaled like the keys.</summary>
    private const double KeyCornerRadiusPx = 6;

    /// <summary>Label font size at full scale, px.</summary>
    private const double LabelFontSizePx = 20;

    /// <summary>Stroke width of the rotation handle's stem, px.</summary>
    private const double HandleStemWidthPx = 1;

    // Fixed colours matching the App.axaml brushes of the same name, used when
    // the resources are not reachable (the control outside the app).
    private static readonly Color FallbackKeyDefaultFill = Color.Parse("#F2F2F2");
    private static readonly Color FallbackBgSurface1 = Color.Parse("#45475A");
    private static readonly Color FallbackKeyStroke = Color.Parse("#B0B0B0");
    private static readonly Color FallbackText = Color.Parse("#CDD6F4");
    private static readonly Color FallbackBgBase = Color.Parse("#1E1E2E");
    private static readonly Color FallbackAccentBlue = Color.Parse("#89B4FA");
    private static readonly Color FallbackAccentYellow = Color.Parse("#F9E2AF");
    private static readonly Color FallbackAccentTeal = Color.Parse("#94E2D5");

    public static readonly StyledProperty<LayoutEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<LayoutEditorCanvas, LayoutEditorViewModel?>(nameof(Editor));

    // Cursors are compared by equality on each move, so share three instances.
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor SplitLineCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor DefaultCursor = new(StandardCursorType.Arrow);

    static LayoutEditorCanvas()
    {
        EditorProperty.Changed.AddClassHandler<LayoutEditorCanvas>((x, e) =>
            x.EditorChanged(e.OldValue as LayoutEditorViewModel,
                e.NewValue as LayoutEditorViewModel));
    }

    /// <summary>The view-model to draw and feed. When it changes the control
    /// re-hooks <c>PropertyChanged</c> so every state change repaints.</summary>
    public LayoutEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    // The VM whose PropertyChanged we are currently subscribed to. Kept even
    // for a detached control's property change so OnAttachedToVisualTree can
    // tell "already hooked" from "needs the hook" and never double-subscribes.
    private LayoutEditorViewModel? _hooked;
    private bool _captured;

    // Mapping state, refreshed by UpdateMapping (at the top of Render and
    // before every input conversion): centi-u (cx, cy) -> pixels is
    // (cx * _scale + _offsetX, ...).
    private double _scale;
    private double _offsetX;
    private double _offsetY;

    public LayoutEditorCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        // The mapping depends on our size, so a resize must repaint.
        SizeChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Editor is { } vm && !ReferenceEquals(_hooked, vm))
            Hook(vm);
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unhook();
        _captured = false;
    }

    private void EditorChanged(LayoutEditorViewModel? oldVm, LayoutEditorViewModel? newVm)
    {
        if (ReferenceEquals(oldVm, newVm))
            return;

        if (oldVm is { } old)
            old.PropertyChanged -= OnEditorPropertyChanged;
        _hooked = null;

        if (newVm is { } vm)
            Hook(vm);

        InvalidateVisual();
    }

    private void Hook(LayoutEditorViewModel vm)
    {
        vm.PropertyChanged += OnEditorPropertyChanged;
        _hooked = vm;
    }

    private void Unhook()
    {
        if (_hooked is { } vm)
            vm.PropertyChanged -= OnEditorPropertyChanged;
        _hooked = null;
    }

    private void OnEditorPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    // ---- mapping ------------------------------------------------------------

    /// <summary>
    /// Recompute the centi-u → pixel mapping from the editor's current
    /// <see cref="LayoutEditorViewModel.ViewBounds"/> and our bounds. False
    /// (and no drawing, no input) without an editor, with zero-size bounds,
    /// or when the padding eats the whole control.
    /// </summary>
    private bool UpdateMapping()
    {
        if (Editor is not { } ed)
            return false;

        var (minX, minY, maxX, maxY) = ed.ViewBounds;
        var bw = maxX - minX;
        var bh = maxY - minY;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0 || bw <= 0 || bh <= 0)
            return false;

        var s = Math.Min(
            Math.Min((w - 2 * MappingPadding) / bw, (h - 2 * MappingPadding) / bh),
            MaxScale);
        if (s <= 0)
            return false;

        _scale = s;
        _offsetX = (w - bw * s) / 2.0 - minX * s;
        _offsetY = (h - bh * s) / 2.0 - minY * s;
        return true;
    }

    private (double X, double Y)? ToCentiU(Point position)
    {
        if (!UpdateMapping())
            return null;
        return ((position.X - _offsetX) / _scale, (position.Y - _offsetY) / _scale);
    }

    private Point ToPixel(double x, double y) =>
        new(x * _scale + _offsetX, y * _scale + _offsetY);

    /// <summary>The grab tolerance in centi-u for the current zoom — the same
    /// 8 px the view-model was told to use on press.</summary>
    private double GrabTolerance => GrabPixels / _scale;

    // ---- rendering ----------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Editor is not { } ed || !UpdateMapping())
            return;

        // Transparent backdrop so empty space still hit-tests to us.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        DrawKeys(context, ed);
        DrawSplitLine(context, ed);
        DrawSnapGuides(context, ed);
        DrawRotateHandle(context, ed);
        DrawSelectionBox(context, ed);
    }

    private void DrawKeys(DrawingContext context, LayoutEditorViewModel ed)
    {
        var keys = ed.Keys;
        var labels = ed.Labels;
        var radius = Math.Max(2, KeyCornerRadiusPx * _scale / MaxScale);
        var fontSize = Math.Min(12, LabelFontSizePx * _scale / MaxScale);

        var leftFill = Brush("KeyDefaultFill", FallbackKeyDefaultFill);
        var rightFill = Brush("BgSurface1", FallbackBgSurface1);
        var stroke = new Pen(Brush("KeyStroke", FallbackKeyStroke), 1);
        var selected = new Pen(Brush("AccentYellow", FallbackAccentYellow), 2);
        // Light left keys need dark captions, dark right keys light ones —
        // the same pairing BoardView uses on KeyDefaultFill.
        var leftText = Brush("BgBase", FallbackBgBase);
        var rightText = Brush("Text", FallbackText);
        // The app's default family (Inter, see Program.cs). A bare
        // `new Typeface()` has no family and FontManager throws on it.
        var typeface = new Typeface(FontFamily.Default);

        // Keys touch in units (the MoErgo convention); the gap is drawn. Like
        // BoardView's per-key margin, but a slightly clearer inset so the
        // editor doesn't look crammed. The pivot stays put — rotated keys
        // still swing about their own rotation point.
        var inset = Math.Max(1, 2 * _scale / MaxScale);

        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var rect = new Rect(
                ToPixel(key.X, key.Y),
                new Size(key.W * _scale, key.H * _scale));
            rect = new Rect(
                rect.X + inset, rect.Y + inset,
                Math.Max(0, rect.Width - 2 * inset),
                Math.Max(0, rect.Height - 2 * inset));
            var fill = key.Hand == Hand.Left ? leftFill : rightFill;
            var textBrush = key.Hand == Hand.Left ? leftText : rightText;
            var pen = ed.Selection.Contains(i) ? selected : stroke;
            var label = i < labels.Count ? labels[i] : string.Empty;

            if (key.R == 0)
            {
                DrawKey(context, rect, fill, pen, radius, label, fontSize, typeface, textBrush);
                continue;
            }

            // Positive rotation is clockwise on screen (same as BoardView's
            // RotateTransform Angle = R / 100), about the key's own pivot.
            var pivot = ToPixel(key.Rx, key.Ry);
            using (context.PushTransform(
                       new RotateTransform(key.R / 100.0, pivot.X, pivot.Y).Value))
            {
                DrawKey(context, rect, fill, pen, radius, label, fontSize, typeface, textBrush);
            }
        }
    }

    private static void DrawKey(
        DrawingContext context, Rect rect, IBrush fill, IPen pen,
        double radius, string label, double fontSize, Typeface typeface, IBrush textBrush)
    {
        context.DrawRectangle(fill, pen, rect, radius, radius);
        if (label.Length == 0 || fontSize <= 0)
            return;

        var text = new FormattedText(
            label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, fontSize, textBrush);
        context.DrawText(text, new Point(
            rect.X + (rect.Width - text.Width) / 2.0,
            rect.Y + (rect.Height - text.Height) / 2.0));
    }

    private void DrawSplitLine(DrawingContext context, LayoutEditorViewModel ed)
    {
        var x = ToPixel(ed.SplitLineX, 0).X;
        var pen = new Pen(Brush("AccentBlue", FallbackAccentBlue), 1.5)
        {
            DashStyle = new DashStyle([4, 3], 0),
        };
        context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
    }

    /// <summary>The edges/centres the current move drag snapped onto — teal so
    /// they read as feedback, not as structure. Null (the common case) draws
    /// nothing; 0 is a legitimate guide value.</summary>
    private void DrawSnapGuides(DrawingContext context, LayoutEditorViewModel ed)
    {
        if (ed.SnapGuideX is null && ed.SnapGuideY is null)
            return;

        var pen = new Pen(Brush("AccentTeal", FallbackAccentTeal), 1)
        {
            DashStyle = new DashStyle([2, 2], 0),
        };

        if (ed.SnapGuideX is { } guideX)
        {
            var x = ToPixel(guideX, 0).X;
            context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
        }

        if (ed.SnapGuideY is { } guideY)
        {
            var y = ToPixel(0, guideY).Y;
            context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
        }
    }

    private void DrawRotateHandle(DrawingContext context, LayoutEditorViewModel ed)
    {
        if (ed.RotateHandle is not { } handle)
            return;

        var yellow = Brush("AccentYellow", FallbackAccentYellow);
        var dot = ToPixel(handle.X, handle.Y);
        // The stem drops to the selection's top centre — the gap the
        // view-model offset the handle by, in centi-u.
        var stemTop = ToPixel(handle.X, handle.Y + LayoutEditorViewModel.RotationHandleGap);

        context.DrawLine(new Pen(yellow, HandleStemWidthPx), stemTop, dot);
        context.DrawEllipse(yellow, null, dot, HandleRadiusPx, HandleRadiusPx);
    }

    private void DrawSelectionBox(DrawingContext context, LayoutEditorViewModel ed)
    {
        if (ed.SelectionBox is not (var x1, var y1, var x2, var y2))
            return;

        var blue = Brush("AccentBlue", FallbackAccentBlue);
        var rect = new Rect(ToPixel(x1, y1), ToPixel(x2, y2));

        using (context.PushOpacity(0.2))
            context.DrawRectangle(blue, null, rect);
        context.DrawRectangle(null, new Pen(blue, 1), rect);
    }

    private IBrush Brush(string resourceKey, Color fallback) =>
        this.TryFindResource(resourceKey, out var value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);

    // ---- pointer input ------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Editor is not { } ed)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (ToCentiU(e.GetPosition(this)) is not { } c)
            return;

        Focus();
        e.Pointer.Capture(this);
        _captured = true;
        ed.PointerPressed(c.X, c.Y,
            additive: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            tolerance: GrabTolerance);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Editor is not { } ed)
            return;
        if (ToCentiU(e.GetPosition(this)) is not { } c)
            return;

        if (_captured)
        {
            ed.PointerMoved(c.X, c.Y, free: e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            return;
        }

        if (ed.CurrentDrag == EditorDragKind.None)
            UpdateCursor(ed, c.X, c.Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_captured)
            return;

        _captured = false;
        e.Pointer.Capture(null);
        if (Editor is { } ed)
            ed.PointerReleased();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_captured)
            return;

        _captured = false;
        // Capture can vanish without a release (window switch, popup steals
        // it); never leave the view-model mid-drag.
        if (Editor is { } ed && ed.CurrentDrag != EditorDragKind.None)
            ed.PointerReleased();
    }

    /// <summary>Hand over a key or the handle, resize cursor near the split
    /// line, arrow elsewhere — the same precedence the view-model's press
    /// handling uses.</summary>
    private void UpdateCursor(LayoutEditorViewModel ed, double cx, double cy)
    {
        var tolerance = GrabTolerance;
        var overKeyOrHandle =
            UserLayoutGeometry.HitTest(ed.Keys, cx, cy) >= 0
            || (ed.RotateHandle is { } handle
                && Math.Abs(cx - handle.X) <= tolerance
                && Math.Abs(cy - handle.Y) <= tolerance);

        if (overKeyOrHandle)
            Cursor = HandCursor;
        else if (Math.Abs(cx - ed.SplitLineX) <= tolerance)
            Cursor = SplitLineCursor;
        else
            Cursor = DefaultCursor;
    }

    // ---- keyboard -----------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Editor is not { } ed)
        {
            base.OnKeyDown(e);
            return;
        }

        var free = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        switch (e.Key)
        {
            case Key.Left:
                ed.Nudge(-1, 0, free);
                break;
            case Key.Right:
                ed.Nudge(1, 0, free);
                break;
            case Key.Up:
                ed.Nudge(0, -1, free);
                break;
            case Key.Down:
                ed.Nudge(0, 1, free);
                break;
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                         || e.KeyModifiers.HasFlag(KeyModifiers.Control):
                ed.SelectAll();
                break;
            case Key.Escape:
                ed.ClearSelection();
                break;
            default:
                base.OnKeyDown(e);
                return;
        }

        e.Handled = true;
    }
}
