using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using LViz.App.Ring;
using LViz.App.ViewModels;
using LViz.Core.Diagnostics;
using Projektanker.Icons.Avalonia;

namespace LViz.App.Views;

/// <summary>
/// The radial key editor's view: one custom-drawn control that paints the
/// whole ring from a <see cref="KeyRingViewModel"/> — backdrop, crumb row,
/// main ring, inner modifier ring, behavior band, centre disc and hint pill.
/// Numbers follow the approved visual reference
/// (docs/ideas/lviz-key-ring-reference.html).
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
    // ─── colours ─────────────────────────────────────────────────────────

    private static readonly SolidColorBrush BackdropBrush = Tint("#181825");
    private static readonly SolidColorBrush SegmentBrush = Tint("#313244");
    private static readonly SolidColorBrush SegmentHoverBrush = Tint("#45475A");
    private static readonly SolidColorBrush BandSegmentBrush = Tint("#2B2C3F");
    private static readonly SolidColorBrush ToggledOnBrush = Tint("#5A4A2E");
    private static readonly SolidColorBrush ToggledOnHoverBrush = Tint("#6A5734");
    private static readonly SolidColorBrush DisabledBrush = Tint("#282939");
    private static readonly SolidColorBrush CenterBrush = Tint("#1E1E2E");
    private static readonly SolidColorBrush CenterStrokeBrush = Tint("#45475A");
    private static readonly SolidColorBrush CenterChangedBrush = Tint("#2C2822");
    private static readonly SolidColorBrush GoldBrush = Tint("#E8B45A");
    private static readonly SolidColorBrush GoldSoftBrush = Tint("#29E8B45A");
    private static readonly SolidColorBrush TextBrush = Tint("#CDD6F4");
    private static readonly SolidColorBrush SubBrush = Tint("#A6ADC8");
    private static readonly SolidColorBrush MutedBrush = Tint("#6C7086");
    private static readonly SolidColorBrush HintBrush = Tint("#EB181825");

    // ─── typefaces ───────────────────────────────────────────────────────

    private static readonly FontFamily MonoFamily = new("Menlo, Consolas, monospace");

    /// <summary>The app's default family (Inter, see Program.cs). A bare
    /// <c>new Typeface()</c> has no family and FontManager throws on it.</summary>
    private static readonly Typeface InterRegular = New(FontFamily.Default, FontWeight.Normal);
    private static readonly Typeface InterMedium = New(FontFamily.Default, FontWeight.Medium);
    private static readonly Typeface InterSemiBold = New(FontFamily.Default, FontWeight.SemiBold);
    private static readonly Typeface MonoRegular = New(MonoFamily, FontWeight.Normal);
    private static readonly Typeface MonoMedium = New(MonoFamily, FontWeight.Medium);
    private static readonly Typeface MonoSemiBold = New(MonoFamily, FontWeight.SemiBold);
    private static readonly Typeface MonoBold = New(MonoFamily, FontWeight.Bold);

    private const double HoverAnimationMs = 90;
    private const double BandAnimationMs = 120;

    /// <summary>Middle of the band, where item widths are measured.</summary>
    private static readonly double BandMidRadius = (RingMetrics.BandInner + RingMetrics.BandOuter) / 2;

    private static readonly Dictionary<string, IconEntry?> IconCache = [];

    /// <summary>The ring centre in control-local coordinates; <see cref="MeasureOverride"/>
    /// is built so this is exactly the centre of the ring.</summary>
    public static Point RingCentre { get; } = new(RingMetrics.FrameLeft, RingMetrics.FrameTop);

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

    /// <summary>Suppresses the hint pill (part 10 sets it when the hint would
    /// cover the edited key).</summary>
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

    /// <summary>Clears the hovered, pinned or highlighted id when the rebuilt
    /// layout no longer holds it.</summary>
    private void PruneRemovedIds()
    {
        if (HoverId is { } hover && !IdInLayout(hover)) HoverId = null;
        if (PinnedBandId is { } pinned && !IdInLayout(pinned)) PinnedBandId = null;
        if (KeyboardHighlightId is { } keyboard && !IdInLayout(keyboard)) KeyboardHighlightId = null;
    }

    /// <summary>Whether <paramref name="id"/> names the centre, a crumb that
    /// exists, or a slot in the current layout.</summary>
    private bool IdInLayout(string id) =>
        id == CenterId
        || (id.StartsWith(CrumbIdPrefix, StringComparison.Ordinal)
            && int.TryParse(id[CrumbIdPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var crumb)
            && crumb >= 0 && crumb < _crumbSlots.Count)
        || _slots.Any(slot => slot.Segment.Id == id);

    // ─── view state (pointer and keyboard land here) ─────────────────────

    /// <summary>The hovered segment or band id, <c>"center"</c>, or
    /// <c>"crumb:{i}"</c>; null when nothing is.</summary>
    public string? HoverId { get; private set; }

    /// <summary>The main segment whose band stays open without a hover.</summary>
    public string? PinnedBandId { get; private set; }

    /// <summary>The id the keyboard cursor is on; tints like a hover but
    /// never owns the band.</summary>
    public string? KeyboardHighlightId { get; private set; }

    /// <summary>The segment the pointer is on, else the one the keyboard
    /// cursor is on; the centre shows its full label and sub. Null over the
    /// centre, a crumb or nothing.</summary>
    internal KeyRingSegment? PointedSegment
    {
        get
        {
            if ((HoverId ?? KeyboardHighlightId) is not { } id) return null;
            foreach (var slot in _slots)
            {
                if (slot.Segment.Id == id) return slot.Segment;
            }

            return null;
        }
    }

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
    /// items; else the pinned id — and none when that owner has no band.</summary>
    private string? ComputeBandOwner()
    {
        if (Ring is not { } vm) return null;

        if (HoverId is { } hover
            && vm.Segments.Any(segment => segment.Id == hover)
            && vm.BandFor(hover).Count > 0)
        {
            return hover;
        }

        if (_bandOwner is { } owner
            && HoverId is { } onBand
            && _bandSegments.Any(segment => segment.Id == onBand)
            && vm.BandFor(owner).Count > 0)
        {
            return owner;
        }

        if (PinnedBandId is { } pinned && vm.BandFor(pinned).Count > 0) return pinned;

        return null;
    }

    // ─── layout (hit-tested below) ───────────────────────────────────────

    private readonly record struct Slot(
        KeyRingSegment Segment, double Start, double End, double Inner, double Outer, SlotLayer Layer);

    private enum SlotLayer { Main, Inner, Band }

    private readonly record struct CrumbSlot(int Index, KeyRingCrumb Crumb, Rect Bounds);

    private const string CenterId = "center";
    private const string CrumbIdPrefix = "crumb:";
    private const string ProfileIdPrefix = "prof:";

    private List<Slot> _slots = [];
    private List<CrumbSlot> _crumbSlots = [];
    private IReadOnlyList<KeyRingSegment> _bandSegments = [];
    private string? _bandOwner;
    private double _bandOpacity;

    private void RebuildLayout()
    {
        var slots = new List<Slot>();
        var crumbs = new List<CrumbSlot>();
        _bandSegments = [];

        if (Ring is { } vm)
        {
            var segments = vm.Segments;
            var inner = vm.InnerSegments;
            var hasInner = inner.Count > 0;
            var mainInner = vm.StepKind == KeyRingStepKind.Keycode && hasInner
                ? RingMetrics.GroupsInner : RingMetrics.RingInner;
            var mainOuter = vm.StepKind == KeyRingStepKind.Keycode && hasInner
                ? RingMetrics.GroupsOuter : RingMetrics.RingOuter;

            if (segments.Count > 0)
            {
                var start = RingGeometry.CenteredStart(segments.Count);
                for (var i = 0; i < segments.Count; i++)
                {
                    var (s, e) = RingGeometry.SegmentSpan(segments.Count, i, start);
                    slots.Add(new Slot(segments[i], s, e, mainInner, mainOuter, SlotLayer.Main));
                }
            }

            for (var i = 0; i < inner.Count; i++)
            {
                var (s, e) = RingGeometry.SegmentSpan(inner.Count, i, 0);
                slots.Add(new Slot(inner[i], s, e, RingMetrics.ModsInner, RingMetrics.ModsOuter, SlotLayer.Inner));
            }

            var owner = _bandOwner;
            var ownerSlot = owner is null
                ? (Slot?)null
                : slots.FirstOrDefault(slot => slot.Layer == SlotLayer.Main && slot.Segment.Id == owner);
            var band = owner is null ? [] : vm.BandFor(owner);
            if (owner is null || ownerSlot is null || band.Count == 0)
            {
                _bandOwner = null;
            }
            else
            {
                var centre = (ownerSlot.Value.Start + ownerSlot.Value.End) / 2;
                var widths = new List<double>(band.Count);
                foreach (var segment in band)
                {
                    widths.Add(BandItemWidth(segment));
                }

                var spans = band[0].Id.StartsWith(ProfileIdPrefix, StringComparison.Ordinal)
                    ? RingGeometry.BandInOrder(centre, widths, BandMidRadius)
                    : RingGeometry.BandCentreOut(centre, widths, BandMidRadius);

                for (var i = 0; i < band.Count; i++)
                {
                    slots.Add(new Slot(band[i], spans[i].Start, spans[i].End,
                        RingMetrics.BandInner, RingMetrics.BandOuter, SlotLayer.Band));
                }

                _bandSegments = band;
            }

            BuildCrumbSlots(vm, crumbs);
        }

        _slots = slots;
        _crumbSlots = crumbs;
    }

    private void BuildCrumbSlots(KeyRingViewModel vm, List<CrumbSlot> crumbs)
    {
        var list = vm.Crumbs;
        if (list.Count == 0) return;

        const double separator = 16;
        var widths = new double[list.Count];
        var total = 0.0;
        for (var i = 0; i < list.Count; i++)
        {
            var crumb = list[i];
            var textWidth = MeasureTextWidth(
                crumb.Text, crumb.IsCode ? MonoMedium : InterMedium, 12);
            widths[i] = textWidth + 2 * 10 + (crumb.IsChanged ? 9 : 0);
            total += widths[i];
        }

        total += separator * (list.Count - 1);

        var x = RingCentre.X - total / 2;
        for (var i = 0; i < list.Count; i++)
        {
            crumbs.Add(new CrumbSlot(i, list[i],
                new Rect(x, 0, widths[i], RingMetrics.CrumbHeight)));
            x += widths[i] + separator;
        }
    }

    /// <summary>Band item width from the text as drawn: the wider of label
    /// and sub, in their segment fonts, through <see cref="RingGeometry.BandItemWidth"/>.</summary>
    private static double BandItemWidth(KeyRingSegment segment)
    {
        var labelWidth = MeasureTextWidth(
            segment.Label,
            segment.LabelIsCode ? MonoSemiBold : InterSemiBold,
            SegmentLabelSize(segment, layerStep: false));

        var textWidth = labelWidth;
        if (segment.Sub is { Length: > 0 } sub)
        {
            textWidth = Math.Max(textWidth,
                MeasureTextWidth(sub, segment.SubIsCode ? MonoRegular : InterRegular, 11));
        }

        return RingGeometry.BandItemWidth(textWidth);
    }

    private static double SegmentLabelSize(KeyRingSegment segment, bool layerStep) =>
        segment.LabelIsCode ? 12
        : layerStep || segment.Id.StartsWith(ProfileIdPrefix, StringComparison.Ordinal) ? 15
        : 13;

    /// <summary>Centre of each open band item in control coordinates, in item
    /// order — the point a hit-test lands on for that item. Visible to
    /// LViz.Tests (InternalsVisibleTo) for its input tests.</summary>
    internal IReadOnlyList<(string Id, Point Centre)> BandItemCentres
    {
        get
        {
            var list = new List<(string Id, Point Centre)>(_slots.Count);
            foreach (var slot in _slots)
            {
                if (slot.Layer != SlotLayer.Band) continue;
                var mid = RingGeometry.Polar((slot.Inner + slot.Outer) / 2, (slot.Start + slot.End) / 2);
                list.Add((slot.Segment.Id, new Point(RingCentre.X + mid.X, RingCentre.Y + mid.Y)));
            }

            return list;
        }
    }

    /// <summary>The crumb boxes in control coordinates, in crumb order.
    /// Visible to LViz.Tests (InternalsVisibleTo) for its input tests.</summary>
    internal IReadOnlyList<Rect> CrumbBounds => [.. _crumbSlots.Select(slot => slot.Bounds)];

    // ─── hit-testing ─────────────────────────────────────────────────────

    /// <summary>Radial slack on slot hit-testing: the gaps between drawn
    /// segments belong to the segments (selection is by direction).</summary>
    private const double HitPad = 1.5;

    /// <summary>What is under a control-local point: a segment or band-item
    /// id, <c>"center"</c> or <c>"crumb:{i}"</c> — in this order: crumb boxes,
    /// the centre disc, the open band, the inner ring, the main ring (each
    /// ring matched by radius ± <see cref="HitPad"/> and angle span). A
    /// disabled segment, and anything off the ring, is null.</summary>
    public string? HitTest(Point p)
    {
        if (Ring is null) return null;

        foreach (var slot in _crumbSlots)
        {
            if (slot.Bounds.Contains(p)) return $"{CrumbIdPrefix}{slot.Index}";
        }

        var dx = p.X - RingCentre.X;
        var dy = p.Y - RingCentre.Y;
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (r <= RingMetrics.CenterRadius) return CenterId;

        var angle = RingGeometry.AngleOf(dx, dy);
        return HitSlots(SlotLayer.Band, angle, r)
            ?? HitSlots(SlotLayer.Inner, angle, r)
            ?? HitSlots(SlotLayer.Main, angle, r);
    }

    /// <summary>The id of the slot of this layer whose padded radii hold
    /// <paramref name="r"/> and whose span holds <paramref name="angle"/>; a
    /// disabled slot hitting makes the whole layer miss (null).</summary>
    private string? HitSlots(SlotLayer layer, double angle, double r)
    {
        foreach (var slot in _slots)
        {
            if (slot.Layer != layer) continue;
            if (r < slot.Inner - HitPad || r > slot.Outer + HitPad) continue;
            if (!RingGeometry.InSpan(angle, slot.Start, slot.End)) continue;
            return slot.Segment.IsEnabled ? slot.Segment.Id : null;
        }

        return null;
    }

    /// <summary>Whether a control-local point lies on anything the ring owns:
    /// within <see cref="RingMetrics.ExtentRadius"/> of the centre, on a
    /// crumb, or on the hint pill while it shows. A left-click here never
    /// dismisses (the dead zone between ring and band included).</summary>
    public bool IsOverRing(Point p)
    {
        if (Ring is null) return false;

        var dx = p.X - RingCentre.X;
        var dy = p.Y - RingCentre.Y;
        if (Math.Sqrt(dx * dx + dy * dy) <= RingMetrics.ExtentRadius) return true;

        foreach (var slot in _crumbSlots)
        {
            if (slot.Bounds.Contains(p)) return true;
        }

        return HintBounds() is { } hint && hint.Contains(p);
    }

    /// <summary>The hint pill's rectangle in control coordinates, or null
    /// while it is hidden or has no text.</summary>
    private Rect? HintBounds()
    {
        if (HideHint || Ring is not { } vm || vm.Hint.Length == 0) return null;

        var width = MeasureTextWidth(vm.Hint, InterRegular, 11) + 2 * 12;
        return new Rect(
            RingCentre.X - width / 2,
            RingCentre.Y + RingMetrics.ExtentRadius + RingMetrics.HintGap,
            width, RingMetrics.HintHeight);
    }

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

        var id = HitTest(e.GetPosition(this));
        if (id is not null) SetHover(id); // a miss keeps the last hover (dead zone)
        Cursor = id is not null ? HandCursor : ArrowCursor;
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
        if (HitTest(p) is { } id) _lastPressPicked = ApplyClick(id);
        else if (!IsOverRing(p)) Dismissed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What a left-click on this id does: the centre raises
    /// <see cref="SaveRequested"/> when closable; a crumb jumps there; a main
    /// segment with a band toggles the pin; anything else is chosen and the
    /// view starts clean. Returns whether the click picked a value (see
    /// <see cref="KeyRingViewModel.PicksAValue"/>).</summary>
    private bool ApplyClick(string id)
    {
        if (Ring is not { } vm) return false;

        if (id == CenterId)
        {
            if (vm.CanClose) SaveRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (id.StartsWith(CrumbIdPrefix, StringComparison.Ordinal)
            && int.TryParse(
                id[CrumbIdPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var crumb))
        {
            vm.GoToCrumb(crumb);
            PinBand(null);
            return false;
        }

        if (_slots.Any(slot => slot.Layer == SlotLayer.Main && slot.Segment.Id == id)
            && vm.BandFor(id).Count > 0)
        {
            PinBand(PinnedBandId == id ? null : id);
            return false;
        }

        var picked = vm.Choose(id) && KeyRingViewModel.PicksAValue(id);
        PinBand(null);
        SetKeyboardHighlight(null);
        return picked;
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
            var count = _bandSegments.Count;
            var next = ((bandIndex + delta) % count + count) % count;
            SetKeyboardHighlight(_bandSegments[next].Id);
            return;
        }

        var mains = new List<KeyRingSegment>();
        foreach (var slot in _slots)
        {
            if (slot.Layer == SlotLayer.Main) mains.Add(slot.Segment);
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
        PinBand(vm.BandFor(segment.Id).Count > 0 ? segment.Id : null);
    }

    /// <summary>Steps the cursor from a highlighted main segment into the
    /// first item of its open band; without one it does nothing.</summary>
    public void MoveIntoBand()
    {
        if (_bandOwner is null || KeyboardHighlightId != _bandOwner || _bandSegments.Count == 0) return;
        SetKeyboardHighlight(_bandSegments[0].Id);
    }

    /// <summary>Does what a left-click on the highlighted id does; false when
    /// nothing is highlighted.</summary>
    public bool ActivateKeyboardHighlight()
    {
        if (KeyboardHighlightId is not { } id) return false;
        ApplyClick(id);
        return true;
    }

    private int? IndexOfBandItem(string id)
    {
        for (var i = 0; i < _bandSegments.Count; i++)
        {
            if (_bandSegments[i].Id == id) return i;
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
            DrawAll(context, vm);
        }
        catch (Exception ex)
        {
            // An exception inside Render freezes the window; log and draw a
            // partial ring instead.
            DiagnosticLog.Error("KeyRing", $"Render failed: {ex}");
        }
    }

    private void DrawAll(DrawingContext context, KeyRingViewModel vm)
    {
        using (context.PushTransform(Matrix.CreateTranslation(RingCentre.X, RingCentre.Y)))
        {
            var bandOpen = _bandOwner is not null && _bandSegments.Count > 0;

            // 1. backdrop disc
            context.DrawEllipse(BackdropBrush, null, default,
                RingMetrics.BackdropRadius, RingMetrics.BackdropRadius);

            // 2. band backdrop, then 3–5. segments, labels and markers. The
            // band draws at its fade-in opacity; nothing shares a radius with
            // it, so keeping its passes together inside the scope preserves
            // the painting order.
            if (bandOpen)
            {
                using (context.PushOpacity(_bandOpacity))
                {
                    DrawBandBackdrop(context);
                }
            }

            var layerStep = vm.StepKind == KeyRingStepKind.Layer;

            foreach (var slot in _slots)
            {
                if (slot.Layer != SlotLayer.Band) DrawSegment(context, slot);
            }

            if (bandOpen)
            {
                using (context.PushOpacity(_bandOpacity))
                {
                    foreach (var slot in _slots)
                    {
                        if (slot.Layer == SlotLayer.Band) DrawSegment(context, slot);
                    }
                }
            }

            foreach (var slot in _slots)
            {
                if (slot.Layer != SlotLayer.Band) DrawSegmentLabel(context, slot, layerStep);
            }

            if (bandOpen)
            {
                using (context.PushOpacity(_bandOpacity))
                {
                    foreach (var slot in _slots)
                    {
                        if (slot.Layer == SlotLayer.Band) DrawSegmentLabel(context, slot, layerStep);
                    }
                }
            }

            foreach (var slot in _slots)
            {
                if (slot.Layer != SlotLayer.Band) DrawCurrentMarker(context, slot);
            }

            if (bandOpen)
            {
                using (context.PushOpacity(_bandOpacity))
                {
                    foreach (var slot in _slots)
                    {
                        if (slot.Layer == SlotLayer.Band) DrawCurrentMarker(context, slot);
                    }
                }
            }

            // 6. centre
            DrawCentre(context, vm);

            // 7. crumbs (the row runs across the top of the frame)
            DrawCrumbs(context);

            // 8. hint
            DrawHint(context, vm);
        }
    }

    private void DrawBandBackdrop(DrawingContext context)
    {
        var pad = RingMetrics.BandBackdropPad /
                  ((RingMetrics.BandBackdropInner + RingMetrics.BandBackdropOuter) / 2);

        var start = double.MaxValue;
        var end = double.MinValue;
        foreach (var slot in _slots)
        {
            if (slot.Layer != SlotLayer.Band) continue;
            start = Math.Min(start, slot.Start);
            end = Math.Max(end, slot.End);
        }

        if (start > end) return;

        var geometry = SectorGeometry(RingMetrics.BandBackdropInner, RingMetrics.BandBackdropOuter,
            start - pad, end + pad, gap: 0, corner: RingMetrics.BackdropCorner);
        context.DrawGeometry(BackdropBrush, null, geometry);
    }

    private void DrawSegment(DrawingContext context, Slot slot)
    {
        var segment = slot.Segment;
        var h = AmountOf(segment.Id);

        var fill = !segment.IsEnabled ? DisabledBrush
            : segment.IsOn ? BlendBrush(ToggledOnBrush, ToggledOnHoverBrush, h)
            : slot.Layer == SlotLayer.Band ? BlendBrush(BandSegmentBrush, SegmentHoverBrush, h)
            : BlendBrush(SegmentBrush, SegmentHoverBrush, h);

        IPen? pen = h > 0
            ? new Pen(new SolidColorBrush(WithAlpha(GoldBrush.Color, h)), 1.5)
            : null;

        context.DrawGeometry(fill, pen,
            SectorGeometry(slot.Inner, slot.Outer, slot.Start, slot.End, RingMetrics.Gap, RingMetrics.Corner));
    }

    private void DrawSegmentLabel(DrawingContext context, Slot slot, bool layerStep)
    {
        var segment = slot.Segment;
        var h = AmountOf(segment.Id);
        var midRadius = (slot.Inner + slot.Outer) / 2;
        var centre = RingGeometry.Polar(midRadius, (slot.Start + slot.End) / 2);
        var maxWidth = Math.Max(8, 2 * midRadius * Math.Sin((slot.End - slot.Start) / 2) - 8);

        var icon = segment.Icon is { Length: > 0 } name ? LoadIcon(name) : null;
        var hasSub = segment.Sub is { Length: > 0 };
        var iconBlock = icon is null ? 0 : 16 + 4;
        var blockHeight = iconBlock + 16 + (hasSub ? 2 + 14 : 0);
        var top = centre.Y - blockHeight / 2;

        if (icon is not null)
        {
            var iconColor = !segment.IsEnabled ? MutedBrush.Color
                : h > 0.5 ? GoldBrush.Color
                : SubBrush.Color;
            DrawIcon(context, icon.Value, centre.X, top + 8, new SolidColorBrush(iconColor));
        }

        var labelColor = !segment.IsEnabled ? MutedBrush.Color
            : h > 0.5 || segment.IsOn ? GoldBrush.Color
            : TextBrush.Color;
        var labelSize = SegmentLabelSize(segment, layerStep);
        DrawTextLine(context, segment.Label,
            segment.LabelIsCode ? MonoSemiBold : InterSemiBold, labelSize,
            new SolidColorBrush(labelColor),
            top + iconBlock, 16, centre.X, maxWidth);

        if (!hasSub) return;

        var subColor = !segment.IsEnabled ? MutedBrush.Color : SubBrush.Color;
        DrawTextLine(context, segment.Sub!,
            segment.SubIsCode ? MonoRegular : InterRegular, 11,
            new SolidColorBrush(subColor),
            top + iconBlock + 16 + 2, 14, centre.X, maxWidth);
    }

    private void DrawCurrentMarker(DrawingContext context, Slot slot)
    {
        if (!slot.Segment.IsCurrent) return;

        var radius = slot.Outer - 5;
        var mid = (slot.Start + slot.End) / 2;
        var half = Math.Min(0.2 * (slot.End - slot.Start), 14 / radius);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(RingGeometry.Polar(radius, mid - half), isFilled: true);
            ctx.ArcTo(RingGeometry.Polar(radius, mid + half), new Size(radius, radius),
                0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(GoldBrush, 2.5) { LineCap = PenLineCap.Round }, geometry);
    }

    private void DrawCentre(DrawingContext context, KeyRingViewModel vm)
    {
        var changed = vm.CenterState == KeyRingCenterState.Changed;
        var fill = changed ? CenterChangedBrush.Color : CenterBrush.Color;
        if (vm.CanClose && AmountOf(CenterId) > 0)
        {
            fill = Blend(fill, SegmentHoverBrush.Color, 0.4);
        }

        var stroke = changed ? GoldBrush : CenterStrokeBrush;
        var strokeWidth = changed ? 2 : 1;
        context.DrawEllipse(new SolidColorBrush(fill), new Pen(stroke, strokeWidth), default,
            RingMetrics.CenterRadius, RingMetrics.CenterRadius);

        if (vm.StepKind == KeyRingStepKind.Text)
        {
            // The middle belongs to the text box part 9 puts there; the
            // caption and the line keep their 30 px offsets from the centre.
            var textCaption = LayoutText(vm.CenterCaption.ToUpperInvariant(),
                InterSemiBold, 10, MutedBrush, 104, letterSpacing: 0.6);
            DrawLineBox(context, textCaption, -30 - textCaption.Height / 2, 52);
            textCaption.Dispose();

            var textLine = LayoutText(vm.CenterLine, InterRegular, 11, SubBrush, 104);
            DrawLineBox(context, textLine, 30 - textLine.Height / 2, 52);
            textLine.Dispose();
            return;
        }

        // Pointing at a segment shows its full name and description here:
        // labels in narrow segments are trimmed, the centre has the room.
        var lines = PointedSegment is { } pointed
            ? PointedLines(pointed)
            : BindingLines(vm, changed);

        var total = 0.0;
        foreach (var (_, height) in lines) total += height;

        var y = -total / 2;
        foreach (var (layout, height) in lines)
        {
            DrawLineBox(context, layout, y + (height - layout.Height) / 2, 52);
            layout.Dispose();
            y += height;
        }
    }

    /// <summary>The centre's text while a segment is pointed at: its label
    /// (gold, like the hovered segment) and its sub, each up to two lines.</summary>
    private static List<(TextLayout Layout, double Height)> PointedLines(KeyRingSegment pointed)
    {
        var label = LayoutText(pointed.Label,
            pointed.LabelIsCode ? MonoSemiBold : InterSemiBold, 13,
            pointed.IsEnabled ? GoldBrush : MutedBrush, 104, maxLines: 2);
        var lines = new List<(TextLayout Layout, double Height)> { (label, label.Height + 2) };

        if (pointed.Sub is { Length: > 0 } sub)
        {
            var subText = LayoutText(sub, pointed.SubIsCode ? MonoRegular : InterRegular, 11,
                SubBrush, 104, maxLines: 2);
            lines.Add((subText, subText.Height));
        }

        return lines;
    }

    /// <summary>The centre's text otherwise: caption, the binding before the
    /// change, the binding, and the line (save / pick / description).</summary>
    private static List<(TextLayout Layout, double Height)> BindingLines(KeyRingViewModel vm, bool changed)
    {
        var lines = new List<(TextLayout Layout, double Height)>();

        if (vm.CenterState is KeyRingCenterState.Choosing or KeyRingCenterState.Unchanged
            && vm.CenterCaption.Length > 0)
        {
            lines.Add((LayoutText(vm.CenterCaption.ToUpperInvariant(), InterSemiBold, 10,
                MutedBrush, 104, letterSpacing: 0.6), 15));
        }

        if (vm.CenterBefore.Length > 0)
        {
            lines.Add((LayoutText(vm.CenterBefore + "  \u2192", MonoRegular, 11,
                MutedBrush, 104), 16));
        }

        lines.Add((LayoutText(vm.CenterBinding, MonoBold, 14,
            changed ? GoldBrush : TextBrush, 104), 20));

        if (vm.CenterLine.Length > 0)
        {
            lines.Add((LayoutText(vm.CenterLine,
                changed ? InterSemiBold : InterRegular, 11,
                changed ? GoldBrush : SubBrush, 104), 16));
        }

        return lines;
    }

    private void DrawCrumbs(DrawingContext context)
    {
        if (_crumbSlots.Count == 0) return;

        var left = -RingCentre.X;
        var top = -RingCentre.Y;

        foreach (var slot in _crumbSlots)
        {
            var bounds = new Rect(
                slot.Bounds.X + left, slot.Bounds.Y + top,
                slot.Bounds.Width, slot.Bounds.Height);
            var h = AmountOf($"{CrumbIdPrefix}{slot.Index}");
            var crumb = slot.Crumb;

            IBrush? fill;
            IPen? pen;
            Color textColor;
            switch (crumb.State)
            {
                case KeyRingCrumbState.Current:
                    fill = GoldSoftBrush;
                    pen = new Pen(GoldBrush, 1);
                    textColor = GoldBrush.Color;
                    break;
                case KeyRingCrumbState.Open:
                    fill = null;
                    pen = new Pen(GoldBrush, 1) { DashStyle = new DashStyle([3, 3], 0) };
                    textColor = GoldBrush.Color;
                    break;
                default:
                    fill = BlendBrush(SegmentBrush, SegmentHoverBrush, h);
                    pen = null;
                    textColor = TextBrush.Color;
                    break;
            }

            var radius = RingMetrics.CrumbHeight / 2;
            context.DrawRectangle(fill, pen, bounds, radius, radius);

            var typeface = crumb.IsCode ? MonoMedium : InterMedium;
            using (var text = LayoutText(crumb.Text, typeface, 12, new SolidColorBrush(textColor), 0))
            {
                text.Draw(context, new Point(
                    bounds.X + 10, bounds.Y + (RingMetrics.CrumbHeight - text.Height) / 2));
            }

            if (crumb.IsChanged)
            {
                context.DrawEllipse(GoldBrush, null,
                    new Point(bounds.Right - 9, bounds.Y + RingMetrics.CrumbHeight / 2), 3, 3);
            }

            if (slot.Index < _crumbSlots.Count - 1)
            {
                using (var chevron = LayoutText("\u203A", InterRegular, 12, MutedBrush, 0))
                {
                    var gapCentreX = bounds.Right + 16 / 2;
                    chevron.Draw(context, new Point(
                        gapCentreX - chevron.Width / 2,
                        bounds.Y + (RingMetrics.CrumbHeight - chevron.Height) / 2));
                }
            }
        }
    }

    private void DrawHint(DrawingContext context, KeyRingViewModel vm)
    {
        if (HideHint || vm.Hint.Length == 0) return;

        using var text = LayoutText(vm.Hint, InterRegular, 11, SubBrush, 0);
        var width = text.Width + 2 * 12;
        var top = RingMetrics.ExtentRadius + RingMetrics.HintGap;

        context.DrawRectangle(HintBrush, null,
            new Rect(-width / 2, top, width, RingMetrics.HintHeight), 11, 11);
        text.Draw(context, new Point(
            -width / 2 + 12, top + (RingMetrics.HintHeight - text.Height) / 2));
    }

    // ─── paths and text helpers ──────────────────────────────────────────

    /// <summary>The segment outline as a fillable geometry, following the
    /// order documented on <see cref="SectorOutline"/>: corner arcs clockwise,
    /// the outer arc clockwise, the inner arc counter-clockwise.</summary>
    private static StreamGeometry SectorGeometry(
        double inner, double outer, double start, double end, double gap, double corner)
    {
        var outline = RingGeometry.Sector(inner, outer, start, end, gap, corner);
        var cornerSize = new Size(corner, corner);
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(outline.OuterEdge0, isFilled: true);
            ctx.ArcTo(outline.OuterArc0, cornerSize, 0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.ArcTo(outline.OuterArc1, new Size(outer, outer), 0, outline.IsLargeArc, SweepDirection.Clockwise);
            ctx.ArcTo(outline.OuterEdge1, cornerSize, 0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.LineTo(outline.InnerEdge1);
            ctx.ArcTo(outline.InnerArc1, cornerSize, 0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.ArcTo(outline.InnerArc0, new Size(inner, inner), 0, outline.IsLargeArc, SweepDirection.CounterClockwise);
            ctx.ArcTo(outline.InnerEdge0, cornerSize, 0, isLargeArc: false, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: true);
        }

        return geometry;
    }

    /// <summary>Draws <paramref name="text"/> centred on <paramref name="centreX"/>
    /// (control-local, ring-centre-relative), top-aligned inside the line box
    /// starting at <paramref name="y"/>.</summary>
    private static void DrawLineBox(DrawingContext context, TextLayout text, double y, double halfWidth)
    {
        text.Draw(context, new Point(-halfWidth, y));
    }

    private void DrawTextLine(
        DrawingContext context, string text, Typeface typeface, double size,
        IBrush brush, double y, double lineHeight, double centreX, double maxWidth)
    {
        using var layout = LayoutText(text, typeface, size, brush, maxWidth);
        layout.Draw(context, new Point(centreX - maxWidth / 2, y + (lineHeight - layout.Height) / 2));
    }

    private static TextLayout LayoutText(
        string text, Typeface typeface, double size, IBrush brush, double maxWidth,
        double letterSpacing = 0, int maxLines = 1) =>
        new(
            text, typeface, size, brush,
            textAlignment: TextAlignment.Center,
            textWrapping: maxLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            textTrimming: TextTrimming.CharacterEllipsis,
            // 0 means unlimited: TextLayout clamps a maxWidth of 0 to an empty
            // line box.
            maxWidth: maxWidth > 0 ? maxWidth : double.PositiveInfinity,
            maxLines: maxLines,
            letterSpacing: letterSpacing);

    private static double MeasureTextWidth(string text, Typeface typeface, double size)
    {
        if (text.Length == 0) return 0;

        using var layout = new TextLayout(
            text, typeface, size, TextBrush,
            textAlignment: TextAlignment.Left,
            textWrapping: TextWrapping.NoWrap,
            textTrimming: TextTrimming.None);
        return layout.Width;
    }

    // ─── icons ───────────────────────────────────────────────────────────

    private readonly record struct IconEntry(Geometry Geometry, int X, int Y, int Width, int Height);

    /// <summary>The cached path geometry of one Font Awesome icon, or null
    /// when it was unknown or unreadable (then it is skipped).</summary>
    private static IconEntry? LoadIcon(string name)
    {
        if (IconCache.TryGetValue(name, out var cached)) return cached;

        IconEntry? entry = null;
        try
        {
            var model = IconProvider.Current.GetIcon(name);
            string data = model.Path;
            if (data.Length > 0 && model.ViewBox.Width > 0 && model.ViewBox.Height > 0)
            {
                entry = new IconEntry(
                    Geometry.Parse(data),
                    model.ViewBox.X, model.ViewBox.Y, model.ViewBox.Width, model.ViewBox.Height);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("KeyRing", $"Icon '{name}' unavailable: {ex.Message}");
            entry = null;
        }

        IconCache[name] = entry;
        return entry;
    }

    private static void DrawIcon(DrawingContext context, IconEntry icon, double centreX, double centreY, IBrush brush)
    {
        const double size = 16;
        var scale = size / Math.Max(icon.Width, icon.Height);
        var offsetX = centreX - scale * (icon.X + icon.Width / 2.0);
        var offsetY = centreY - scale * (icon.Y + icon.Height / 2.0);

        using (context.PushTransform(new Matrix(scale, 0, 0, scale, offsetX, offsetY)))
        {
            context.DrawGeometry(brush, null, icon.Geometry);
        }
    }

    // ─── colour helpers ──────────────────────────────────────────────────

    private static SolidColorBrush Tint(string hex) => new(Color.Parse(hex));

    private static Typeface New(FontFamily family, FontWeight weight) =>
        new(family, FontStyle.Normal, weight);

    /// <summary>Linear RGB blend from idle to hover.</summary>
    private static Color Blend(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    private static SolidColorBrush BlendBrush(SolidColorBrush from, SolidColorBrush to, double t) =>
        t <= 0 ? from : t >= 1 ? to : new SolidColorBrush(Blend(from.Color, to.Color, t));

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);
}
