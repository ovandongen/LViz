using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using LViz.App.ViewModels;
using LViz.App.Views;
using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Rendering;

/// <summary>
/// <see cref="KeyRingOverlay"/> hosts the ring over a window: scrim up and key
/// outline placed while it is open, scrim down and the open flag cleared when
/// it ends, and a scrim press or a window resize cancels it.
/// </summary>
public class KeyRingOverlayTests
{
    private const string Keymap = """
        / {
            keymap {
                compatible = "zmk,keymap";
                Base { bindings = < &kp A &kp B >; };
            };
        };
        """;

    private static (Window Window, KeyRingOverlay Overlay) Host()
    {
        var overlay = new KeyRingOverlay();
        var window = new Window { Width = 600, Height = 400, Content = new Grid { Children = { overlay } } };
        overlay.BoardRoot = window;
        window.Show();
        return (window, overlay);
    }

    private static KeyDialogRequest Request()
    {
        var config = ZmkKeymapLoader.LoadFromText(Keymap, "test");
        return new KeyDialogRequest(config, Preprocessor.CollectDefines(Keymap), LayerIndex: 0, KeyIndex: 0);
    }

    private static async Task Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    [AvaloniaFact]
    public async Task Open_ShowsScrimAndOutline_ThenResizeCancelsAndHidesIt()
    {
        var (window, overlay) = Host();
        Assert.False(overlay.IsVisible);
        Assert.False(overlay.IsOpen);

        var edit = overlay.EditKeyAsync(Request(), useRing: true);
        await Pump();

        Assert.True(overlay.IsOpen);
        Assert.True(overlay.IsVisible);
        // Key not on the board: a zero rect at the centre, inflated by 3 each side.
        var outline = overlay.FindControl<Border>("KeyOutline")!;
        Assert.Equal(6, outline.Width);

        window.Width = 640;
        await Pump();
        var result = await edit;

        Assert.Null(result);
        Assert.False(overlay.IsOpen);
        Assert.False(overlay.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ScrimPress_CancelsAndHidesIt()
    {
        var (window, overlay) = Host();
        var edit = overlay.EditKeyAsync(Request(), useRing: true);
        await Pump();
        Assert.True(overlay.IsOpen);

        window.MouseDown(new Point(300, 200), MouseButton.Left);
        window.MouseUp(new Point(300, 200), MouseButton.Left);
        await Pump();

        Assert.Null(await edit);
        Assert.False(overlay.IsOpen);
        Assert.False(overlay.IsVisible);
        window.Close();
    }
}
