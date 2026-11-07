using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LViz.App.Ring;
using LViz.App.ViewModels;
using LViz.Core.Diagnostics;
// Avalonia.Input also declares a KeyBinding, so the model type is aliased.
using ZmkKeyBinding = LViz.Core.Models.KeyBinding;

namespace LViz.App.Views;

/// <summary>
/// The window-independent half of opening the user's key editor: the screen
/// math, the <see cref="KeyRingWindow"/> lifetime, and the modal
/// <see cref="KeyDialog"/>. <see cref="MainWindow"/> hosts the ring through
/// this (keeping its scrim, gold outline and resize cancel for itself), and
/// any other window — e.g. the combo dialog — opens the same editor on one of
/// its own controls via <see cref="EditAsync"/>.
/// </summary>
public static class KeyEditorHost
{
    /// <summary>Pure: a screen-pixel rect as DIPs inside a working area.</summary>
    public static (Size Area, Rect Anchor) ToWorkArea(
        PixelRect workArea, double scaling, PixelPoint topLeft, PixelPoint bottomRight)
    {
        Point Dip(PixelPoint p) => new((p.X - workArea.X) / scaling, (p.Y - workArea.Y) / scaling);
        return (
            new Size(workArea.Width / scaling, workArea.Height / scaling),
            new Rect(Dip(topLeft), Dip(bottomRight)));
    }

    /// <summary>Shows the ring for <paramref name="ring"/> anchored on the screen
    /// rect, owned by <paramref name="owner"/>. Null when there is no screen or the
    /// ring doesn't fit (the caller shows the dialog); otherwise how it closed.
    /// <paramref name="cancel"/> closes it as a cancel (Result null, OpenInDialog false).</summary>
    public static async Task<KeyRingClosedEventArgs?> ShowRingAsync(
        Window owner, KeyRingViewModel ring, PixelPoint anchorTopLeft, PixelPoint anchorBottomRight,
        CancellationToken cancel = default)
    {
        var screens = owner.Screens;
        var midX = (anchorTopLeft.X + anchorBottomRight.X) / 2;
        var midY = (anchorTopLeft.Y + anchorBottomRight.Y) / 2;
        var screen = screens.ScreenFromPoint(new PixelPoint(midX, midY))
                     ?? screens.ScreenFromWindow(owner)
                     ?? screens.Primary;
        if (screen is null) return null;

        var (area, anchor) = ToWorkArea(screen.WorkingArea, screen.Scaling, anchorTopLeft, anchorBottomRight);
        var placement = RingGeometry.Place(new Rect(area), anchor.Center);
        if (!placement.Fits) return null;

        var tcs = new TaskCompletionSource<KeyRingClosedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var ringWindow = new KeyRingWindow
        {
            Position = screen.WorkingArea.Position,
            Width = area.Width,
            Height = area.Height,
        };
        ringWindow.Present(ring, area, anchor, placement);
        ringWindow.Finished += (_, closed) => tcs.TrySetResult(closed);

        using var registration = cancel.CanBeCanceled
            ? cancel.Register(() => tcs.TrySetResult(new KeyRingClosedEventArgs(null, openInDialog: false)))
            : default;

        try
        {
            ringWindow.Show(owner);
            return await tcs.Task;
        }
        finally
        {
            ringWindow.Close();
        }
    }

    /// <summary>The key dialog on <paramref name="dialog"/>, modal over <paramref name="owner"/>; re-activates the owner after.</summary>
    public static async Task<ZmkKeyBinding?> ShowDialogAsync(Window owner, KeyDialogViewModel dialog)
    {
        try
        {
            return await new KeyDialog { DataContext = dialog }.ShowDialog<ZmkKeyBinding?>(owner);
        }
        finally
        {
            owner.Activate();
        }
    }

    /// <summary>The user's key editor for <paramref name="request"/>, anchored on
    /// <paramref name="anchor"/> (a control in <paramref name="owner"/>): the ring
    /// when <paramref name="useRing"/> and it fits, else the dialog. A ring that
    /// hands over continues in the dialog on the same VM. Null on cancel.
    /// Exceptions are logged (DiagnosticLog.Error "KeyRing") and answer null.</summary>
    public static Task<ZmkKeyBinding?> EditAsync(
        Window owner, Control anchor, KeyDialogRequest request, bool useRing)
    {
        // The anchor's bounds in screen pixels, like the main window takes
        // its key rect: PointToScreen of the two corners.
        var bounds = anchor.Bounds;
        return EditAsync(
            owner,
            request,
            useRing
                ? new KeyEditorRingAnchor(
                    anchor.PointToScreen(bounds.TopLeft), anchor.PointToScreen(bounds.BottomRight))
                : null);
    }

    /// <summary>The user's key editor for <paramref name="request"/>: the ring on
    /// <paramref name="ring"/> when given and it fits, else the dialog. A ring
    /// that hands over (or has no room) continues in the dialog on the same VM.
    /// Null on cancel. Exceptions are logged (DiagnosticLog.Error "KeyRing") and
    /// answer null. The one place that decides ring versus dialog.</summary>
    public static async Task<ZmkKeyBinding?> EditAsync(
        Window owner, KeyDialogRequest request, KeyEditorRingAnchor? ring)
    {
        try
        {
            var dialogVm = new KeyDialogViewModel(request);
            if (ring is null) return await ShowDialogAsync(owner, dialogVm);

            var closed = await ShowRingAsync(
                owner,
                new KeyRingViewModel(dialogVm, request),
                ring.TopLeft,
                ring.BottomRight,
                ring.Cancel);

            // Null: no screen, or the ring doesn't fit there — the dialog is
            // the user's editor. OpenInDialog: the ring's hand-over, so the
            // same dialog VM continues the half-made edit.
            if (closed is null || closed.OpenInDialog)
            {
                ring.BeforeDialog?.Invoke();
                return await ShowDialogAsync(owner, dialogVm);
            }

            owner.Activate();
            return closed.Result;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("KeyRing", $"Key editor failed: {ex}");
            return null;
        }
    }
}

/// <summary>Where <see cref="KeyEditorHost.EditAsync(Window, KeyDialogRequest, KeyEditorRingAnchor?)"/>
/// puts the ring: the anchor's screen-pixel corners, a token that closes the
/// ring as a cancel, and a hook that runs when the editor continues in the
/// dialog instead (the owner takes its ring decorations down there).</summary>
public sealed record KeyEditorRingAnchor(
    PixelPoint TopLeft,
    PixelPoint BottomRight,
    CancellationToken Cancel = default,
    Action? BeforeDialog = null);
