using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.Core.Layout;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Board geometry and stacked-mode state for the render surface: canvas size
/// plus the per-hand translation offsets for both side-by-side and vertically
/// stacked layouts. Composed by <see cref="MainWindowViewModel"/> (persisted,
/// profile-switchable) and <see cref="ExitKeyPickerViewModel"/> (a fixed,
/// never-stacked profile), and exposed through <see cref="IBoardSurface"/> so
/// <c>BoardView</c> binds one geometry source for both hosts. The stacked-vs-
/// horizontal placement math itself lives on <see cref="BoardLayoutGeometry"/>;
/// this VM owns the observable toggles, the persisted state, and the per-hand
/// bounds it feeds that struct.
/// </summary>
public partial class BoardLayoutViewModel : ObservableObject
{
    // Null for hosts that don't persist (e.g. the exit-key picker).
    private readonly ISettingsService? _settings;
    private IKeyboardProfile _profile;

    // Per-profile bounding boxes for each hand, recomputed on profile change.
    // Used to translate each half's container in stacked mode so the bounding
    // box starts at the canvas-edge margin.
    private (double MinX, double MinY, double MaxX, double MaxY) _leftBounds;
    private (double MinX, double MinY, double MaxX, double MaxY) _rightBounds;

    public BoardLayoutViewModel(
        IKeyboardProfile profile,
        ISettingsService? settings = null,
        bool stacked = false,
        string topHand = "Left")
    {
        _settings = settings;
        _isStackedLayout = stacked;
        _stackedTopHand = string.IsNullOrWhiteSpace(topHand) ? "Left" : topHand;
        _profile = profile;
        RecomputeBounds();
    }

    /// <summary>Switches the active profile, recomputes the per-hand bounds, and
    /// raises every geometry property so the board re-lays-out.</summary>
    public void SetProfile(IKeyboardProfile profile)
    {
        _profile = profile;
        RecomputeBounds();
    }

    private void RecomputeBounds()
    {
        // Rotated bounds matter for boards like Glove80 whose shared-pivot thumb
        // cluster swings far outside the unrotated (X, Y, W, H) rect.
        _leftBounds = _profile.Keys.Where(k => k.Hand == Hand.Left).RotatedBounds();
        _rightBounds = _profile.Keys.Where(k => k.Hand == Hand.Right).RotatedBounds();

        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(BoardSurfaceWidth));
        OnPropertyChanged(nameof(BoardSurfaceHeight));
        OnPropertyChanged(nameof(LeftHandX));
        OnPropertyChanged(nameof(LeftHandY));
        OnPropertyChanged(nameof(RightHandX));
        OnPropertyChanged(nameof(RightHandY));
    }

    /// <summary>Current placement math for the board halves. Rebuilt on read
    /// from the live bounds + layout toggles.</summary>
    private BoardLayoutGeometry Geometry => new(
        _leftBounds, _rightBounds,
        _profile.CanvasWidth, _profile.CanvasHeight,
        IsStackedLayout, StackedTopHand == "Right");

    /// <summary>When true, the two halves render stacked vertically instead of side-by-side. Persisted across launches.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanvasWidth))]
    [NotifyPropertyChangedFor(nameof(CanvasHeight))]
    [NotifyPropertyChangedFor(nameof(LeftHandX))]
    [NotifyPropertyChangedFor(nameof(LeftHandY))]
    [NotifyPropertyChangedFor(nameof(RightHandX))]
    [NotifyPropertyChangedFor(nameof(RightHandY))]
    private bool _isStackedLayout;

    partial void OnIsStackedLayoutChanged(bool value) =>
        _settings?.Update(s => s with { StackedLayout = value }, "BoardLayout");

    /// <summary>Which half ("Left"/"Right") sits on top in stacked mode. Ignored in horizontal mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftHandX))]
    [NotifyPropertyChangedFor(nameof(LeftHandY))]
    [NotifyPropertyChangedFor(nameof(RightHandX))]
    [NotifyPropertyChangedFor(nameof(RightHandY))]
    private string _stackedTopHand;

    partial void OnStackedTopHandChanged(string value) =>
        _settings?.Update(s => s with { StackedTopHand = value }, "BoardLayout");

    [RelayCommand]
    private void ToggleStackedLayout() => IsStackedLayout = !IsStackedLayout;

    /// <summary>Canvas size for the current keyboard profile and layout mode (drives BoardView's Canvas width/height).</summary>
    public double CanvasWidth => Geometry.CanvasWidth;
    public double CanvasHeight => Geometry.CanvasHeight;

    /// <summary>
    /// Per-hand drawing surface size, independent of layout mode. Each per-hand
    /// ItemsControl in BoardView binds Width/Height to these so it has a
    /// non-zero render box — keys position themselves absolutely within it
    /// using the original profile coordinates, and the surrounding Canvas.Left/
    /// Top translates the whole surface for stacked mode.
    /// </summary>
    public double BoardSurfaceWidth => Geometry.BoardSurfaceWidth;
    public double BoardSurfaceHeight => Geometry.BoardSurfaceHeight;

    /// <summary>Translation applied to each hand's container (0 in horizontal mode).</summary>
    public double LeftHandX => Geometry.LeftHandX;
    public double LeftHandY => Geometry.LeftHandY;
    public double RightHandX => Geometry.RightHandX;
    public double RightHandY => Geometry.RightHandY;
}
