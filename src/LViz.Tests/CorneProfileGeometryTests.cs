using LViz.Core.Layout;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Sanity checks on the Corne profile geometry parsed out of
/// <c>Resources/corne.dtsi</c>. Pins the key count, the left/right split,
/// canvas extents, and the thumb-cluster rotation — the things a botched
/// dtsi edit would silently break without these tests.
/// </summary>
public class CorneProfileGeometryTests
{
    [Fact]
    public void HasFortyTwoKeys_SplitEqually()
    {
        var p = new CorneProfile();

        Assert.Equal(42, p.Keys.Count);
        Assert.Equal(21, p.Keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(21, p.Keys.Count(k => k.Hand == Hand.Right));
    }

    [Fact]
    public void EveryKeyHasFiniteNonZeroSize()
    {
        var p = new CorneProfile();

        Assert.All(p.Keys, k =>
        {
            Assert.True(double.IsFinite(k.X) && double.IsFinite(k.Y),
                $"Key {k.Index} has non-finite coords ({k.X},{k.Y})");
            Assert.True(k.Width > 0 && k.Height > 0,
                $"Key {k.Index} has non-positive size ({k.Width}x{k.Height})");
        });
    }

    [Fact]
    public void KeysFitInsideCanvas()
    {
        var p = new CorneProfile();

        // Use rotation-aware bounds: Corne thumbs are rotated, so the raw
        // (X,Y,X+W,Y+H) rect understates extent.
        Assert.All(p.Keys, k =>
        {
            var b = k.RotatedBounds();
            Assert.True(b.MaxX <= p.CanvasWidth + 0.5,
                $"Key {k.Index} extends past canvas right ({b.MaxX} > {p.CanvasWidth})");
            Assert.True(b.MaxY <= p.CanvasHeight + 0.5,
                $"Key {k.Index} extends past canvas bottom ({b.MaxY} > {p.CanvasHeight})");
            Assert.True(b.MinX >= -0.5, $"Key {k.Index} crosses canvas left ({b.MinX})");
            Assert.True(b.MinY >= -0.5, $"Key {k.Index} crosses canvas top ({b.MinY})");
        });
    }

    [Fact]
    public void LeftAndRightHalvesSitOnOppositeSidesOfMidline()
    {
        var p = new CorneProfile();
        var leftCentre = p.Keys.Where(k => k.Hand == Hand.Left).Average(k => k.X + k.Width / 2);
        var rightCentre = p.Keys.Where(k => k.Hand == Hand.Right).Average(k => k.X + k.Width / 2);

        Assert.True(leftCentre < rightCentre,
            $"Left-hand centroid should be left of right-hand centroid; got {leftCentre} vs {rightCentre}");
    }

    [Fact]
    public void ThumbKeysAreRotated()
    {
        var p = new CorneProfile();

        // Corne thumb keys are the inner-most three on each hand. They sit
        // below the bottom matrix row and are rotated outward — the dtsi
        // specifies rotation, so any thumb with rotation=0 means the parser
        // dropped the field.
        var thumbs = p.Keys.OrderByDescending(k => k.Y).Take(6).ToList();
        Assert.Contains(thumbs, k => k.RotationDegrees != 0);
    }
}
