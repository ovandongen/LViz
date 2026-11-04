using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Projektanker.Icons.Avalonia;
using Xunit;

namespace LViz.Tests.Rendering;

/// <summary>
/// Gate for the headless rendering setup (batch 020): Skia draws real pixels,
/// Inter and the Font Awesome provider are available, and snapshots land in
/// <see cref="SnapshotPaths.Folder"/>.
/// </summary>
public class HeadlessRenderSmokeTests
{
    [AvaloniaFact]
    public void Window_RendersPixels_AndSavesPng()
    {
        var window = new Window
        {
            Width = 160,
            Height = 60,
            Background = Brushes.Black,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#E8B45A")),
                Margin = new Avalonia.Thickness(10),
                Child = new TextBlock { Text = "LViz ring", Foreground = Brushes.Black, Margin = new Avalonia.Thickness(6) },
            },
        };
        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(160, frame!.PixelSize.Width);

        var path = SnapshotPaths.File("smoke.png");
        frame.Save(path);
        Assert.True(new FileInfo(path).Length > 0);

        window.Close();
    }

    [AvaloniaFact]
    public void FontAwesomeIcon_ResolvesToPathData()
    {
        var icon = IconProvider.Current.GetIcon("fa-solid fa-keyboard");
        // PathModel converts implicitly to its SVG path data; its ToString()
        // is the record printout, not the data.
        string data = icon.Path;
        Assert.False(string.IsNullOrEmpty(data));
        Assert.True(icon.ViewBox.Width > 0);
    }
}
