namespace LViz.App.Services;

/// <summary>The layer the main window is showing. Written by the main
/// view-model on the UI thread, read by the layer-push engines on theirs.</summary>
public sealed class ShownLayer
{
    private volatile int _index;
    public int Index { get => _index; set => _index = value; }
}
