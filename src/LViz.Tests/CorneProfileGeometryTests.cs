using LViz.Core.Layout;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Sanity checks on the Corne profile geometry parsed out of
/// <c>Resources/corne.dtsi</c>. Pins the key count, the left/right split,
/// canvas extents, and the thumb-cluster rotation — the things a botched
/// dtsi edit would silently break without these tests. Shares invariants
/// with <see cref="ProfileGeometryAssertions"/>.
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
    public void EveryKeyHasFiniteNonZeroSize() =>
        ProfileGeometryAssertions.AssertFiniteNonZeroSizes(new CorneProfile());

    [Fact]
    public void KeysFitInsideCanvas() =>
        ProfileGeometryAssertions.AssertKeysFitCanvas(new CorneProfile());

    [Fact]
    public void LeftAndRightHalvesSitOnOppositeSidesOfMidline() =>
        ProfileGeometryAssertions.AssertHalvesSplitByMidline(new CorneProfile());

    [Fact]
    public void ThumbKeysAreRotated() =>
        ProfileGeometryAssertions.AssertThumbKeysRotated(new CorneProfile());
}
