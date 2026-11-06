using System;
using System.Collections.Generic;
using Avalonia;

namespace LViz.App.Ring;

/// <summary>
/// Every size the radial key editor ("key ring") uses, in pixels at scale 1.
/// Angles elsewhere are radians with 0 = straight up, increasing clockwise.
/// </summary>
public static class RingMetrics
{
    /// <summary>Radius of the centre disc that shows the hovered key.</summary>
    public const double CenterRadius = 58;

    /// <summary>Inner radius of the main key ring.</summary>
    public const double RingInner = 64;

    /// <summary>Outer radius of the main key ring.</summary>
    public const double RingOuter = 160;

    /// <summary>Inner radius of the modifier sub-ring.</summary>
    public const double ModsInner = 64;

    /// <summary>Outer radius of the modifier sub-ring.</summary>
    public const double ModsOuter = 102;

    /// <summary>Inner radius of the behavior-group sub-ring.</summary>
    public const double GroupsInner = 106;

    /// <summary>Outer radius of the behavior-group sub-ring.</summary>
    public const double GroupsOuter = 160;

    /// <summary>Inner radius of the outer item band (layers, profile numbers).</summary>
    public const double BandInner = 171;

    /// <summary>Outer radius of the outer item band.</summary>
    public const double BandOuter = 219;

    /// <summary>Radius of the circular backdrop drawn behind the rings.</summary>
    public const double BackdropRadius = 165;

    /// <summary>Inner radius of the backdrop arc behind the outer band.</summary>
    public const double BandBackdropInner = 166;

    /// <summary>Outer radius of the backdrop arc behind the outer band.</summary>
    public const double BandBackdropOuter = 224;

    /// <summary>Around-band slack added to the band backdrop, px.</summary>
    public const double BandBackdropPad = 5;

    /// <summary>Constant pixel gap between neighbouring segments.</summary>
    public const double Gap = 3;

    /// <summary>Corner radius of a normal segment.</summary>
    public const double Corner = 5;

    /// <summary>Corner radius of a backdrop segment.</summary>
    public const double BackdropCorner = 10;

    /// <summary>Smallest width of a band item, px at the band radius.</summary>
    public const double BandItemMin = 60;

    /// <summary>Horizontal text padding inside a band item, each side, px.</summary>
    public const double BandItemPad = 12;

    /// <summary>Wedge angle the outer band may occupy, degrees.</summary>
    public const double BandMaxSpanDegrees = 200;

    /// <summary>Furthest any ring drawing reaches from the centre.</summary>
    public const double ExtentRadius = 224;

    /// <summary>Height of the breadcrumb strip above the ring.</summary>
    public const double CrumbHeight = 24;

    /// <summary>Gap between the breadcrumb strip and the ring extent.</summary>
    public const double CrumbGap = 12;

    /// <summary>Height of the hint line below the ring.</summary>
    public const double HintHeight = 22;

    /// <summary>Gap between the hint line and the ring extent.</summary>
    public const double HintGap = 8;

    /// <summary>Left half of the ring's bounding frame, px at scale 1.</summary>
    public const double FrameLeft = 224;

    /// <summary>Right half of the ring's bounding frame, px at scale 1.</summary>
    public const double FrameRight = 224;

    /// <summary>Frame above the centre: extent plus breadcrumb gap and strip.</summary>
    public const double FrameTop = ExtentRadius + CrumbGap + CrumbHeight;

    /// <summary>Frame below the centre: extent plus hint gap and line.</summary>
    public const double FrameBottom = ExtentRadius + HintGap + HintHeight;

    /// <summary>Smallest distance the whole frame keeps from the window edge.</summary>
    public const double WindowMargin = 12;

    /// <summary>Lowest scale the placement may shrink the ring to.</summary>
    public const double MinScale = 0.75;

    /// <summary>Width of the detail panel shown beside the ring.</summary>
    public const double PanelWidth = 260;

    /// <summary>Height of the detail panel shown beside the ring.</summary>
    public const double PanelHeight = 300;

    /// <summary>Gap between the backdrop edge and the detail panel.</summary>
    public const double PanelGap = 16;

    /// <summary>Key-to-centre distance under which no tether line is drawn.</summary>
    public const double TetherMinDistance = BackdropRadius + 30;

    /// <summary>How far the tether starts past the key, px at scale 1.</summary>
    public const double TetherKeyInset = 32;

    /// <summary>Half-extents of the ring's bounding frame, per side, at scale 1.</summary>
    public static Thickness Frame => new(FrameLeft, FrameTop, FrameRight, FrameBottom);
}

/// <summary>
/// The eight tangent points of one annular-sector segment outline, in drawing
/// order, relative to the ring centre. The path runs: start at
/// <see cref="OuterEdge0"/> → corner arc (radius <see cref="Corner"/>,
/// clockwise) → <see cref="OuterArc0"/> → outer arc (radius <see cref="Outer"/>,
/// clockwise, <see cref="IsLargeArc"/>) → <see cref="OuterArc1"/> → corner arc →
/// <see cref="OuterEdge1"/> → line → <see cref="InnerEdge1"/> → corner arc →
/// <see cref="InnerArc1"/> → inner arc (radius <see cref="Inner"/>,
/// counter-clockwise, <see cref="IsLargeArc"/>) → <see cref="InnerArc0"/> →
/// corner arc → <see cref="InnerEdge0"/> → close. All corner arcs are clockwise.
/// </summary>
public readonly record struct SectorOutline(
    Point OuterEdge0,
    Point OuterArc0,
    Point OuterArc1,
    Point OuterEdge1,
    Point InnerEdge1,
    Point InnerArc1,
    Point InnerArc0,
    Point InnerEdge0,
    double Inner,
    double Outer,
    double Corner,
    bool IsLargeArc);

/// <summary>Where the ring sits: centre in window coordinates, uniform scale,
/// and whether the frame fits in the area at all.</summary>
public readonly record struct RingPlacement(Point Centre, double Scale, bool Fits);

/// <summary>Which side of the ring the detail panel goes on.</summary>
public enum RingPanelSide
{
    Right,
    Left,
}

/// <summary>Detail panel box in window coordinates. <see cref="ShiftX"/> is how
/// far the ring itself must slide (negative = left) for the panel to fit; 0 when
/// the ring stays where it was placed.</summary>
public readonly record struct RingPanelPlacement(RingPanelSide Side, double ShiftX, double Left, double Top);

/// <summary>
/// Pure maths for the radial key editor: angles (0 = up, clockwise),
/// constant-pixel-gap sector outlines, polar hit-testing, band layout and
/// window placement. No drawing, no Avalonia types beyond Point/Rect/Thickness.
/// </summary>
public static class RingGeometry
{
    private const double Tau = 2 * Math.PI;

    /// <summary>Point at radius <paramref name="radius"/>, angle
    /// <paramref name="angle"/> (0 = up, clockwise): (r·sin a, −r·cos a).</summary>
    public static Point Polar(double radius, double angle) =>
        new(radius * Math.Sin(angle), -radius * Math.Cos(angle));

    /// <summary>Angle of the offset (x, y) from the centre, normalised to [0, 2π).</summary>
    public static double AngleOf(double x, double y) => Normalize(Math.Atan2(x, -y));

    /// <summary>Folds <paramref name="angle"/> into [0, 2π).</summary>
    public static double Normalize(double angle)
    {
        var a = angle % Tau;
        return a < 0 ? a + Tau : a;
    }

    /// <summary>Start angle that centres segment 0 at the top of an n-segment ring.</summary>
    public static double CenteredStart(int count)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        return -Math.PI / count;
    }

    /// <summary>The [start, end) span of segment <paramref name="index"/> of an
    /// n-segment ring whose first boundary is <paramref name="start"/>.</summary>
    public static (double Start, double End) SegmentSpan(int count, int index, double start)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var step = Tau / count;
        return (start + index * step, start + (index + 1) * step);
    }

    /// <summary>True when <paramref name="angle"/> lies in [start, end) modulo 2π.
    /// Spans may start negative or run past 2π.</summary>
    public static bool InSpan(double angle, double start, double end) =>
        Normalize(angle - start) < Normalize(end - start);

    /// <summary>The index of the segment whose span holds <paramref name="angle"/>,
    /// or −1 when none does (only reachable for degenerate input).</summary>
    public static int SegmentAt(double angle, int count, double start)
    {
        for (var i = 0; i < count; i++)
        {
            var (s, e) = SegmentSpan(count, i, start);
            if (InSpan(angle, s, e))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Outline of one annular-sector segment from <paramref name="start"/> to
    /// <paramref name="end"/> between radii <paramref name="inner"/> and
    /// <paramref name="outer"/>. The <paramref name="gap"/> is a constant pixel
    /// width at every radius: each edge line is parallel to its boundary ray,
    /// gap/2 inside it. Corners round with radius <paramref name="corner"/>.
    /// With corner = 0 and gap = 0 the arc points are exactly the polar points.
    /// </summary>
    public static SectorOutline Sector(double inner, double outer, double start, double end, double gap, double corner)
    {
        var (outerEdge0, outerArc0) = Corner(outer - corner, outer, start, gap, corner, side: 1);
        var (outerEdge1, outerArc1) = Corner(outer - corner, outer, end, gap, corner, side: -1);
        var (innerEdge1, innerArc1) = Corner(inner + corner, inner, end, gap, corner, side: -1);
        var (innerEdge0, innerArc0) = Corner(inner + corner, inner, start, gap, corner, side: 1);
        return new SectorOutline(
            outerEdge0, outerArc0, outerArc1, outerEdge1,
            innerEdge1, innerArc1, innerArc0, innerEdge0,
            inner, outer, corner, end - start > Math.PI);
    }

    /// <summary>Edge tangent point and ring-arc point of one corner circle at
    /// boundary ray <paramref name="angle"/> on side <paramref name="side"/>
    /// (+1 at start, −1 at end). The corner circle has radius
    /// <paramref name="corner"/>, its centre sits at distance
    /// <paramref name="d"/> (= outer − corner or inner + corner) from the ring
    /// centre on the bisector of the edge line (distance h = gap/2) and the
    /// circle of radius d; the arc point is that centre scaled onto the ring
    /// circle of radius <paramref name="ring"/>. With corner = 0 and gap = 0 the
    /// centre collapses onto the ring centre and the arc point is the polar point.</summary>
    private static (Point Edge, Point Arc) Corner(
        double d, double ring, double angle, double gap, double corner, double side)
    {
        var h = gap / 2;
        var u = new Point(Math.Sin(angle), -Math.Cos(angle));
        var n = new Point(Math.Cos(angle), Math.Sin(angle));
        var t = Math.Sqrt(Math.Max(0, d * d - (h + corner) * (h + corner)));
        var centre = t * u + (side * (h + corner)) * n;
        var edge = t * u + (side * h) * n;

        var length = Math.Sqrt(centre.X * centre.X + centre.Y * centre.Y);
        Point arc;
        if (length < 1e-12)
        {
            arc = Polar(ring, angle);
        }
        else
        {
            var k = ring / length;
            arc = new Point(centre.X * k, centre.Y * k);
        }

        return (edge, arc);
    }

    /// <summary>Width of a band item holding <paramref name="textWidth"/> px of text.</summary>
    public static double BandItemWidth(double textWidth) =>
        Math.Max(RingMetrics.BandItemMin, textWidth + 2 * RingMetrics.BandItemPad);

    /// <summary>
    /// Band item spans (radians) for items of the given pixel widths at
    /// <paramref name="radius"/>. Item 0 is centred on <paramref name="centre"/>;
    /// item 1 then goes clockwise of everything placed so far, item 2
    /// counter-clockwise, item 3 clockwise, and so on. Results are in item order.
    /// </summary>
    public static IReadOnlyList<(double Start, double End)> BandCentreOut(
        double centre, IReadOnlyList<double> widths, double radius)
    {
        var spans = new List<(double Start, double End)>(widths.Count);
        var clockwiseEnd = centre;
        var counterClockwiseStart = centre;
        for (var i = 0; i < widths.Count; i++)
        {
            var half = widths[i] / radius / 2;
            if (i == 0)
            {
                spans.Add((centre - half, centre + half));
                clockwiseEnd = centre + half;
                counterClockwiseStart = centre - half;
            }
            else if (i % 2 == 1)
            {
                var w = widths[i] / radius;
                spans.Add((clockwiseEnd, clockwiseEnd + w));
                clockwiseEnd += w;
            }
            else
            {
                var w = widths[i] / radius;
                spans.Add((counterClockwiseStart - w, counterClockwiseStart));
                counterClockwiseStart -= w;
            }
        }

        return spans;
    }

    /// <summary>Band item spans placed side by side clockwise in item order, the
    /// whole run centred on <paramref name="centre"/> (profile numbers 0–4).</summary>
    public static IReadOnlyList<(double Start, double End)> BandInOrder(
        double centre, IReadOnlyList<double> widths, double radius)
    {
        var total = 0.0;
        for (var i = 0; i < widths.Count; i++)
        {
            total += widths[i] / radius;
        }

        var spans = new List<(double Start, double End)>(widths.Count);
        var cursor = centre - total / 2;
        for (var i = 0; i < widths.Count; i++)
        {
            var w = widths[i] / radius;
            spans.Add((cursor, cursor + w));
            cursor += w;
        }

        return spans;
    }

    /// <summary>Angular span from the earliest start to the latest end; 0 when empty.</summary>
    public static double SpanOf(IReadOnlyList<(double Start, double End)> spans)
    {
        if (spans.Count == 0)
        {
            return 0;
        }

        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var (start, end) in spans)
        {
            if (start < min)
            {
                min = start;
            }

            if (end > max)
            {
                max = end;
            }
        }

        return max - min;
    }

    /// <summary>Place the ring for the key at <paramref name="anchor"/> inside
    /// <paramref name="area"/>, using the standard frame, margin and minimum scale.</summary>
    public static RingPlacement Place(Rect area, Point anchor) =>
        Place(area, anchor, RingMetrics.Frame, RingMetrics.WindowMargin, RingMetrics.MinScale);

    /// <summary>
    /// Centre the ring on the anchor, scale down only if the frame does not fit
    /// (never below <paramref name="minScale"/>; then <c>Fits</c> is false and the
    /// caller falls back), otherwise slide the anchor the shortest way until the
    /// whole frame sits <paramref name="margin"/> inside the area.
    /// </summary>
    public static RingPlacement Place(Rect area, Point anchor, Thickness frame, double margin, double minScale)
    {
        var scale = Math.Min(1, Math.Min(
            (area.Width - 2 * margin) / (frame.Left + frame.Right),
            (area.Height - 2 * margin) / (frame.Top + frame.Bottom)));
        if (scale < minScale)
        {
            return new RingPlacement(anchor, scale, Fits: false);
        }

        var x = Math.Clamp(anchor.X,
            area.X + margin + frame.Left * scale,
            area.Right - margin - frame.Right * scale);
        var y = Math.Clamp(anchor.Y,
            area.Y + margin + frame.Top * scale,
            area.Bottom - margin - frame.Bottom * scale);
        return new RingPlacement(new Point(x, y), scale, Fits: true);
    }

    /// <summary>
    /// Where to put the detail panel beside a placed ring: prefer right, then
    /// left; if neither fits, shift the ring left (reported in
    /// <see cref="RingPanelPlacement.ShiftX"/>) so the panel fits on the right.
    /// Null when even that is impossible.
    /// </summary>
    public static RingPanelPlacement? PlacePanel(Rect area, RingPlacement placement)
    {
        var s = placement.Scale;
        var m = RingMetrics.WindowMargin;
        var needed = RingMetrics.BackdropRadius + RingMetrics.PanelGap + RingMetrics.PanelWidth;
        var w = needed * s;
        var top = Math.Clamp(
            placement.Centre.Y - RingMetrics.PanelHeight / 2 * s,
            area.Y + m,
            Math.Max(area.Y + m, area.Bottom - m - RingMetrics.PanelHeight * s));
        var leftOfPanel = placement.Centre.X + (RingMetrics.BackdropRadius + RingMetrics.PanelGap) * s;

        if (placement.Centre.X + w <= area.Right - m)
        {
            return new RingPanelPlacement(RingPanelSide.Right, 0, leftOfPanel, top);
        }

        if (placement.Centre.X - w >= area.X + m)
        {
            return new RingPanelPlacement(RingPanelSide.Left, 0, placement.Centre.X - w, top);
        }

        var newX = area.Right - m - w;
        if (newX - RingMetrics.FrameLeft * s >= area.X + m)
        {
            return new RingPanelPlacement(
                RingPanelSide.Right,
                newX - placement.Centre.X,
                newX + (RingMetrics.BackdropRadius + RingMetrics.PanelGap) * s,
                top);
        }

        return null;
    }

    /// <summary>The tether line from the anchor key to the ring, or null when the
    /// key is close enough to the centre that a line would be noise.</summary>
    public static (Point From, Point To)? Tether(Point key, RingPlacement placement)
    {
        var dx = placement.Centre.X - key.X;
        var dy = placement.Centre.Y - key.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= RingMetrics.TetherMinDistance * placement.Scale)
        {
            return null;
        }

        var vx = dx / distance;
        var vy = dy / distance;
        var s = placement.Scale;
        return (
            new Point(key.X + vx * RingMetrics.TetherKeyInset * s, key.Y + vy * RingMetrics.TetherKeyInset * s),
            new Point(placement.Centre.X - vx * (RingMetrics.BackdropRadius + 4) * s,
                placement.Centre.Y - vy * (RingMetrics.BackdropRadius + 4) * s));
    }
}
