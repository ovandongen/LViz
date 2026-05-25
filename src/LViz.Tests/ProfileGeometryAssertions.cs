using LViz.Core.Layout;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Shared geometry invariants every <see cref="IKeyboardProfile"/> must hold.
/// Per-board test classes call these helpers + assert board-specific numbers
/// (key count, expected thumb indices) so adding board N+1 stays cheap.
/// </summary>
internal static class ProfileGeometryAssertions
{
    public static void AssertFiniteNonZeroSizes(IKeyboardProfile p) =>
        Assert.All(p.Keys, k =>
        {
            Assert.True(double.IsFinite(k.X) && double.IsFinite(k.Y),
                $"Key {k.Index} has non-finite coords ({k.X},{k.Y})");
            Assert.True(k.Width > 0 && k.Height > 0,
                $"Key {k.Index} has non-positive size ({k.Width}x{k.Height})");
        });

    public static void AssertKeysFitCanvas(IKeyboardProfile p) =>
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

    public static void AssertHalvesSplitByMidline(IKeyboardProfile p)
    {
        var leftCentre = p.Keys.Where(k => k.Hand == Hand.Left).Average(k => k.X + k.Width / 2);
        var rightCentre = p.Keys.Where(k => k.Hand == Hand.Right).Average(k => k.X + k.Width / 2);
        Assert.True(leftCentre < rightCentre,
            $"Left-hand centroid should be left of right-hand centroid; got {leftCentre} vs {rightCentre}");
    }

    /// <summary>
    /// Asserts at least one key among the bottom-most <paramref name="thumbCandidateCount"/>
    /// keys has non-zero rotation. Used as a guard against the dtsi parser
    /// silently dropping rotation fields. Skip for boards with no rotated
    /// thumb cluster (e.g. unibody ortholinear).
    /// </summary>
    public static void AssertThumbKeysRotated(IKeyboardProfile p, int thumbCandidateCount = 6)
    {
        var thumbs = p.Keys.OrderByDescending(k => k.Y).Take(thumbCandidateCount).ToList();
        Assert.Contains(thumbs, k => k.RotationDegrees != 0);
    }
}
