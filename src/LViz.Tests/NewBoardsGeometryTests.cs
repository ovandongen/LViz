using LViz.Core.Layout;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Per-board geometry pins for the 6 boards added in the §4 multi-profile
/// expansion. Each board asserts its declared key count, the left/right
/// split where applicable, and the presence of thumb rotation where the
/// dtsi declares it. Universal invariants (finite sizes, fits canvas) are
/// already covered by <see cref="AllProfilesGeometryTheory"/>.
/// </summary>
public class NewBoardsGeometryTests
{
    [Fact]
    public void ADux_Has34Keys_SplitEqually()
    {
        var p = new ADuxProfile();
        Assert.Equal(34, p.Keys.Count);
        Assert.Equal(17, p.Keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(17, p.Keys.Count(k => k.Hand == Hand.Right));
        ProfileGeometryAssertions.AssertHalvesSplitByMidline(p);
        // a_dux is the only registered board with no rotated keys — assert
        // that's still true so a future dtsi edit can't silently add one
        // without bumping the docstring.
        Assert.All(p.Keys, k => Assert.Equal(0, k.RotationDegrees));
    }

    [Fact]
    public void Kyria_Has50Keys_SplitEqually_WithRotatedThumbs()
    {
        var p = new KyriaProfile();
        Assert.Equal(50, p.Keys.Count);
        Assert.Equal(25, p.Keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(25, p.Keys.Count(k => k.Hand == Hand.Right));
        ProfileGeometryAssertions.AssertHalvesSplitByMidline(p);
        ProfileGeometryAssertions.AssertThumbKeysRotated(p, thumbCandidateCount: 14);
    }

    [Fact]
    public void Lily58_Has58Keys_SplitEqually_WithRotatedThumbs()
    {
        var p = new Lily58Profile();
        Assert.Equal(58, p.Keys.Count);
        Assert.Equal(29, p.Keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(29, p.Keys.Count(k => k.Hand == Hand.Right));
        ProfileGeometryAssertions.AssertHalvesSplitByMidline(p);
        // Lily58's two 1.5u rotated thumbs (idx 53,54) sit at y=400 — higher
        // up than the plain bottom-row thumbs at y=412..425 — so the default
        // bottom-6 candidate window misses them. Widen to 8.
        ProfileGeometryAssertions.AssertThumbKeysRotated(p, thumbCandidateCount: 8);
    }

    [Fact]
    public void Sofle_Has60Keys_SplitEqually_WithRotatedThumbs()
    {
        var p = new SofleProfile();
        Assert.Equal(60, p.Keys.Count);
        Assert.Equal(30, p.Keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(30, p.Keys.Count(k => k.Hand == Hand.Right));
        ProfileGeometryAssertions.AssertHalvesSplitByMidline(p);
        ProfileGeometryAssertions.AssertThumbKeysRotated(p);
    }

    [Fact]
    public void Go60_Has60Keys_SplitEqually_WithRotatedThumbs()
    {
        // GO60 is a unibody board visually, but its geometry (left cluster at
        // x=0..500, right cluster at x=1150..1650) still splits cleanly about
        // the chosen midline — verified here so tooltip handedness stays
        // correct.
        var p = new Go60Profile();
        Assert.Equal(60, p.Keys.Count);
        Assert.Equal(30, p.Keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(30, p.Keys.Count(k => k.Hand == Hand.Right));
        ProfileGeometryAssertions.AssertHalvesSplitByMidline(p);
        ProfileGeometryAssertions.AssertThumbKeysRotated(p);
    }

    [Fact]
    public void Glove80_Has80Keys_SplitEqually_WithTwoRotatedThumbRows()
    {
        var p = new Glove80Profile();
        Assert.Equal(80, p.Keys.Count);
        Assert.Equal(40, p.Keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(40, p.Keys.Count(k => k.Hand == Hand.Right));
        ProfileGeometryAssertions.AssertHalvesSplitByMidline(p);

        // Both thumb rows (52–57 row A, 69–74 row B) must be rotated — guard
        // against the dtsi parser silently dropping rx/ry on either row.
        var rowA = p.Keys.Where(k => k.Index >= 52 && k.Index <= 57).ToList();
        var rowB = p.Keys.Where(k => k.Index >= 69 && k.Index <= 74).ToList();
        Assert.All(rowA, k => Assert.NotEqual(0, k.RotationDegrees));
        Assert.All(rowB, k => Assert.NotEqual(0, k.RotationDegrees));

        // All left-thumb keys (row A 52-54, row B 69-71) must share the
        // anatomical pivot the docstring promises — (450, 925) centi-u maps
        // to (300, 615) px under the standard 0.6 px/centi-u + (30,60) margin.
        var leftThumbs = p.Keys.Where(k => k.Index is 52 or 53 or 54 or 69 or 70 or 71).ToList();
        Assert.All(leftThumbs, k =>
        {
            Assert.NotNull(k.RotationOriginX);
            Assert.NotNull(k.RotationOriginY);
            Assert.Equal(300.0, k.RotationOriginX!.Value, 3);
            Assert.Equal(615.0, k.RotationOriginY!.Value, 3);
        });
    }
}
