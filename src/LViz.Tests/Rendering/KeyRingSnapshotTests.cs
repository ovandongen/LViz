using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LViz.App.ViewModels;
using LViz.App.Views;
using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Rendering;

/// <summary>
/// Snapshot renders of every state the visual reference
/// (docs/ideas/lviz-key-ring-reference.html) shows: each fact draws
/// <see cref="KeyRingControl"/> once and saves the frame to
/// <see cref="SnapshotPaths.File"/> as one numbered picture, so the review
/// can compare them side by side with the reference; <c>index.html</c>
/// collects them. They also gate that <c>Render</c> throws in none of the
/// states.
/// </summary>
public class KeyRingSnapshotTests
{
    // The reference fixture keymap. Base keys, 0-based:
    // 0 &kp A, 1 &lt NAV ESC, 2 &trans, 3 &mo NAV, 4 &bt BT_SEL 0,
    // 5 &kp LC(LS(T)), 6 &rgb_ug RGB_TOG, 7 &kp SPACE — one key per state.
    private const string Keymap = """
        #define NAV 3

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

                Base { bindings = < &kp A &lt NAV ESC &trans &mo NAV &bt BT_SEL 0 &kp LC(LS(T)) &rgb_ug RGB_TOG &kp SPACE >; };

                Sys { bindings = < &trans &trans &trans &trans &trans &trans &trans &trans >; };

                Symbol { bindings = < &trans &trans &trans &trans &trans &trans &trans &trans >; };

                Nav { bindings = < &trans &trans &trans &trans &trans &trans &trans &trans >; };

                Numbers { bindings = < &trans &trans &trans &trans &trans &trans &trans &trans >; };

                Mouse { bindings = < &trans &trans &trans &trans &trans &trans &trans &trans >; };

                Func { bindings = < &trans &trans &trans &trans &trans &trans &trans &trans >; };
            };
        };
        """;

    /// <summary>Every picture in reference order. The index page lists these
    /// names whether or not the facts that write them have run yet, so the
    /// page never depends on the order the facts run in.</summary>
    private static readonly string[] SnapshotFiles =
    [
        "ring-1-behavior.png",
        "ring-1b-key-band.png",
        "ring-2-layer.png",
        "ring-3-bt-profile.png",
        "ring-4-keycode-mods.png",
        "ring-5-summary.png",
        "ring-6-incomplete.png",
        "ring-7-rgb-13.png",
        "ring-8-keyboard.png",
        "ring-9-scaled.png",
        "ring-10-view-keycode.png",
        "ring-11-view-text.png",
    ];

    /// <summary>Category ring with the Layer category hovered and its
    /// <c>&amp;mo</c> band item hovered on top (the band stays open because its
    /// owner segment was hovered first).</summary>
    [AvaloniaFact]
    public void Ring1_BehaviorBand()
    {
        Render("ring-1-behavior.png", BuildRing(keyIndex: 2), c =>
        {
            c.SetHover("cat:Layer", animate: false);
            c.SetHover("beh:&mo", animate: false);
        });
    }

    /// <summary>The same ring with the Key category hovered: its band of
    /// key behaviors.</summary>
    [AvaloniaFact]
    public void Ring1b_KeyBand()
    {
        Render("ring-1b-key-band.png", BuildRing(keyIndex: 2),
            c => c.SetHover("cat:Key", animate: false));
    }

    /// <summary>The layer step (opened on <c>&amp;mo NAV</c>, moved to the
    /// layer parameter, layer 0 chosen) with the Nav layer hovered.</summary>
    [AvaloniaFact]
    public void Ring2_LayerStep()
    {
        var ring = BuildRing(keyIndex: 3);
        ring.GoToCrumb(2);
        ring.ChooseId("layer:0");
        Render("ring-2-layer.png", ring, c => c.SetHover("layer:3", animate: false));
    }

    /// <summary>The command step of <c>&amp;bt BT_SEL 0</c> with profile 2
    /// chosen; BT_SEL and its profile band item hovered.</summary>
    [AvaloniaFact]
    public void Ring3_BtProfileBand()
    {
        var ring = BuildRing(keyIndex: 4);
        ring.GoToCrumb(2);
        ring.ChooseId("prof:BT_SEL:2");
        Render("ring-3-bt-profile.png", ring, c =>
        {
            c.SetHover("cmd:BT_SEL", animate: false);
            c.SetHover("prof:BT_SEL:2", animate: false);
        });
    }

    /// <summary>The keycode step of <c>&amp;kp LC(LS(T))</c> — ten group
    /// sectors plus the inner modifier ring — with Letters hovered.</summary>
    [AvaloniaFact]
    public void Ring4_KeycodeGroupsAndModifiers()
    {
        var ring = BuildRing(keyIndex: 5);
        ring.GoToCrumb(2);
        Render("ring-4-keycode-mods.png", ring, c => c.SetHover("keys:Letters", animate: false));
    }

    /// <summary>The summary of <c>&amp;lt NAV ESC</c> exactly as the key
    /// opened: no hover, no choice.</summary>
    [AvaloniaFact]
    public void Ring5_Summary()
    {
        Render("ring-5-summary.png", BuildRing(keyIndex: 1), _ => { });
    }

    /// <summary>Opened on <c>&amp;lt NAV ESC</c>, switched to <c>&amp;mt</c>
    /// (its first parameter has no value): incomplete, on the keycode step,
    /// with the Modifiers group hovered.</summary>
    [AvaloniaFact]
    public void Ring6_IncompleteAfterSwitchToMt()
    {
        var ring = BuildRing(keyIndex: 1);
        ring.ChooseId("beh:&mt");
        Render("ring-6-incomplete.png", ring, c => c.SetHover("keys:Modifiers", animate: false));
    }

    /// <summary>The command step of <c>&amp;rgb_ug RGB_TOG</c>: thirteen
    /// commands on the ring, RGB_BRI hovered.</summary>
    [AvaloniaFact]
    public void Ring7_RgbThirteenCommands()
    {
        var ring = BuildRing(keyIndex: 6);
        ring.GoToCrumb(2);
        Render("ring-7-rgb-13.png", ring, c => c.SetHover("cmd:RGB_BRI", animate: false));
    }

    /// <summary>The keyboard cursor on the Mouse category with its band
    /// pinned open.</summary>
    [AvaloniaFact]
    public void Ring8_KeyboardHighlightAndPinnedBand()
    {
        Render("ring-8-keyboard.png", BuildRing(keyIndex: 2), c =>
        {
            c.SetKeyboardHighlight("cat:Mouse");
            c.PinBand("cat:Mouse");
        });
    }

    /// <summary>The summary ring drawn at the smallest allowed scale.</summary>
    [AvaloniaFact]
    public void Ring9_Scaled()
    {
        Render("ring-9-scaled.png", BuildRing(keyIndex: 1), _ => { }, scale: 0.75);
    }

    /// <summary>The side-by-side page the reviewer opens: every snapshot at
    /// 1:1 in a wrapping grid with its file name as caption, plus a link to
    /// the visual reference. Written from <see cref="SnapshotFiles"/>, so it
    /// shows every name whether or not the picture exists yet.</summary>
    [AvaloniaFact]
    public void Index_WritesReviewPage()
    {
        var html = new System.Text.StringBuilder();
        html.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append("<title>Key ring snapshots</title>\n<style>\n");
        html.Append("body { background: #151521; color: #CDD6F4; font-family: system-ui, sans-serif; padding: 24px; }\n");
        html.Append("main { display: flex; flex-wrap: wrap; gap: 24px; }\n");
        html.Append("figure { margin: 0; }\n");
        html.Append("img { display: block; }\n");
        html.Append("figcaption { margin-top: 6px; font-size: 13px; }\n");
        html.Append("a { color: #E8B45A; }\n");
        html.Append("</style>\n</head>\n<body>\n");
        html.Append("<p>Visual reference: <a href=\"../lviz-key-ring-reference.html\">lviz-key-ring-reference.html</a></p>\n");
        html.Append("<main>\n");
        foreach (var file in SnapshotFiles)
        {
            html.Append("<figure><img src=\"").Append(file)
                .Append("\"><figcaption>").Append(file).Append("</figcaption></figure>\n");
        }
        html.Append("</main>\n</body>\n</html>\n");

        var path = SnapshotPaths.File("index.html");
        System.IO.File.WriteAllText(path, html.ToString());
        Assert.True(new FileInfo(path).Length > 0);
    }

    /// <summary>Hosts <paramref name="ring"/> centred in a dark window, runs
    /// <paramref name="setUp"/> on the control, captures the frame and saves
    /// it as <c>docs/ideas/ring-snapshots/{file}</c>. With <paramref name="scale"/>
    /// not 1 the control is scaled about the ring centre, like the placement's
    /// smallest allowed scale.</summary>
    private static void Render(
        string file, KeyRingViewModel ring, Action<KeyRingControl> setUp, double scale = 1)
    {
        var control = new KeyRingControl
        {
            Ring = ring,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (scale != 1)
        {
            control.RenderTransform = new ScaleTransform(scale, scale);
            control.RenderTransformOrigin =
                new RelativePoint(KeyRingControl.RingCentre, RelativeUnit.Absolute);
        }

        var window = new Window
        {
            Width = 560,
            Height = 600,
            Background = new SolidColorBrush(Color.Parse("#1E1E2E")),
            Content = control,
        };
        window.Show();

        setUp(control);

        // setUp may have started the 90/120 ms hover and band ramps (PinBand
        // and SetKeyboardHighlight always animate), which only advance on
        // render-timer ticks. Give the animation its full duration with real
        // time between forced ticks, so every snapshot shows the settled
        // state instead of a mid-fade frame.
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

        window.Close();
    }

    private static KeyRingViewModel BuildRing(int keyIndex)
    {
        var config = ZmkKeymapLoader.LoadFromText(Keymap, "test");
        var defines = Preprocessor.CollectDefines(Keymap);
        var request = new KeyDialogRequest(config, defines, LayerIndex: 0, KeyIndex: keyIndex);
        return new KeyRingViewModel(new KeyDialogViewModel(request), request);
    }
}
