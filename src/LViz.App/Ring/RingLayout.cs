using Avalonia;
using LViz.App.ViewModels;

namespace LViz.App.Ring;

/// <summary>Which ring a <see cref="RingSlot"/> belongs to.</summary>
internal enum RingLayer { Main, Inner, Band }

/// <summary>One sector placed on the ring: its angular span and its radii.</summary>
internal readonly record struct RingSlot(
    KeyRingSegment Segment, double Start, double End, double Inner, double Outer, RingLayer Layer);

/// <summary>One crumb placed above the ring, in control-local coordinates.</summary>
internal readonly record struct RingCrumbSlot(int Index, KeyRingCrumb Crumb, Rect Bounds);

/// <summary>
/// What a point on the ring lands on. <see cref="Key"/> is the stable string
/// the control keys its hover and animation state by; it is built from the
/// hit, never parsed back.
/// </summary>
internal abstract record RingHit
{
    private RingHit() { }

    public abstract string Key { get; }

    /// <summary>The centre disc.</summary>
    public sealed record Center : RingHit
    {
        public const string CenterKey = "center";

        public override string Key => CenterKey;
    }

    /// <summary>A crumb above the ring.</summary>
    public sealed record Crumb(int Index) : RingHit
    {
        public override string Key => "crumb:" + Index;
    }

    /// <summary>A sector of the main ring, the inner ring or the band.</summary>
    public sealed record Segment(KeyRingSegment Value) : RingHit
    {
        public override string Key => Value.Id;
    }
}

/// <summary>Text widths the layout needs, which only a font stack can give.</summary>
internal interface IRingTextMeasure
{
    /// <summary>Width of the widest line (label or sub) of a band item, as drawn.</summary>
    double BandItemTextWidth(KeyRingSegment segment);

    /// <summary>Width of a crumb's text, as drawn.</summary>
    double CrumbTextWidth(KeyRingCrumb crumb);
}

/// <summary>
/// Where everything of the key ring sits, built from a
/// <see cref="KeyRingViewModel"/>: the sector slots of the main ring, the inner
/// modifier ring and the open band, the crumb boxes, and the hit-testing over
/// them. Pure geometry (text widths come in through
/// <see cref="IRingTextMeasure"/>), so it is unit-testable without a window.
/// </summary>
internal sealed class RingLayout
{
    /// <summary>The ring centre in control-local coordinates.</summary>
    public static Point Centre { get; } = new(RingMetrics.FrameLeft, RingMetrics.FrameTop);

    /// <summary>Radial slack on slot hit-testing: the gaps between drawn
    /// segments belong to the segments (selection is by direction).</summary>
    private const double HitPad = 1.5;

    /// <summary>Middle of the band, where item widths are measured.</summary>
    private static readonly double BandMidRadius = (RingMetrics.BandInner + RingMetrics.BandOuter) / 2;

    /// <summary>The layout of no ring: it hits and holds nothing.</summary>
    public static RingLayout Empty { get; } = new([], [], [], null, hasRing: false);

    private readonly bool _hasRing;

    private RingLayout(
        IReadOnlyList<RingSlot> slots, IReadOnlyList<RingCrumbSlot> crumbs,
        IReadOnlyList<KeyRingSegment> band, string? bandOwnerKey, bool hasRing)
    {
        Slots = slots;
        CrumbSlots = crumbs;
        BandSegments = band;
        BandOwnerKey = bandOwnerKey;
        _hasRing = hasRing;
    }

    public IReadOnlyList<RingSlot> Slots { get; }

    public IReadOnlyList<RingCrumbSlot> CrumbSlots { get; }

    /// <summary>The open band's items, in item order; empty while no band is open.</summary>
    public IReadOnlyList<KeyRingSegment> BandSegments { get; }

    /// <summary>The key of the main segment whose band is open, or null when
    /// none is (a requested owner without a band, or not on the ring, ends up
    /// here).</summary>
    public string? BandOwnerKey { get; }

    /// <summary>Label font size of a sector: code labels are small, layer and
    /// profile numbers big.</summary>
    public static double LabelSize(KeyRingSegment segment, bool layerStep) =>
        segment.LabelIsCode ? 12
        : layerStep || segment.Target is KeyRingTarget.Profile ? 15
        : 13;

    /// <summary>Lays out the ring for the view-model's current state, with the
    /// band of the main segment <paramref name="bandOwnerKey"/> open when that
    /// segment has one.</summary>
    public static RingLayout Build(KeyRingViewModel vm, string? bandOwnerKey, IRingTextMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(measure);

        var slots = new List<RingSlot>();
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
                slots.Add(new RingSlot(segments[i], s, e, mainInner, mainOuter, RingLayer.Main));
            }
        }

        for (var i = 0; i < inner.Count; i++)
        {
            var (s, e) = RingGeometry.SegmentSpan(inner.Count, i, 0);
            slots.Add(new RingSlot(inner[i], s, e, RingMetrics.ModsInner, RingMetrics.ModsOuter, RingLayer.Inner));
        }

        IReadOnlyList<KeyRingSegment> band = [];
        string? ownerKey = null;

        if (bandOwnerKey is not null)
        {
            RingSlot? ownerSlot = slots
                .Where(slot => slot.Layer == RingLayer.Main && slot.Segment.Id == bandOwnerKey)
                .Select(slot => (RingSlot?)slot)
                .FirstOrDefault();

            var items = ownerSlot is { } owner ? vm.BandFor(owner.Segment.Target) : [];
            if (ownerSlot is { } placed && items.Count > 0)
            {
                var centre = (placed.Start + placed.End) / 2;
                var widths = new List<double>(items.Count);
                foreach (var segment in items)
                {
                    widths.Add(RingGeometry.BandItemWidth(measure.BandItemTextWidth(segment)));
                }

                var spans = items[0].Target is KeyRingTarget.Profile
                    ? RingGeometry.BandInOrder(centre, widths, BandMidRadius)
                    : RingGeometry.BandCentreOut(centre, widths, BandMidRadius);

                for (var i = 0; i < items.Count; i++)
                {
                    slots.Add(new RingSlot(items[i], spans[i].Start, spans[i].End,
                        RingMetrics.BandInner, RingMetrics.BandOuter, RingLayer.Band));
                }

                band = items;
                ownerKey = bandOwnerKey;
            }
        }

        return new RingLayout(slots, BuildCrumbSlots(vm, measure), band, ownerKey, hasRing: true);
    }

    private static List<RingCrumbSlot> BuildCrumbSlots(KeyRingViewModel vm, IRingTextMeasure measure)
    {
        var crumbs = new List<RingCrumbSlot>();
        var list = vm.Crumbs;
        if (list.Count == 0) return crumbs;

        const double separator = 16;
        var widths = new double[list.Count];
        var total = 0.0;
        for (var i = 0; i < list.Count; i++)
        {
            var crumb = list[i];
            widths[i] = measure.CrumbTextWidth(crumb) + 2 * 10 + (crumb.IsChanged ? 9 : 0);
            total += widths[i];
        }

        total += separator * (list.Count - 1);

        var x = Centre.X - total / 2;
        for (var i = 0; i < list.Count; i++)
        {
            crumbs.Add(new RingCrumbSlot(i, list[i], new Rect(x, 0, widths[i], RingMetrics.CrumbHeight)));
            x += widths[i] + separator;
        }

        return crumbs;
    }

    /// <summary>The slot of the main ring with this key, or null.</summary>
    public RingSlot? MainSlot(string key)
    {
        foreach (var slot in Slots)
        {
            if (slot.Layer == RingLayer.Main && slot.Segment.Id == key) return slot;
        }

        return null;
    }

    /// <summary>What the key names in this layout: the centre, a crumb that
    /// exists, or a slot; null when it names nothing (any more).</summary>
    public RingHit? Find(string key)
    {
        if (!_hasRing) return null;
        if (key == RingHit.Center.CenterKey) return new RingHit.Center();

        foreach (var crumb in CrumbSlots)
        {
            var hit = new RingHit.Crumb(crumb.Index);
            if (hit.Key == key) return hit;
        }

        foreach (var slot in Slots)
        {
            if (slot.Segment.Id == key) return new RingHit.Segment(slot.Segment);
        }

        return null;
    }

    /// <summary>Centre of each open band item in control coordinates, in item
    /// order — the point a hit-test lands on for that item.</summary>
    public IReadOnlyList<(string Id, Point Centre)> BandItemCentres()
    {
        var list = new List<(string Id, Point Centre)>(BandSegments.Count);
        foreach (var slot in Slots)
        {
            if (slot.Layer != RingLayer.Band) continue;
            var mid = RingGeometry.Polar((slot.Inner + slot.Outer) / 2, (slot.Start + slot.End) / 2);
            list.Add((slot.Segment.Id, new Point(Centre.X + mid.X, Centre.Y + mid.Y)));
        }

        return list;
    }

    /// <summary>What is under a control-local point — in this order: crumb
    /// boxes, the centre disc, the open band, the inner ring, the main ring
    /// (each ring matched by radius ± <see cref="HitPad"/> and angle span). A
    /// disabled segment, and anything off the ring, is null.</summary>
    public RingHit? HitTest(Point p)
    {
        if (!_hasRing) return null;

        foreach (var slot in CrumbSlots)
        {
            if (slot.Bounds.Contains(p)) return new RingHit.Crumb(slot.Index);
        }

        var dx = p.X - Centre.X;
        var dy = p.Y - Centre.Y;
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (r <= RingMetrics.CenterRadius) return new RingHit.Center();

        var angle = RingGeometry.AngleOf(dx, dy);
        return HitSlots(RingLayer.Band, angle, r)
            ?? HitSlots(RingLayer.Inner, angle, r)
            ?? HitSlots(RingLayer.Main, angle, r);
    }

    /// <summary>The slot of this layer whose padded radii hold <paramref name="r"/>
    /// and whose span holds <paramref name="angle"/>; a disabled slot hitting
    /// makes the whole layer miss (null).</summary>
    private RingHit? HitSlots(RingLayer layer, double angle, double r)
    {
        foreach (var slot in Slots)
        {
            if (slot.Layer != layer) continue;
            if (r < slot.Inner - HitPad || r > slot.Outer + HitPad) continue;
            if (!RingGeometry.InSpan(angle, slot.Start, slot.End)) continue;
            return slot.Segment.IsEnabled ? new RingHit.Segment(slot.Segment) : null;
        }

        return null;
    }

    /// <summary>Whether a control-local point lies on anything the ring owns:
    /// within <see cref="RingMetrics.ExtentRadius"/> of the centre, on a
    /// crumb, or on the hint pill (<paramref name="hint"/>, null while it is
    /// hidden). A left-click here never dismisses (the dead zone between ring
    /// and band included).</summary>
    public bool IsOverRing(Point p, Rect? hint)
    {
        if (!_hasRing) return false;

        var dx = p.X - Centre.X;
        var dy = p.Y - Centre.Y;
        if (Math.Sqrt(dx * dx + dy * dy) <= RingMetrics.ExtentRadius) return true;

        foreach (var slot in CrumbSlots)
        {
            if (slot.Bounds.Contains(p)) return true;
        }

        return hint is { } pill && pill.Contains(p);
    }
}
