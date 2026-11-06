using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using LViz.App.Ring;
using Xunit;

namespace LViz.Tests.Ring;

/// <summary>
/// Pure-maths checks for the radial key editor: the 0-up/clockwise angle
/// convention, constant-pixel-gap sector tangency, band layout, window
/// placement and the tether line. All numbers come from the key-ring
/// reference; tolerances are 1e-6 unless a test says otherwise.
/// </summary>
public class RingGeometryTests
{
    private const double Tol = 1e-6;

    private static void Close(double expected, double actual, double tol = Tol) =>
        Assert.True(Math.Abs(expected - actual) <= tol,
            $"expected {expected} ± {tol}, got {actual}");

    private static void Close(Point expected, Point actual, double tol = Tol)
    {
        Close(expected.X, actual.X, tol);
        Close(expected.Y, actual.Y, tol);
    }

    private static double Length(Point p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);

    [Fact]
    public void Polar_ZeroIsUp_QuarterTurnIsRight()
    {
        Close(new Point(0, -10), RingGeometry.Polar(10, 0));
        Close(new Point(10, 0), RingGeometry.Polar(10, Math.PI / 2));
    }

    [Fact]
    public void AngleOf_CardinalDirections()
    {
        Close(0, RingGeometry.AngleOf(0, -1));
        Close(Math.PI / 2, RingGeometry.AngleOf(1, 0));
        Close(3 * Math.PI / 2, RingGeometry.AngleOf(-1, 0));
        Close(Math.PI, RingGeometry.AngleOf(0, 1));
    }

    [Fact]
    public void CenteredStart_RejectsNonPositiveCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RingGeometry.CenteredStart(0));
    }

    [Fact]
    public void SegmentAt_TopMiddleAndWrap()
    {
        Assert.Equal(0, RingGeometry.SegmentAt(0, 10, RingGeometry.CenteredStart(10)));
        Assert.Equal(1, RingGeometry.SegmentAt(Math.PI / 2, 4, -Math.PI / 4));
        Assert.Equal(0, RingGeometry.SegmentAt(2 * Math.PI - 0.01, 10, RingGeometry.CenteredStart(10)));
    }

    [Fact]
    public void SegmentSpan_UsesEvenDivisionOfFullTurn()
    {
        var (start, end) = RingGeometry.SegmentSpan(4, 2, 0);
        Close(Math.PI, start);
        Close(3 * Math.PI / 2, end);
    }

    [Fact]
    public void InSpan_WrapsAcrossZero()
    {
        Assert.True(RingGeometry.InSpan(0.1, -0.2, 0.2));
        Assert.True(RingGeometry.InSpan(2 * Math.PI - 0.1, -0.2, 0.2));
        Assert.False(RingGeometry.InSpan(0.3, -0.2, 0.2));
    }

    [Fact]
    public void Sector_GapEdgesAndArcPointsStayOnTheirCircles()
    {
        var s = RingGeometry.Sector(64, 160, 0, Math.PI / 2, 3, 5);
        Close(160, Length(s.OuterArc0));
        Close(64, Length(s.InnerArc0));
        Close(160, Length(s.OuterArc1));
        Close(1.5, s.OuterEdge0.X);
        Close(1.5, s.InnerEdge0.X);
        Close(-1.5, s.OuterEdge1.Y);
        Assert.False(s.IsLargeArc);
    }

    [Fact]
    public void Sector_ZeroCornerZeroGap_IsExactPolar()
    {
        var s = RingGeometry.Sector(64, 160, 0, Math.PI / 2, 0, 0);
        Close(RingGeometry.Polar(160, 0), s.OuterArc0);
        Close(RingGeometry.Polar(64, Math.PI / 2), s.InnerArc1);
    }

    [Fact]
    public void Sector_WideSpan_IsLargeArc()
    {
        var s = RingGeometry.Sector(64, 160, 0, Math.PI + 0.1, 3, 5);
        Assert.True(s.IsLargeArc);
    }

    [Fact]
    public void BandCentreOut_AlternatesClockwiseThenCounterClockwise()
    {
        var spans = RingGeometry.BandCentreOut(0, new[] { 60.0, 60.0, 60.0 }, 60);
        AssertSpans(spans, (-0.5, 0.5), (0.5, 1.5), (-1.5, -0.5));
    }

    [Fact]
    public void BandInOrder_SitsSideBySideCentredOnCentre()
    {
        var spans = RingGeometry.BandInOrder(0, new[] { 60.0, 60.0, 60.0 }, 60);
        AssertSpans(spans, (-1.5, -0.5), (-0.5, 0.5), (0.5, 1.5));
        Close(3, RingGeometry.SpanOf(spans));
    }

    [Fact]
    public void BandItemWidth_HonoursMinimum()
    {
        Close(60, RingGeometry.BandItemWidth(10));
        Close(104, RingGeometry.BandItemWidth(80));
    }

    [Fact]
    public void SpanOf_EmptyIsZero()
    {
        Close(0, RingGeometry.SpanOf(Array.Empty<(double, double)>()));
    }

    [Fact]
    public void Place_AnchorWellInsideArea_IsUnchanged()
    {
        var p = RingGeometry.Place(new Rect(0, 0, 1000, 800), new Point(500, 400));
        Close(new Point(500, 400), p.Centre);
        Close(1, p.Scale);
        Assert.True(p.Fits);
    }

    [Fact]
    public void Place_AnchorInCorner_SlidesFrameInsideArea()
    {
        var p = RingGeometry.Place(new Rect(0, 0, 1000, 800), new Point(50, 780));
        Close(new Point(236, 534), p.Centre);
        Close(1, p.Scale);
        Assert.True(p.Fits);
    }

    [Fact]
    public void Place_AreaTooSmall_DoesNotFit()
    {
        var p = RingGeometry.Place(new Rect(0, 0, 600, 400), new Point(300, 200));
        Assert.False(p.Fits);
    }

    [Fact]
    public void Place_MarginallySmallArea_ScalesDownAndStillFits()
    {
        var p = RingGeometry.Place(new Rect(0, 0, 600, 420), new Point(300, 210));
        Assert.True(p.Fits);
        Close(0.77043, p.Scale, 1e-4);
        Close(212.31, p.Centre.Y, 1e-2);
    }

    [Fact]
    public void PlacePanel_RoomOnRight_GoesRight()
    {
        var panel = RingGeometry.PlacePanel(
            new Rect(0, 0, 1000, 800), new RingPlacement(new Point(500, 400), 1, true));
        Assert.NotNull(panel);
        Assert.Equal(RingPanelSide.Right, panel!.Value.Side);
        Close(0, panel.Value.ShiftX);
        Close(681, panel.Value.Left);
        Close(250, panel.Value.Top);
    }

    [Fact]
    public void PlacePanel_RightBlocked_GoesLeft()
    {
        var panel = RingGeometry.PlacePanel(
            new Rect(0, 0, 1000, 800), new RingPlacement(new Point(800, 400), 1, true));
        Assert.NotNull(panel);
        Assert.Equal(RingPanelSide.Left, panel!.Value.Side);
        Close(0, panel.Value.ShiftX);
        Close(359, panel.Value.Left);
        Close(250, panel.Value.Top);
    }

    [Fact]
    public void PlacePanel_NeitherSideFits_ShiftsRingLeftForRightPanel()
    {
        var panel = RingGeometry.PlacePanel(
            new Rect(0, 0, 700, 800), new RingPlacement(new Point(350, 400), 1, true));
        Assert.NotNull(panel);
        Assert.Equal(RingPanelSide.Right, panel!.Value.Side);
        Close(-103, panel.Value.ShiftX);
    }

    [Fact]
    public void PlacePanel_NoRoomEvenAfterShift_ReturnsNull()
    {
        var panel = RingGeometry.PlacePanel(
            new Rect(0, 0, 480, 800), new RingPlacement(new Point(240, 400), 1, true));
        Assert.Null(panel);
    }

    [Fact]
    public void Tether_FarKeyDrawsLine_NearKeyDoesNot()
    {
        var placement = new RingPlacement(new Point(236, 334), 1, true);
        var line = RingGeometry.Tether(new Point(84, 536), placement);
        Assert.NotNull(line);
        Close(32, Length(new Point(line!.Value.From.X - 84, line.Value.From.Y - 536)));

        Assert.Null(RingGeometry.Tether(new Point(240, 340), placement));
    }

    private static void AssertSpans(
        IReadOnlyList<(double Start, double End)> spans,
        params (double Start, double End)[] expected)
    {
        Assert.Equal(expected.Length, spans.Count);
        foreach (var (actual, want) in spans.Zip(expected))
        {
            Close(want.Start, actual.Start);
            Close(want.End, actual.End);
        }
    }
}
