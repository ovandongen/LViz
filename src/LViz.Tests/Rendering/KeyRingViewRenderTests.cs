using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using LViz.App.ViewModels;
using LViz.App.Views;
using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Rendering;

/// <summary>
/// The hosted key-ring view: the ring, its list panel and centre
/// text box render in a window, the "open in the key dialog" button reports
/// itself once through <see cref="KeyRingView.Closed"/>, and Esc cancels.
/// Snapshots go to <see cref="SnapshotPaths.File"/> next to the ring
/// snapshots, continuing the reference numbering (10 and 11).
/// </summary>
public class KeyRingViewRenderTests
{
    // Part 3's fixture keymap: Base(0)/Nav(1) of ten keys, #define NAV 1,
    // and on Base &kp A, &lt NAV ESC, &trans, &none, &mo NAV, &bt BT_SEL 0,
    // &hm LSHFT A, a nested rgb_ug parameter the editors cannot show (so it
    // opens as Custom text), &kp LC(LS(T)) and &mt LSHFT F1.
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

    /// <summary>The keycode step of <c>&amp;kp LC(LS(T))</c>: the view hosts
    /// the ring and the panel stands beside it, listing the letter keycodes
    /// with the picked one highlighted.</summary>
    [AvaloniaFact]
    public void View_KeycodeStep_ShowsThePanelBesideTheRing()
    {
        var ring = BuildRing(8);
        ring.GoToCrumb(2);
        var (view, window) = Host(ring);

        Assert.True(view.PanelBorder.IsVisible);
        Assert.True(view.SearchBox.IsVisible);
        Assert.NotEmpty(view.PanelItems.Items);

        Save(window, "ring-10-view-keycode.png");
        window.Close();
    }

    /// <summary>Opened on the nested <c>&amp;rgb_ug</c> parameter the editors
    /// cannot show: the ring starts as Custom text, so the centre text box
    /// stands in the middle of the ring.</summary>
    [AvaloniaFact]
    public void View_CustomTextStep_ShowsTheCenterBox()
    {
        var (view, window) = Host(BuildRing(7));

        Assert.True(view.CenterText.IsVisible);
        Assert.False(view.PanelBorder.IsVisible);

        Save(window, "ring-11-view-text.png");
        window.Close();
    }

    /// <summary>The button closes the view with the open-in-dialog outcome,
    /// and a second click raises nothing (one outcome per view).</summary>
    [AvaloniaFact]
    public void OpenInDialogButton_RaisesClosedOnce()
    {
        var (view, window) = Host(BuildRing(8));
        var events = new List<KeyRingClosedEventArgs>();
        view.Closed += (_, e) => events.Add(e);

        view.OpenInDialogButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Single(events);
        Assert.True(events[0].OpenInDialog);
        Assert.Null(events[0].Result);

        view.OpenInDialogButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Single(events);
        window.Close();
    }

    /// <summary>Esc — after the initial focus landed on the panel's search
    /// box — cancels: a closed event with no result and no dialog switch.</summary>
    [AvaloniaFact]
    public void Escape_ClosesWithNoResult()
    {
        var (view, window) = Host(BuildRing(8));
        KeyRingClosedEventArgs? closed = null;
        view.Closed += (_, e) => closed = e;

        view.FocusInitial();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.NotNull(closed);
        Assert.Null(closed!.Result);
        Assert.False(closed.OpenInDialog);
        window.Close();
    }

    /// <summary>A double-click on a keycode row picks it and closes the view
    /// with the new binding.</summary>
    [AvaloniaFact]
    public void DoubleClick_OnAPanelRow_PicksAndCloses()
    {
        var ring = BuildRing(0); // &kp A
        ring.ChooseId("beh:&kp");  // on to the keycode step, Letters listed
        var (view, window) = Host(ring);
        KeyRingClosedEventArgs? closed = null;
        view.Closed += (_, e) => closed = e;

        var rows = ring.Panel!.Rows;
        var index = rows.ToList().FindIndex(row => row.Id == "keycode:B");
        var container = view.PanelItems.ContainerFromIndex(index)!;
        var point = container.TranslatePoint(new Point(20, container.Bounds.Height / 2), window)!.Value;

        for (var i = 0; i < 2; i++)
        {
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        }

        Assert.NotNull(closed);
        Assert.Equal("&kp", closed!.Result?.Behavior);
        Assert.Equal(["B"], closed.Result!.Params);
        Assert.False(closed.OpenInDialog);
        window.Close();
    }

    /// <summary>Hosts the view in a dark window at (20, 40) — the panel is
    /// placed to the ring's right, reaching outside the view's own frame,
    /// where the host's room holds it.</summary>
    private static (KeyRingView View, Window Window) Host(KeyRingViewModel ring)
    {
        var view = new KeyRingView { DataContext = ring };
        view.SetPanelPosition(224 + 165 + 16, 110);

        var canvas = new Canvas();
        canvas.Children.Add(view);
        Canvas.SetLeft(view, 20);
        Canvas.SetTop(view, 40);

        var window = new Window
        {
            Width = 760,
            Height = 600,
            Background = new SolidColorBrush(Color.Parse("#1E1E2E")),
            Content = canvas,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

    /// <summary>Runs the layout through a few timer ticks (the ring's entry
    /// animations settle on render ticks, as in the snapshot tests), captures
    /// the frame and saves it as <c>docs/ideas/ring-snapshots/{file}</c>.</summary>
    private static void Save(Window window, string file)
    {
        for (var i = 0; i < 8; i++)
        {
            Thread.Sleep(25);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var path = SnapshotPaths.File(file);
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0);
    }

    private static KeyRingViewModel BuildRing(int keyIndex)
    {
        var config = ZmkKeymapLoader.LoadFromText(Keymap, "test");
        var defines = Preprocessor.CollectDefines(Keymap);
        var request = new KeyDialogRequest(config, defines, LayerIndex: 0, KeyIndex: keyIndex);
        return new KeyRingViewModel(new KeyDialogViewModel(request), request);
    }
}
