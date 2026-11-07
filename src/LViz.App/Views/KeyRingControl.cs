using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LViz.App.Ring;
using LViz.App.ViewModels;
using LViz.Core.Diagnostics;

namespace LViz.App.Views;

/// <summary>
/// The radial key editor's view: one custom-drawn control that shows a
/// <see cref="KeyRingViewModel"/> as a ring — backdrop, crumb row, main ring,
/// inner modifier ring, behavior band, centre disc and hint pill. It owns the
/// view state (hover, pinned band, keyboard cursor, animation) and the input;
/// where things sit is <see cref="RingLayout"/> and how they look is
/// <see cref="KeyRingPainter"/>.
///
/// Pointer input hits through <see cref="HitTest"/> — hover, click to choose
/// or pin a band, right-click back — and a keyboard cursor moves with
/// <see cref="MoveKeyboardHighlight"/>; the host listens to
/// <see cref="SaveRequested"/> and <see cref="Dismissed"/>. Without a
/// <see cref="Ring"/> it draws nothing and hit-tests to null, and it never
/// throws from <see cref="Render"/> — a broken segment must not freeze the
/// window.
/// </summary>
public sealed class KeyRingControl : Control
{
    private const double HoverAnimationMs = 90;
    private const double BandAnimationMs = 120;

    /// <summary>The ring centre in control-local coordinates; <see cref="MeasureOverride"/>
    /// is built so this is exactly the centre of the ring.</summary>
    public static Point RingCentre => RingLayout.Centre;

    // ─── view-model wiring ───────────────────────────────────────────────

    public static readonly StyledProperty<KeyRingViewModel?> RingProperty =
        AvaloniaProperty.Register<KeyRingControl, KeyRingViewModel?>(nameof(Ring));

    public static readonly StyledProperty<bool> HideHintProperty =
        AvaloniaProperty.Register<KeyRingControl, bool>(nameof(HideHint));

    /// <summary>The view-model to draw. Null draws nothing.</summary>
    public KeyRingViewModel? Ring
    {
        get => GetValue(RingProperty);
        set => SetValue(RingProperty, value);
    }

    /// <summary>Suppresses the hint pill (set when the hint would cover
    /// the edited key).</summary>
    public bool HideHint
    {
        get => GetValue(HideHintProperty);
        set => SetValue(HideHintProperty, value);
    }

    static KeyRingControl()
    {
        RingProperty.Changed.AddClassHandler<KeyRingControl>((c, _) => c.OnRingChanged());
        HideHintProperty.Changed.AddClassHandler<KeyRingControl>((c, _) => c.InvalidateVisual());
    }

    public KeyRingControl()
    {
        ClipToBounds = false;
        Effect = new DropShadowEffect
        {
            OffsetX = 0,
            OffsetY = 10,
            BlurRadius = 28,
            Color = Color.FromArgb(128, 0, 0, 0),
        };
    }

    private KeyRingViewModel? _subscribed;

    private void OnRingChanged()
    {
        if (!ReferenceEquals(_subscribed, Ring))
        {
            if (_subscribed is not null) _subscribed.PropertyChanged -= OnRingViewModelChanged;
            _subscribed = Ring;
            if (_subscribed is not null) _subscribed.PropertyChanged += OnRingViewModelChanged;
        }

        RefreshFromRing();
    }

    private void OnRingViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshFromRing();
    }

    /// <summary>Rebuilds the layout for the view-model's new state, drops view
    /// ids the change removed (a step change starts clean) and re-applies the
    /// band owner and hover animation.</summary>
    private void RefreshFromRing()
    {
        RebuildLayout();
        PruneRemovedIds();
        ApplyView(animate: true);
    }

    /// <summary>Clears the hovered, pinned or highlighted key when the rebuilt
    /// layout no longer holds it.</summary>
    private void PruneRemovedIds()
    {
        if (HoverId is { } hover && _layout.Find(hover) is null) HoverId = null;
        if (PinnedBandId is { } pinned && _layout.Find(pinned) is null) PinnedBandId = null;
        if (KeyboardHighlightId is { } keyboard && _layout.Find(keyboard) is null) KeyboardHighlightId = null;
    }

    // ─── view state (pointer and keyboard land here) ─────────────────────

    /// <summary>Stable key (<see cref="RingHit.Key"/>) of the hovered segment
    /// or band item, the centre or a crumb; null when nothing is.</summary>
    public string? HoverId { get; private set; }

    /// <summary>The main segment whose band stays open without a hover.</summary>
    public string? PinnedBandId { get; private set; }

    /// <summary>The key the keyboard cursor is on; tints like a hover but
    /// never owns the band.</summary>
    public string? KeyboardHighlightId { get; private set; }

    /// <summary>The segment the pointer is on, else the one the keyboard
    /// cursor is on; the centre shows its full label and sub. Null over the
    /// centre, a crumb or nothing.</summary>
    internal KeyRingSegment? PointedSegment =>
        (HoverId ?? KeyboardHighlightId) is { } id && _layout.Find(id) is RingHit.Segment hit
            ? hit.Value
            : null;

    /// <summary>Points the ring at <paramref name="id"/> (see
    /// <see cref="HoverId"/>). With <c>animate: false</c> the hover amounts
    /// jump straight to their targets.</summary>
    public void SetHover(string? id, bool animate = true)
    {
        if (HoverId == id) return;
        HoverId = id;
        ApplyView(animate);
    }

    /// <summary>Keeps the band of the main segment <paramref name="id"/> open
    /// while nothing hovers a band-owning segment; null unpins.</summary>
    public void PinBand(string? id)
    {
        if (PinnedBandId == id) return;
        PinnedBandId = id;
        ApplyView(animate: true);
    }

    /// <summary>Moves the keyboard highlight (a segment or band id; null
    /// clears it).</summary>
    public void SetKeyboardHighlight(string? id)
    {
        if (KeyboardHighlightId == id) return;
        KeyboardHighlightId = id;
        ApplyView(animate: true);
    }

    private void ApplyView(bool animate)
    {
        var owner = ComputeBandOwner();
        if (owner != _bandOwner)
        {
            _bandOwner = owner;
            RebuildLayout();
            _bandOpacity = 0;
        }

        if (animate)
        {
            if (HoverId is { } h) _amounts.TryAdd(h, 0);
            if (KeyboardHighlightId is { } k) _amounts.TryAdd(k, 0);
            StartAnimation();
        }
        else
        {
            SnapAmounts();
        }

        InvalidateVisual();
    }

    /// <summary>The open band's owner: the hovered main segment when it has a
    /// band; else the current owner while the pointer is on one of its band
    /// items; else the pinned one — and none when that owner has no band.</summary>
    private string? ComputeBandOwner()
    {
        if (Ring is not { } vm) return null;

        if (HoverId is { } hover && _layout.MainSlot(hover) is { } hovered
            && vm.BandFor(hovered.Segment.Target).Count > 0)
        {
            return hover;
        }

        if (_bandOwner is { } owner
            && HoverId is { } onBand
            && _layout.BandSegments.Any(segment => segment.Id == onBand)
            && _layout.MainSlot(owner) is { } ownerSlot
            && vm.BandFor(ownerSlot.Segment.Target).Count > 0)
        {
            return owner;
        }

        if (PinnedBandId is { } pinned && _layout.MainSlot(pinned) is { } pinnedSlot
            && vm.BandFor(pinnedSlot.Segment.Target).Count > 0)
        {
            return pinned;
        }

        return null;
    }

    // ─── layout and hit-testing (the geometry is RingLayout) ─────────────

    private RingLayout _layout = RingLayout.Empty;
    private string? _bandOwner;
    private double _bandOpacity;

    private void RebuildLayout()
    {
        _layout = Ring is { } vm
            ? RingLayout.Build(vm, _bandOwner, KeyRingPainter.Measure)
            : RingLayout.Empty;
        _bandOwner = _layout.BandOwnerKey;
    }

    /// <summary>Centre of each open band item in control coordinates, in item
    /// order — the point a hit-test lands on for that item. Visible to
    /// LViz.Tests (InternalsVisibleTo) for its input tests.</summary>
    internal IReadOnlyList<(string Id, Point Centre)> BandItemCentres => _layout.BandItemCentres();

    /// <summary>The crumb boxes in control coordinates, in crumb order.
    /// Visible to LViz.Tests (InternalsVisibleTo) for its input tests.</summary>
    internal IReadOnlyList<Rect> CrumbBounds => [.. _layout.CrumbSlots.Select(slot => slot.Bounds)];

    /// <summary>What is under a control-local point (see
    /// <see cref="RingLayout.HitTest"/>); null without a ring.</summary>
    internal RingHit? HitTest(Point p) => _layout.HitTest(p);

    /// <summary>Whether a control-local point lies on anything the ring owns
    /// (see <see cref="RingLayout.IsOverRing"/>), the hint pill included while
    /// it shows. A left-click here never dismisses.</summary>
    public bool IsOverRing(Point p) =>
        Ring is { } vm && _layout.IsOverRing(p, KeyRingPainter.HintBounds(vm, HideHint));

    // ─── pointer and keyboard input ──────────────────────────────────────

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

    /// <summary>The ring can close and the user asked to save: a click on the
    /// centre disc, or the second click of a double-click whose first click
    /// picked a value. The host writes the binding.</summary>
    public event EventHandler? SaveRequested;

    /// <summary>Whether the previous left press picked a value; the second
    /// press of a double-click then finishes instead of hitting again (the
    /// ring under the pointer has usually moved on to the next step).</summary>
    private bool _lastPressPicked;

    /// <summary>A left-click landed outside ring, crumbs and hint: cancel.</summary>
    public event EventHandler? Dismissed;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var hit = HitTest(e.GetPosition(this));
        if (hit is not null) SetHover(hit.Key); // a miss keeps the last hover (dead zone)
        Cursor = hit is not null ? HandCursor : ArrowCursor;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetCurrentPoint(this);

        // Right-click goes back one step wherever it lands.
        if (point.Properties.IsRightButtonPressed)
        {
            Ring?.Back();
            PinBand(null);
            e.Handled = true;
            return;
        }

        var lastPressPicked = _lastPressPicked;
        _lastPressPicked = false;

        if (!point.Properties.IsLeftButtonPressed) return;
        e.Handled = true;

        // A double-click on a value is "pick it and save": the first click
        // picked, the second saves once the binding is complete (else does
        // nothing, rather than picking whatever the next step put there).
        if (e.ClickCount >= 2 && lastPressPicked)
        {
            if (Ring is { CanClose: true }) SaveRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var p = point.Position;
        if (HitTest(p) is { } hit) _lastPressPicked = ApplyClick(hit);
        else if (!IsOverRing(p)) Dismissed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What a left-click on this hit does: the centre raises
    /// <see cref="SaveRequested"/> when closable; a crumb jumps there; a main
    /// segment with a band toggles the pin; anything else is chosen and the
    /// view starts clean. Returns whether the click picked a value (see
    /// <see cref="KeyRingTarget.PicksAValue"/>).</summary>
    private bool ApplyClick(RingHit hit)
    {
        if (Ring is not { } vm) return false;

        switch (hit)
        {
            case RingHit.Center:
                if (vm.CanClose) SaveRequested?.Invoke(this, EventArgs.Empty);
                return false;

            case RingHit.Crumb crumb:
                vm.GoToCrumb(crumb.Index);
                PinBand(null);
                return false;

            case RingHit.Segment { Value: var segment } when
                _layout.MainSlot(segment.Id) is not null && vm.BandFor(segment.Target).Count > 0:
                PinBand(PinnedBandId == segment.Id ? null : segment.Id);
                return false;

            case RingHit.Segment { Value: var segment }:
                var picked = vm.Choose(segment.Target) && segment.Target.PicksAValue;
                PinBand(null);
                SetKeyboardHighlight(null);
                return picked;

            default:
                return false;
        }
    }

    /// <summary>Moves the keyboard cursor. A highlighted item of an open band
    /// steps through the band items; otherwise the main ring steps by
    /// <paramref name="delta"/> (clockwise for +1), skipping disabled
    /// segments, starting from the current highlight, else the current-value
    /// segment, else index 0. A highlighted main segment with a band pins it,
    /// one without clears the pin.</summary>
    public void MoveKeyboardHighlight(int delta)
    {
        if (Ring is not { } vm) return;

        // A highlighted band item steps through its band; IndexOfBandItem
        // hits only while that band is open.
        if (KeyboardHighlightId is { } highlighted
            && IndexOfBandItem(highlighted) is { } bandIndex)
        {
            var band = _layout.BandSegments;
            var count = band.Count;
            var next = ((bandIndex + delta) % count + count) % count;
            SetKeyboardHighlight(band[next].Id);
            return;
        }

        var mains = new List<KeyRingSegment>();
        foreach (var slot in _layout.Slots)
        {
            if (slot.Layer == RingLayer.Main) mains.Add(slot.Segment);
        }

        if (mains.Count == 0) return;

        var at = KeyboardHighlightId is { } current
            ? mains.FindIndex(segment => segment.Id == current)
            : -1;
        if (at < 0) at = mains.FindIndex(segment => segment.IsCurrent);
        if (at < 0) at = 0;

        var index = ((at + delta) % mains.Count + mains.Count) % mains.Count;
        var skipped = 0;
        while (skipped < mains.Count && !mains[index].IsEnabled)
        {
            index = (index + 1) % mains.Count;
            skipped++;
        }

        if (skipped >= mains.Count) return; // nothing enabled to highlight

        var segment = mains[index];
        SetKeyboardHighlight(segment.Id);
        PinBand(vm.BandFor(segment.Target).Count > 0 ? segment.Id : null);
    }

    /// <summary>Steps the cursor from a highlighted main segment into the
    /// first item of its open band; without one it does nothing.</summary>
    public void MoveIntoBand()
    {
        if (_bandOwner is null || KeyboardHighlightId != _bandOwner || _layout.BandSegments.Count == 0) return;
        SetKeyboardHighlight(_layout.BandSegments[0].Id);
    }

    /// <summary>Does what a left-click on the highlighted segment does; false
    /// when nothing is highlighted.</summary>
    public bool ActivateKeyboardHighlight()
    {
        if (KeyboardHighlightId is not { } id || _layout.Find(id) is not { } hit) return false;
        ApplyClick(hit);
        return true;
    }

    private int? IndexOfBandItem(string id)
    {
        var band = _layout.BandSegments;
        for (var i = 0; i < band.Count; i++)
        {
            if (band[i].Id == id) return i;
        }

        return null;
    }

    // ─── hover amounts and animation ─────────────────────────────────────

    private readonly Dictionary<string, double> _amounts = [];
    private bool _animating;
    private TimeSpan _lastFrame;

    private double AmountOf(string id) => _amounts.TryGetValue(id, out var value) ? value : 0;

    private double TargetOf(string id) =>
        id == HoverId || id == KeyboardHighlightId ? 1 : 0;

    private void StartAnimation()
    {
        if (_animating) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            // No frame source while detached: land on the targets directly.
            SnapAmounts();
            return;
        }

        _animating = true;
        _lastFrame = TimeSpan.MinValue;
        top.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan time)
    {
        var dt = _lastFrame == TimeSpan.MinValue
            ? 0
            : Math.Clamp((time - _lastFrame).TotalMilliseconds, 0, 100);
        _lastFrame = time;

        var moving = false;

        if (_amounts.Count > 0)
        {
            List<string>? settled = null;
            foreach (var (id, value) in _amounts)
            {
                var target = TargetOf(id);
                if (value == target) continue;

                var next = MoveTowards(value, target, dt / HoverAnimationMs);
                if (next == target)
                {
                    (settled ??= []).Add(id);
                }
                else
                {
                    _amounts[id] = next;
                    moving = true;
                }
            }

            if (settled is not null)
            {
                foreach (var id in settled)
                {
                    if (TargetOf(id) == 0) _amounts.Remove(id);
                }
            }
        }

        if (_bandOwner is not null && _bandOpacity < 1)
        {
            _bandOpacity = Math.Min(1, _bandOpacity + dt / BandAnimationMs);
            moving = true;
        }

        InvalidateVisual();

        if (moving)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
        }
        else
        {
            _animating = false;
        }
    }

    private static double MoveTowards(double value, double target, double step) =>
        value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

    private void SnapAmounts()
    {
        _amounts.Clear();
        if (HoverId is { } hover) _amounts[hover] = 1;
        if (KeyboardHighlightId is { } key) _amounts[key] = 1;
        _bandOpacity = _bandOwner is not null ? 1 : 0;
    }

    // ─── size ────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize) =>
        new(RingMetrics.FrameLeft + RingMetrics.FrameRight,
            RingMetrics.FrameTop + RingMetrics.FrameBottom);

    // ─── drawing ─────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // A frame-wide transparent fill makes the whole control hit-testable,
        // so a click outside the drawn disc still reaches it — IsOverRing
        // decides whether that dismisses.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        if (Ring is not { } vm) return;

        try
        {
            new KeyRingPainter(vm, _layout, _amounts, PointedSegment, _bandOpacity, HideHint).Paint(context);
        }
        catch (Exception ex)
        {
            // An exception inside Render freezes the window; log and draw a
            // partial ring instead.
            DiagnosticLog.Error("KeyRing", $"Render failed: {ex}");
        }
    }
}
