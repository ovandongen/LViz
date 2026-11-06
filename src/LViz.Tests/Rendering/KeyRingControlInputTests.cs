using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using LViz.App.Ring;
using LViz.App.ViewModels;
using LViz.App.Views;
using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Rendering;

/// <summary>
/// The key ring's input surface: <see cref="KeyRingControl.HitTest"/> by
/// angle and radius (crumbs, centre, band, main ring), <c>IsOverRing</c>,
/// real headless pointer gestures (hover, dead-zone, left/right click,
/// dismiss), the keyboard cursor around the ring and into the band, and the
/// two host events. Points are <c>RingCentre + Polar(r, a)</c>; band and
/// crumb points come from the control's own layout.
/// </summary>
public class KeyRingControlInputTests
{
    // The part-3 fixture ring: &kp A is key 0 of Base (ten keys, Base/Nav).
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

    // ─── HitTest ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void HitTest_Centre_AndMainRingByAngle()
    {
        var (control, window, ring) = Show();

        Assert.Equal("center", control.HitTest(KeyRingControl.RingCentre));

        // Angle 0 is inside category 0's span on the &kp A summary ring.
        Assert.Equal("cat:Key", control.HitTest(At(110, 0)));

        // π/2: the category whose span holds it, computed with SegmentAt on
        // the very angle the control will derive from the point.
        var p = At(110, Math.PI / 2);
        var angle = RingGeometry.AngleOf(
            p.X - KeyRingControl.RingCentre.X, p.Y - KeyRingControl.RingCentre.Y);
        var index = RingGeometry.SegmentAt(angle, ring.Segments.Count, RingGeometry.CenteredStart(ring.Segments.Count));
        Assert.Equal(ring.Segments[index].Id, control.HitTest(p));

        window.Close();
    }

    [AvaloniaFact]
    public void HitTest_BandItemOnlyWhileBandIsOpen()
    {
        var (control, window, ring) = Show();

        // No band is open: the band radius belongs to nothing.
        Assert.Null(control.HitTest(At(200, 0)));

        control.SetHover("cat:Layer", animate: false);
        var band = ring.BandFor("cat:Layer");
        Assert.NotEmpty(band);

        var item = control.BandItemCentres[0]; // mid angle, radius 195
        Assert.Equal(band[0].Id, item.Id);
        Assert.Equal(band[0].Id, control.HitTest(item.Centre));

        window.Close();
    }

    [AvaloniaFact]
    public void HitTest_Crumb_AndOutsideTheRing()
    {
        var (control, window, _) = Show();

        Assert.Equal("crumb:0", control.HitTest(control.CrumbBounds[0].Center));

        // Below-left of everything: no id, and not over the ring.
        Assert.Null(control.HitTest(new Point(5, 500)));
        Assert.False(control.IsOverRing(new Point(5, 500)));

        // The centre and the hint pill (its middle) are the ring's.
        Assert.True(control.IsOverRing(KeyRingControl.RingCentre));
        Assert.True(control.IsOverRing(new Point(
            KeyRingControl.RingCentre.X,
            KeyRingControl.RingCentre.Y + RingMetrics.ExtentRadius
                + RingMetrics.HintGap + RingMetrics.HintHeight / 2)));

        window.Close();
    }

    // ─── keyboard cursor ─────────────────────────────────────────────────

    [AvaloniaFact]
    public void MoveKeyboardHighlight_StepsAroundTheCurrentValue()
    {
        var (control, window, _) = Show();

        control.MoveKeyboardHighlight(1);
        Assert.Equal("cat:HoldTap", control.KeyboardHighlightId);
        Assert.Equal("cat:HoldTap", control.PinnedBandId); // its band pins along

        control.MoveKeyboardHighlight(-1);
        Assert.Equal("cat:Key", control.KeyboardHighlightId);

        control.MoveKeyboardHighlight(-1);
        Assert.Equal("cat:Keymap", control.KeyboardHighlightId); // wraps past 0

        window.Close();
    }

    [AvaloniaFact]
    public void ActivateKeyboardHighlight_Centre_RaisesSaveRequested()
    {
        var (control, window, _) = Show();
        var fired = false;
        control.SaveRequested += (_, _) => fired = true;

        Assert.False(control.ActivateKeyboardHighlight()); // nothing highlighted

        control.SetKeyboardHighlight("center");
        Assert.True(control.ActivateKeyboardHighlight());
        Assert.True(fired); // &kp A is a complete binding: CanClose

        window.Close();
    }

    [AvaloniaFact]
    public void Keyboard_CursorIntoBand_AndActivatesItsItem()
    {
        var (control, window, ring) = Show();

        control.SetKeyboardHighlight("cat:Clear");
        Assert.True(control.ActivateKeyboardHighlight());
        Assert.Equal("cat:Clear", control.PinnedBandId); // like clicking it

        control.MoveIntoBand();
        Assert.Equal("beh:&trans", control.KeyboardHighlightId);

        control.MoveKeyboardHighlight(1); // steps through the band items
        Assert.Equal("beh:&none", control.KeyboardHighlightId);
        control.MoveKeyboardHighlight(-1);
        Assert.Equal("beh:&trans", control.KeyboardHighlightId);

        Assert.True(control.ActivateKeyboardHighlight());
        Assert.Equal("&trans", ring.Dialog.SelectedBehavior.Code);
        Assert.Null(control.KeyboardHighlightId);
        Assert.Null(control.PinnedBandId);

        window.Close();
    }

    [AvaloniaFact]
    public void StepChange_ClearsHover()
    {
        var (control, window, ring) = Show();

        control.SetHover("cat:Key", animate: false);
        ring.Choose("beh:&mo"); // a step change removes the hovered id

        Assert.Null(control.HoverId);
        window.Close();
    }

    // ─── pointer ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void PointerMoved_Hovers_KeepsTheDeadZone_AndExitClears()
    {
        // Its own layout: a Canvas gives the control its frame size, so a
        // move past that edge is a real pointer exit.
        var control = new KeyRingControl { Ring = BuildRing() };
        var window = new Window { Width = 560, Height = 600, Content = new Canvas { Children = { control } } };
        window.Show();

        window.MouseMove(At(110, 0));
        Assert.Equal("cat:Key", control.HoverId);

        // Between the ring's outer edge and the band: a miss keeps the hover.
        window.MouseMove(At(165, 0));
        Assert.Equal("cat:Key", control.HoverId);

        // Off the control entirely: exit clears it.
        window.MouseMove(new Point(450, 520));
        Assert.Null(control.HoverId);

        window.Close();
    }

    [AvaloniaFact]
    public void PointerClick_PinsAndUnpinsABandSegment()
    {
        var (control, window, _) = Show();
        var p = At(110, 0); // cat:Key, a main segment with a band

        Click(window, p, MouseButton.Left);
        Assert.Equal("cat:Key", control.PinnedBandId);

        Click(window, p, MouseButton.Left); // the pinned one unpins
        Assert.Null(control.PinnedBandId);

        // A press inside the ring's extent dismisses nothing.
        Click(window, At(165, 0), MouseButton.Left);
        Assert.Null(control.PinnedBandId);

        window.Close();
    }

    [AvaloniaFact]
    public void PointerClick_BandItem_ChoosesIt()
    {
        var (control, window, ring) = Show();
        control.SetHover("cat:Layer", animate: false);

        Click(window, control.BandItemCentres[0].Centre, MouseButton.Left);

        Assert.Equal("&mo", ring.Dialog.SelectedBehavior.Code);
        Assert.Null(control.PinnedBandId);
        Assert.Null(control.HoverId); // the step change starts clean

        window.Close();
    }

    [AvaloniaFact]
    public void PointerClick_OutsideTheRing_RaisesDismissed()
    {
        var (control, window, _) = Show();
        var dismissed = false;
        control.Dismissed += (_, _) => dismissed = true;

        Click(window, new Point(5, 500), MouseButton.Left);
        Assert.True(dismissed);

        dismissed = false;
        Click(window, At(165, 0), MouseButton.Left); // still on the ring
        Assert.False(dismissed);

        window.Close();
    }

    [AvaloniaFact]
    public void PointerRightClick_GoesBackOneStep()
    {
        var (control, window, ring) = Show(keyIndex: 4); // &mo NAV
        ring.GoToCrumb(2);
        Assert.Equal(KeyRingStepKind.Layer, ring.StepKind);

        Click(window, At(110, 0), MouseButton.Right);

        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);
        window.Close();
    }

    [AvaloniaFact]
    public void PointerClick_Crumb_JumpsToThatStep()
    {
        var (control, window, ring) = Show(keyIndex: 4); // &mo NAV
        ring.GoToCrumb(2);
        Assert.Equal(KeyRingStepKind.Layer, ring.StepKind);

        Click(window, control.CrumbBounds[0].Center, MouseButton.Left);

        Assert.Equal(KeyRingStepKind.Behavior, ring.StepKind);
        window.Close();
    }

    /// <summary>A double-click on a band item that completes the binding
    /// picks it and saves.</summary>
    [AvaloniaFact]
    public void DoubleClick_OnACompletingValue_PicksAndSaves()
    {
        var (control, window, ring) = Show(keyIndex: 2); // &trans
        var saves = 0;
        control.SaveRequested += (_, _) => saves++;

        control.SetHover("cat:Clear", animate: false);
        var none = control.BandItemCentres.Single(item => item.Id == "beh:&none").Centre;
        DoubleClick(window, none);

        Assert.Equal("&none", ring.Dialog.SelectedBehavior.Code);
        Assert.Equal(1, saves);
        window.Close();
    }

    /// <summary>A double-click on a segment that only pins its band never
    /// saves, even when the binding is complete (the &amp;kp A summary).</summary>
    [AvaloniaFact]
    public void DoubleClick_OnABandOwner_DoesNotSave()
    {
        var (control, window, ring) = Show();
        var saves = 0;
        control.SaveRequested += (_, _) => saves++;

        var index = ring.Segments.ToList().FindIndex(segment => segment.Id == "cat:Layer");
        var (start, end) = RingGeometry.SegmentSpan(
            ring.Segments.Count, index, RingGeometry.CenteredStart(ring.Segments.Count));
        DoubleClick(window, At(110, (start + end) / 2));

        Assert.True(ring.CanClose);
        Assert.Equal(0, saves);
        window.Close();
    }

    /// <summary>The centre follows the pointer onto a segment (falling back
    /// to the keyboard cursor) and shows the binding again over the centre.</summary>
    [AvaloniaFact]
    public void PointedSegment_FollowsHoverThenKeyboardCursor()
    {
        var (control, window, _) = Show();

        Assert.Null(control.PointedSegment);

        control.SetKeyboardHighlight("cat:Mouse");
        Assert.Equal("cat:Mouse", control.PointedSegment?.Id);

        control.SetHover("cat:Layer", animate: false);
        Assert.Equal("cat:Layer", control.PointedSegment?.Id);

        control.SetHover("center", animate: false);
        Assert.Null(control.PointedSegment);

        window.Close();
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private static KeyRingViewModel BuildRing(int keyIndex = 0)
    {
        var config = ZmkKeymapLoader.LoadFromText(Keymap, "test");
        var defines = Preprocessor.CollectDefines(Keymap);
        var request = new KeyDialogRequest(config, defines, LayerIndex: 0, KeyIndex: keyIndex);
        return new KeyRingViewModel(new KeyDialogViewModel(request), request);
    }

    /// <summary>The control in a shown headless window (so its layout is
    /// built), hosting the fixture keymap's key <paramref name="keyIndex"/>.</summary>
    private static (KeyRingControl Control, Window Window, KeyRingViewModel Ring) Show(int keyIndex = 0)
    {
        var ring = BuildRing(keyIndex);
        var control = new KeyRingControl { Ring = ring };
        var window = new Window
        {
            Width = 560,
            Height = 600,
            Background = new SolidColorBrush(Color.Parse("#1E1E2E")),
            Content = control,
        };
        window.Show();
        return (control, window, ring);
    }

    /// <summary>A control-local point at radius <paramref name="radius"/> on
    /// angle <paramref name="angle"/> from the ring centre.</summary>
    private static Point At(double radius, double angle)
    {
        var p = RingGeometry.Polar(radius, angle);
        return new Point(KeyRingControl.RingCentre.X + p.X, KeyRingControl.RingCentre.Y + p.Y);
    }

    private static void Click(Window window, Point p, MouseButton button)
    {
        var modifiers = button == MouseButton.Right
            ? RawInputModifiers.RightMouseButton
            : RawInputModifiers.LeftMouseButton;
        window.MouseDown(p, button, modifiers);
        window.MouseUp(p, button, RawInputModifiers.None);
    }

    private static void DoubleClick(Window window, Point p)
    {
        Click(window, p, MouseButton.Left);
        Click(window, p, MouseButton.Left);
    }
}
