using System;
using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using LViz.App.Ring;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// The key ring's host: a borderless, transparent, topmost window over the
/// working area of the edited key's screen, so the ring is placed against the
/// monitor, never against the main window. It shows the ring centred on the
/// key (clamped and, if need be, scaled to fit), the list panel beside it, a
/// tether from the key, and the open animation. <see cref="MainWindow"/> owns
/// its life: it sizes and positions the window, calls <see cref="Present"/>,
/// waits for <see cref="Finished"/> and closes it.
/// </summary>
public partial class KeyRingWindow : Window
{
    /// <summary>Length of the fade-and-grow when the ring opens.</summary>
    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(140);

    private KeyRingView? _view;
    private bool _finished;

    public KeyRingWindow()
    {
        InitializeComponent();

        Host.PointerPressed += OnHostPressed;

        // Backup for clicks the backdrop can't see (the Dock, the menu bar,
        // another monitor): losing activation cancels too. Avalonia's Window
        // has no virtual OnDeactivated, so the event is the hook.
        Deactivated += (_, _) => Finish(Cancelled());
    }

    /// <summary>Raised once when the ring is done: saved, cancelled (Esc, a
    /// click beside the ring, another app activated), or moved to the key
    /// dialog. Later causes, such as the deactivation the host's own
    /// <c>Close</c> brings, are ignored.</summary>
    public event EventHandler<KeyRingClosedEventArgs>? Finished;

    /// <summary>
    /// Builds the ring for <paramref name="ring"/> in this window.
    /// <paramref name="area"/> is the window's own size, <paramref name="key"/>
    /// the edited key and <paramref name="placement"/> a fitting
    /// <see cref="RingGeometry.Place(Rect, Point)"/> for it, both in window
    /// coordinates (DIPs). When the panel needs the room on the right, the
    /// ring opens shifted left by that much.
    /// </summary>
    public void Present(KeyRingViewModel ring, Size area, Rect key, RingPlacement placement)
    {
        var bounds = new Rect(area);
        var panel = RingGeometry.PlacePanel(bounds, placement);
        if (panel is { ShiftX: not 0.0 } shifted)
        {
            placement = placement with
            {
                Centre = new Point(placement.Centre.X + shifted.ShiftX, placement.Centre.Y),
            };
        }

        var s = placement.Scale;
        var centre = placement.Centre;
        var origin = new Point(
            centre.X - RingMetrics.FrameLeft * s, centre.Y - RingMetrics.FrameTop * s);

        var view = new KeyRingView
        {
            DataContext = ring,
            Opacity = 0,
            RenderTransformOrigin = RelativePoint.TopLeft,
            RenderTransform = OpenTransform(s, opening: true),
        };
        Canvas.SetLeft(view, origin.X);
        Canvas.SetTop(view, origin.Y);

        // PlacePanel's spot already includes the shift; with no side that
        // fits, park the panel to the right of the ring, inside the area.
        var spot = panel is { } side ? new Point(side.Left, side.Top) : DefaultPanelSpot(bounds, centre, s);
        view.SetPanelPosition((spot.X - origin.X) / s, (spot.Y - origin.Y) / s);

        // The hint strip under the ring would sit on the very key being
        // edited — hide it then.
        var hintStrip = new Rect(
            centre.X - 170 * s, centre.Y + 232 * s, 340 * s, RingMetrics.HintHeight * s);
        view.RingControl.HideHint = hintStrip.Intersects(key);

        if (RingGeometry.Tether(key.Center, placement) is { } tether)
        {
            Tether.StartPoint = tether.From;
            Tether.EndPoint = tether.To;
            Tether.IsVisible = true;
        }

        view.Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = OpenDuration, Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = OpenDuration, Easing = new CubicEaseOut() },
        ];
        view.Closed += (_, closed) => Finish(closed);

        Host.Children.Add(view);
        _view = view;

        // Animate once the layout pass has seen the view.
        Dispatcher.UIThread.Post(() =>
        {
            view.Opacity = 1;
            view.RenderTransform = OpenTransform(s, opening: false);
        });
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // A platform without transparency can't show the screen through the
        // window, so the ring gets a solid backdrop over the whole screen; a
        // click beside the ring still cancels.
        if (ActualTransparencyLevel == WindowTransparencyLevel.None)
            Host.Background = SolidColorBrush.Parse("#181825");

        _view?.FocusInitial();
    }

    /// <summary>A press on the backdrop itself — not on the ring or its panel,
    /// which handle their own — cancels.</summary>
    private void OnHostPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Host)) return;
        Finish(Cancelled());
        e.Handled = true;
    }

    private void Finish(KeyRingClosedEventArgs closed)
    {
        if (_finished) return;
        _finished = true;
        Finished?.Invoke(this, closed);
    }

    private static KeyRingClosedEventArgs Cancelled() => new(null, openInDialog: false);

    /// <summary>To the right of the ring, clamped inside the area, for when
    /// <see cref="RingGeometry.PlacePanel"/> finds no side.</summary>
    private static Point DefaultPanelSpot(Rect area, Point centre, double s)
    {
        var m = RingMetrics.WindowMargin;
        var left = Math.Clamp(
            centre.X + (RingMetrics.BackdropRadius + RingMetrics.PanelGap) * s,
            m, Math.Max(m, area.Width - m - RingMetrics.PanelWidth * s));
        var top = Math.Clamp(
            centre.Y - RingMetrics.PanelHeight / 2 * s,
            m, Math.Max(m, area.Height - m - RingMetrics.PanelHeight * s));
        return new Point(left, top);
    }

    /// <summary>
    /// The view's open-animation transform. The settled state is scale
    /// <c>s</c> about the top-left (the view sits so the ring centre lands on
    /// its placement); the opening state shrinks that to 0.94·s around the
    /// ring centre, written as translate + scale about the top-left with the
    /// same operations, so the transition interpolates cleanly.
    /// </summary>
    private static TransformOperations OpenTransform(double s, bool opening)
    {
        if (!opening)
            return TransformOperations.Parse($"translate(0px, 0px) scale({Inv(s)}, {Inv(s)})");

        const double shrink = 0.94;
        var k = s * shrink;
        var dx = s * (1 - shrink) * RingMetrics.FrameLeft;
        var dy = s * (1 - shrink) * RingMetrics.FrameTop;
        return TransformOperations.Parse(
            $"translate({Inv(dx)}px, {Inv(dy)}px) scale({Inv(k)}, {Inv(k)})");
    }

    private static string Inv(double value) => value.ToString(CultureInfo.InvariantCulture);
}
