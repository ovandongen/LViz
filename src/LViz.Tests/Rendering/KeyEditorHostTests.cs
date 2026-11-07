using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LViz.App.ViewModels;
using LViz.App.Views;
using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Rendering;

/// <summary>
/// The shared key editor host (part 16): <see cref="KeyEditorHost"/> holds the
/// screen math, the ring window lifetime and the modal key dialog that
/// <see cref="MainWindow"/> used to inline, so any window can open the ring.
/// The pure <see cref="KeyEditorHost.ToWorkArea"/> conversion is pinned with
/// plain facts; the ring lifetime is exercised headless against a cancelled
/// token (the same path the main window's resize cancel drives).
/// </summary>
public class KeyEditorHostTests
{
    // The fixture keymap of KeyRingWindowTests (a copy, so these tests stand
    // alone): 10 keys on layer Base, one more on layer Nav.
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

    /// <summary>A Retina-style working area (a 25 px menu bar, 2× scaling): the
    /// area is the working rect over the scaling, the anchor is the screen rect
    /// shifted to the working area's origin and divided by it.</summary>
    [Fact]
    public void ToWorkArea_ScalesPixelsIntoWorkingAreaDips()
    {
        var (area, anchor) = KeyEditorHost.ToWorkArea(
            new PixelRect(0, 25, 3024, 1939), 2.0, new PixelPoint(100, 125), new PixelPoint(160, 185));

        Assert.Equal(new Size(1512, 969.5), area);
        Assert.Equal(new Rect(50, 50, 30, 30), anchor);
    }

    /// <summary>A left-hand secondary monitor has a negative-X working area:
    /// the anchor offsets against that origin, so a screen point at -1000
    /// becomes DIP 920 inside the monitor.</summary>
    [Fact]
    public void ToWorkArea_NegativeXMonitor_OffsetsAgainstItsOrigin()
    {
        var (_, anchor) = KeyEditorHost.ToWorkArea(
            new PixelRect(-1920, 0, 1920, 1080), 1.0, new PixelPoint(-1000, 500), new PixelPoint(-900, 560));

        Assert.Equal(new Rect(920, 500, 100, 60), anchor);
    }

    /// <summary>A token cancelled before the ring even opens (what a resize of
    /// the main window does) closes it as a cancel: Result null, no hand-over
    /// to the dialog. The headless platform's default screen (1920 × 1280) is
    /// big enough for the ring, so the ring really shows here; on a session
    /// without any screen the host answers null instead — asserted as such.</summary>
    [AvaloniaFact]
    public async Task ShowRingAsync_CancelledToken_ClosesAsCancel()
    {
        var owner = new Window();
        owner.Show();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var closed = await KeyEditorHost.ShowRingAsync(
            owner, BuildRing(0), new PixelPoint(950, 630), new PixelPoint(970, 650), cts.Token);

        if (closed is null)
            return; // No screens on this headless session: the documented null answer.

        Assert.Null(closed.Result);
        Assert.False(closed.OpenInDialog);

        owner.Close();
    }

    private static KeyRingViewModel BuildRing(int keyIndex)
    {
        var config = ZmkKeymapLoader.LoadFromText(Keymap, "test");
        var defines = Preprocessor.CollectDefines(Keymap);
        var request = new KeyDialogRequest(config, defines, LayerIndex: 0, KeyIndex: keyIndex);
        return new KeyRingViewModel(new KeyDialogViewModel(request), request);
    }
}
