using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using LViz.App.ViewModels;
using LViz.App.Views;
using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Rendering;

/// <summary>
/// The key ring control renders under the headless Skia app: with a
/// view-model it paints a real frame (saved to
/// <see cref="SnapshotPaths.File"/> for review), without one it draws
/// nothing, and it measures exactly the ring frame.
/// </summary>
public class KeyRingControlRenderTests
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

    [AvaloniaFact]
    public void Render_WithRing_CapturesAndSavesPng()
    {
        var control = new KeyRingControl { Ring = BuildRing() };
        var window = new Window
        {
            Width = 520,
            Height = 560,
            Background = new SolidColorBrush(Color.Parse("#1E1E2E")),
            Content = control,
        };
        window.Show();

        control.SetHover("cat:Layer", animate: false);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var path = SnapshotPaths.File("ring-smoke.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0);

        window.Close();
    }

    [AvaloniaFact]
    public void Render_WithoutRing_DrawsNothingAndDoesNotThrow()
    {
        var control = new KeyRingControl();
        var window = new Window
        {
            Width = 520,
            Height = 560,
            Content = control,
        };
        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        window.Close();
    }

    [AvaloniaFact]
    public void Measure_ReturnsTheRingFrame()
    {
        var control = new KeyRingControl { Ring = BuildRing() };

        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.Equal(448, control.DesiredSize.Width);
        Assert.Equal(514, control.DesiredSize.Height);
    }

    private static KeyRingViewModel BuildRing()
    {
        var config = ZmkKeymapLoader.LoadFromText(Keymap, "test");
        var defines = Preprocessor.CollectDefines(Keymap);
        var request = new KeyDialogRequest(config, defines, LayerIndex: 0, KeyIndex: 0);
        return new KeyRingViewModel(new KeyDialogViewModel(request), request);
    }
}
