using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using LViz.App.Ring;
using LViz.App.ViewModels;
// Avalonia.Input also declares a KeyBinding, so the model type is aliased.
using ZmkKeyBinding = LViz.Core.Models.KeyBinding;

namespace LViz.App.Views;

/// <summary>
/// Hosts the key ring over the main window: dims the board behind a scrim,
/// outlines the edited key in gold, and opens the ring in a
/// <see cref="KeyRingWindow"/> through <see cref="KeyEditorHost"/>. Cancels
/// the ring on a scrim press or a resize of the hosting window.
/// </summary>
public partial class KeyRingOverlay : UserControl
{
    /// <summary>Where the board's key templates are searched: the visual
    /// subtree holding the <see cref="KeyViewModel"/>-bound key Grids.</summary>
    public Visual? BoardRoot { get; set; }

    /// <summary>True while <see cref="EditKeyAsync"/> hosts the ring: the
    /// host pauses its edit shortcuts (the ring owns the keyboard).</summary>
    public bool IsOpen { get; private set; }

    private CancellationTokenSource? _cancel;

    public KeyRingOverlay()
    {
        InitializeComponent();
    }

    /// <summary>A click on the dimmed board cancels the ring. The ring's window
    /// normally covers the board and catches that click itself; this covers a
    /// board outside the screen's working area.</summary>
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsOpen) return;
        _cancel?.Cancel();
        e.Handled = true;
    }

    /// <summary>
    /// Opens the user's key editor for <paramref name="request"/>. With
    /// <paramref name="useRing"/> the board dims, the edited key gets a gold
    /// outline, and the ring opens on the key in a <see cref="KeyRingWindow"/>
    /// over the key's screen, so it is placed against the monitor, not the
    /// window; a resize or a press on the dimmed board cancels it. Without it
    /// (or when the ring has no room, or hands over) the key dialog opens. The
    /// ring-versus-dialog fallback itself is <see cref="KeyEditorHost"/>'s.
    /// Answers the new binding, or null on cancel. The caller (the edit view-model)
    /// allows one editor at a time.
    /// </summary>
    public async Task<ZmkKeyBinding?> EditKeyAsync(KeyDialogRequest request, bool useRing)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            throw new InvalidOperationException("The key ring overlay is not attached to a window.");
        if (!useRing) return await KeyEditorHost.EditAsync(owner, request, null);

        // Whatever happens on the way, the scrim comes down and the edit
        // shortcuts come back; otherwise one exception leaves the board
        // dimmed and the keyboard dead until restart.
        IsOpen = true;
        var cts = new CancellationTokenSource();
        _cancel = cts;
        EventHandler<SizeChangedEventArgs> sizeCancel = (_, _) => cts.Cancel();
        try
        {
            // A collapsed panel has no bounds, so show the layer before finding
            // the key in it.
            IsVisible = true;
            owner.UpdateLayout();
            var key = FindKeyRect(request.KeyIndex, Bounds.Size);

            // The ring's window covers the working area of the key's screen;
            // the host turns these screen-pixel corners into DIPs inside it.
            var keyTopLeft = this.PointToScreen(key.TopLeft);
            var keyBottomRight = this.PointToScreen(key.BottomRight);

            // The board stays dimmed behind the ring, its key outlined in gold.
            // The host's placement never moves the key, so the outline is set
            // from the key rect before the ring opens.
            var outline = key.Inflate(3);
            Canvas.SetLeft(KeyOutline, outline.X);
            Canvas.SetTop(KeyOutline, outline.Y);
            KeyOutline.Width = outline.Width;
            KeyOutline.Height = outline.Height;

            // A resize moves the board under the outline: cancel. A press on
            // the dimmed board cancels through the same source
            // (OnScrimPressed).
            owner.SizeChanged += sizeCancel;

            return await KeyEditorHost.EditAsync(
                owner,
                request,
                new KeyEditorRingAnchor(
                    keyTopLeft, keyBottomRight, cts.Token,
                    BeforeDialog: () => IsVisible = false));
        }
        finally
        {
            owner.SizeChanged -= sizeCancel;
            _cancel = null;
            cts.Dispose();
            IsVisible = false;
            IsOpen = false;
        }
    }

    /// <summary>
    /// The edited key's rotated bounding box in this overlay's coordinates:
    /// the four corners of its template Grid through <c>TranslatePoint</c>,
    /// which folds in the rotation. A zero-size rect at the centre when the key
    /// is not on screen.
    /// </summary>
    private Rect FindKeyRect(int keyIndex, Size area)
    {
        var centre = new Rect(new Point(area.Width / 2, area.Height / 2), new Size(0, 0));

        // Pre-order traversal: the outermost matching Grid — the key template's
        // root, not the label boxes nested in it — comes first.
        var target = BoardRoot?.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(g => g.DataContext is KeyViewModel key && key.Position.Index == keyIndex);
        if (target is null) return centre;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var corner in new[]
                 {
                     new Point(0, 0),
                     new Point(target.Bounds.Width, 0),
                     new Point(target.Bounds.Width, target.Bounds.Height),
                     new Point(0, target.Bounds.Height),
                 })
        {
            if (target.TranslatePoint(corner, this) is not { } p) continue;
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        return minX <= maxX && minY <= maxY
            ? new Rect(minX, minY, maxX - minX, maxY - minY)
            : centre;
    }
}
