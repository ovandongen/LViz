using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using LViz.App.Ring;
using LViz.App.ViewModels;
using LViz.App.Views;
using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Rendering;

/// <summary>
/// The key ring's host window: <see cref="KeyRingWindow.Present"/>
/// puts the <see cref="KeyRingView"/> on the host canvas so the ring centre
/// lands on the placement, keeps the list panel inside the window on either
/// side of the ring, draws the tether only once the ring has moved away from
/// the key, and reports <see cref="KeyRingWindow.Finished"/> exactly once —
/// on a press beside the ring or on Esc. Input goes through Avalonia 11.3.13's
/// <c>Avalonia.Headless</c> window helpers, whose signatures are the same ones
/// <see cref="KeyRingViewRenderTests"/> compiles against:
/// <c>MouseDown(Point, MouseButton, RawInputModifiers)</c>,
/// <c>MouseUp(Point, MouseButton, RawInputModifiers)</c> and
/// <c>KeyPress(Key, RawInputModifiers, PhysicalKey, string?)</c>.
/// </summary>
public class KeyRingWindowTests
{
    // The fixture keymap (KeyRingViewRenderTests), kept as a copy so
    // these tests stand alone. Base keys, 0-based:
    // 0 &kp A, 1 &lt NAV ESC, 2 &trans, 3 &none, 4 &mo NAV, 5 &bt BT_SEL 0,
    // 6 &hm LSHFT A, 7 &rgb_ug RGB_COLOR_HSB(…), 8 &kp LC(LS(T)),
    // 9 &mt LSHFT F1.
    private const string Keymap = """
        #define NAV 1

        / {
            behaviors {
                hm: hm {
                    compatible = "zmk,behavior-hold-tap";
                    #binding-cells = <2>;
                    bindings = <&kp>, <&kp>;
                };
            };

            keymap {
                compatible = "zmk,keymap";

                Base { bindings = < &kp A &lt NAV ESC &trans &none &mo NAV &bt BT_SEL 0 &hm LSHFT A &rgb_ug RGB_COLOR_HSB(0,100,50) &kp LC(LS(T)) &mt LSHFT F1 >; };

                Nav { bindings = < &trans &trans &trans &trans &trans &trans &trans &trans &trans &trans >; };
            };
        };
        """;

    /// <summary>The window the shared set-up hosts the ring in.</summary>
    private const double AreaWidth = 1200;
    private const double AreaHeight = 800;

    /// <summary>A key in the middle of that area: plenty of room all round, so
    /// the ring is neither clamped nor scaled and the panel fits on the right.</summary>
    private static Rect MiddleKey => new(590, 390, 20, 20);

    /// <summary>The ring is presented so its centre lands on the placement:
    /// the view's canvas offset is the centre minus the frame halves at scale 1
    /// (224 left, 260 top), within half a pixel. The key sits under the ring, so
    /// no tether is drawn.</summary>
    [AvaloniaFact]
    public void Present_PutsTheViewOnTheHostAtThePlacement()
    {
        var (window, placement) = Open(BuildRing(0), MiddleKey); // &kp A

        var view = SingleView(window);
        Assert.Equal(1.0, placement.Scale);
        AssertWithin(0.5, placement.Centre.X - 224, Canvas.GetLeft(view));
        AssertWithin(0.5, placement.Centre.Y - 260, Canvas.GetTop(view));
        Assert.False(window.Tether.IsVisible);

        window.Close();
    }

    /// <summary>On the keycode step the list panel stands to the ring's right,
    /// and every corner of it stays inside the window.</summary>
    [AvaloniaFact]
    public void Panel_BesideTheRing_StaysInsideTheWindow()
    {
        var ring = BuildRing(8); // &kp LC(LS(T))
        ring.GoToCrumb(2);       // the keycode step lists keycodes in the panel

        var (window, _) = Open(ring, MiddleKey);
        var view = SingleView(window);

        Assert.True(view.PanelBorder.IsVisible);
        foreach (var corner in WindowCorners(view.PanelBorder, window)) AssertInsideArea(corner);

        window.Close();
    }

    /// <summary>A key at the right edge pushes the ring left to keep its frame
    /// inside; the panel then goes to the ring's left and still fits — left of
    /// the ring centre and clear of the ring's backdrop.</summary>
    [AvaloniaFact]
    public void Panel_AtTheRightEdge_GoesToTheLeftOfTheRing()
    {
        var ring = BuildRing(8);
        ring.GoToCrumb(2);

        var (window, placement) = Open(ring, new Rect(1150, 390, 20, 20));
        var view = SingleView(window);

        Assert.True(view.PanelBorder.IsVisible);
        var corners = WindowCorners(view.PanelBorder, window);
        foreach (var corner in corners) AssertInsideArea(corner);

        var leftEdge = corners.Min(corner => corner.X);
        Assert.True(
            leftEdge < placement.Centre.X,
            $"panel left edge {leftEdge} should be left of the ring centre {placement.Centre.X}");

        // More than "somewhere left of centre": the panel clears the ring's
        // backdrop circle, so it really took the left side with the standard
        // gap — not the fallback spot clamped against the window's right edge.
        var rightEdge = corners.Max(corner => corner.X);
        Assert.True(
            rightEdge < placement.Centre.X - RingMetrics.BackdropRadius,
            $"panel right edge {rightEdge} should clear the ring backdrop at {placement.Centre.X}");

        window.Close();
    }

    /// <summary>A press on the host canvas itself cancels — once: the second
    /// press raises nothing.</summary>
    [AvaloniaFact]
    public void BackdropPress_CancelsOnce()
    {
        var (window, _) = Open(BuildRing(0), MiddleKey);
        var events = new List<KeyRingClosedEventArgs>();
        window.Finished += (_, e) => events.Add(e);

        var backdrop = new Point(20, 20);
        window.MouseDown(backdrop, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(events);
        Assert.Null(events[0].Result);
        Assert.False(events[0].OpenInDialog);

        window.MouseDown(backdrop, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(events);
        window.Close();
    }

    /// <summary>A press on the ring is not a cancel: the Key category at the top
    /// (radius 133, inside the category band) only pins its band.</summary>
    [AvaloniaFact]
    public void PressOnTheRing_DoesNotCancel()
    {
        var (window, placement) = Open(BuildRing(0), MiddleKey);
        var events = new List<KeyRingClosedEventArgs>();
        window.Finished += (_, e) => events.Add(e);

        var onRing = new Point(placement.Centre.X, placement.Centre.Y - 133);
        window.MouseDown(onRing, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(onRing, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(events);
        window.Close();
    }

    /// <summary>A press on the panel's own chrome — its padding, not a row — is
    /// no cancel either: the host cancels only when the press source is the host
    /// canvas itself. (The ring swallows its own presses; the panel border is the
    /// one surface whose press reaches the host and must be told apart.)</summary>
    [AvaloniaFact]
    public void PressOnThePanelChrome_DoesNotCancel()
    {
        var ring = BuildRing(8);
        ring.GoToCrumb(2);

        var (window, _) = Open(ring, MiddleKey);
        var view = SingleView(window);
        var events = new List<KeyRingClosedEventArgs>();
        window.Finished += (_, e) => events.Add(e);

        // Mid-way along the panel's 12 px bottom padding: the panel border is
        // what is under the pointer there.
        var onPanel = view.PanelBorder.TranslatePoint(new Point(130, 294), window)!.Value;
        window.MouseDown(onPanel, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(onPanel, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(events);
        window.Close();
    }

    /// <summary>Esc — after <c>OnOpened</c> put the initial focus on the view —
    /// cancels with no result.</summary>
    [AvaloniaFact]
    public void Escape_Cancels()
    {
        var (window, _) = Open(BuildRing(0), MiddleKey);
        var events = new List<KeyRingClosedEventArgs>();
        window.Finished += (_, e) => events.Add(e);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(events);
        Assert.Null(events[0].Result);
        window.Close();
    }

    /// <summary>A key in the bottom-left corner clamps the ring to the frame, so
    /// it sits far from the key and the tether line shows the connection.</summary>
    [AvaloniaFact]
    public void Tether_ShowsWhenTheRingMovedAway()
    {
        var (window, _) = Open(BuildRing(0), new Rect(10, 770, 20, 20));

        Assert.True(window.Tether.IsVisible);
        window.Close();
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    /// <summary>Presents <paramref name="ring"/> for <paramref name="key"/> in a
    /// fresh 1200 × 800 <see cref="KeyRingWindow"/>, shows it and returns the
    /// window with the placement the ring was presented with. The caller closes
    /// the window.</summary>
    private static (KeyRingWindow Window, RingPlacement Placement) Open(
        KeyRingViewModel ring, Rect key)
    {
        var area = new Size(AreaWidth, AreaHeight);
        var placement = RingGeometry.Place(new Rect(area), key.Center);
        var window = new KeyRingWindow { Width = AreaWidth, Height = AreaHeight };

        window.Present(ring, area, key, placement);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The 140 ms open animation advances on render ticks (as in the snapshot
        // tests): let it settle, so hit-testing and TranslatePoint see the
        // settled transform instead of a mid-fade frame.
        for (var i = 0; i < 8; i++)
        {
            Thread.Sleep(25);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        return (window, placement);
    }

    /// <summary>The one ring view on the host canvas (the tether is a sibling).</summary>
    private static KeyRingView SingleView(KeyRingWindow window)
        => Assert.Single(window.Host.Children.OfType<KeyRingView>());

    /// <summary>The control's four corners in the window's own coordinates.</summary>
    private static List<Point> WindowCorners(Control control, Visual window) =>
    [
        control.TranslatePoint(new Point(0, 0), window)!.Value,
        control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value,
        control.TranslatePoint(new Point(0, control.Bounds.Height), window)!.Value,
        control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window)!.Value,
    ];

    private static void AssertInsideArea(Point point)
    {
        Assert.InRange(point.X, 0.0, AreaWidth);
        Assert.InRange(point.Y, 0.0, AreaHeight);
    }

    private static void AssertWithin(double tolerance, double expected, double actual)
        => Assert.True(
            Math.Abs(expected - actual) <= tolerance,
            $"expected {expected} ± {tolerance}, got {actual}");

    private static KeyRingViewModel BuildRing(int keyIndex)
    {
        var config = ZmkKeymapLoader.LoadFromText(Keymap, "test");
        var defines = Preprocessor.CollectDefines(Keymap);
        var request = new KeyDialogRequest(config, defines, LayerIndex: 0, KeyIndex: keyIndex);
        return new KeyRingViewModel(new KeyDialogViewModel(request), request);
    }
}
