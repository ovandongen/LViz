namespace LViz.App.ViewModels;

/// <summary>
/// Pure stacked-vs-horizontal board placement math. Given each hand's rotated
/// bounding box, the profile's natural canvas size, and the two layout toggles
/// (stacked? right-hand on top?), computes the canvas size and per-hand
/// translations <c>BoardView</c> binds to. Behavior is identical to the
/// formulas that previously lived inline on <see cref="MainWindowViewModel"/>.
/// </summary>
public readonly struct BoardLayoutGeometry
{
    /// <summary>Margin around the bounding boxes in stacked mode.</summary>
    private const double StackedMargin = 30;
    /// <summary>Vertical gap between the two halves in stacked mode.</summary>
    private const double StackedGap = 60;

    private readonly (double MinX, double MinY, double MaxX, double MaxY) _left;
    private readonly (double MinX, double MinY, double MaxX, double MaxY) _right;
    private readonly double _profileWidth;
    private readonly double _profileHeight;
    private readonly bool _stacked;
    private readonly bool _rightOnTop;

    public BoardLayoutGeometry(
        (double MinX, double MinY, double MaxX, double MaxY) left,
        (double MinX, double MinY, double MaxX, double MaxY) right,
        double profileWidth,
        double profileHeight,
        bool stacked,
        bool rightOnTop)
    {
        _left = left;
        _right = right;
        _profileWidth = profileWidth;
        _profileHeight = profileHeight;
        _stacked = stacked;
        _rightOnTop = rightOnTop;
    }

    private double LeftW => _left.MaxX - _left.MinX;
    private double LeftH => _left.MaxY - _left.MinY;
    private double RightW => _right.MaxX - _right.MinX;
    private double RightH => _right.MaxY - _right.MinY;

    public double CanvasWidth => _stacked
        ? Math.Max(LeftW, RightW) + 2 * StackedMargin
        : _profileWidth;

    public double CanvasHeight => _stacked
        ? LeftH + RightH + StackedGap + 2 * StackedMargin
        : _profileHeight;

    /// <summary>Per-hand drawing surface — the profile's natural size, layout-mode independent.</summary>
    public double BoardSurfaceWidth => _profileWidth;
    public double BoardSurfaceHeight => _profileHeight;

    public double LeftHandX => _stacked ? StackedMargin - _left.MinX : 0;

    public double LeftHandY => !_stacked
        ? 0
        : _rightOnTop
            ? StackedMargin + RightH + StackedGap - _left.MinY
            : StackedMargin - _left.MinY;

    public double RightHandX => _stacked ? StackedMargin - _right.MinX : 0;

    public double RightHandY => !_stacked
        ? 0
        : _rightOnTop
            ? StackedMargin - _right.MinY
            : StackedMargin + LeftH + StackedGap - _right.MinY;
}
