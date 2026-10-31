using LViz.Core.Layout;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Layout.Import;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary>
/// Tests for <see cref="UserLayoutGeometry"/> (layout editing, part 3): rotated
/// centres and corners, bounds, hit-testing, snap/normalisation maths, the
/// move/rotate/hand operations on key lists, and the normalisation saved
/// drafts go through. <see cref="K"/> is a 100×100 key in centi-u.
/// </summary>
public sealed class UserLayoutGeometryTests
{
    // ── centres & containment ───────────────────────────────────────────

    [Fact]
    public void RotatedCenter_Unrotated_IsRectCentre()
    {
        var key = K(0, 0);

        var (cx, cy) = UserLayoutGeometry.RotatedCenter(key);

        Assert.Equal(50, cx, 6);
        Assert.Equal(50, cy, 6);
        Assert.True(UserLayoutGeometry.Contains(key, 50, 50));
        Assert.False(UserLayoutGeometry.Contains(key, 101, 50));
    }

    [Fact]
    public void RotatedCenter_90AboutOrigin_SwingsCentreLeft()
    {
        var key = K(0, 0, 9000, 0, 0);

        var (cx, cy) = UserLayoutGeometry.RotatedCenter(key);

        Assert.Equal(-50, cx, 6);
        Assert.Equal(50, cy, 6);
        Assert.True(UserLayoutGeometry.Contains(key, -50, 50));
        Assert.False(UserLayoutGeometry.Contains(key, 50, 50));
    }

    [Fact]
    public void RotatedBounds_TiltedKey_UsesRotatedCorners()
    {
        var (minX, minY, maxX, maxY) =
            UserLayoutGeometry.RotatedBounds([K(0, 0, 4500, 50, 50)]);

        Assert.Equal(-20.71, minX, 2);
        Assert.Equal(-20.71, minY, 2);
        Assert.Equal(120.71, maxX, 2);
        Assert.Equal(120.71, maxY, 2);
    }

    // ── selection ───────────────────────────────────────────────────────

    [Fact]
    public void HitTest_ReturnsHighestContainingIndex_OrMinusOne()
    {
        var keys = new List<UserLayoutKey> { K(0, 0), K(50, 0) };

        Assert.Equal(1, UserLayoutGeometry.HitTest(keys, 60, 50));
        Assert.Equal(0, UserLayoutGeometry.HitTest(keys, 20, 50));
        Assert.Equal(-1, UserLayoutGeometry.HitTest(keys, 300, 300));
    }

    [Fact]
    public void InBox_SelectsCentresInclusively_CornersInAnyOrder()
    {
        var keys = new List<UserLayoutKey> { K(0, 0), K(100, 0), K(400, 0) };

        Assert.Equal([0, 1], UserLayoutGeometry.InBox(keys, -10, -10, 210, 110));
        Assert.Equal([0, 1], UserLayoutGeometry.InBox(keys, 210, 110, -10, -10));
    }

    // ── snap & angle maths ──────────────────────────────────────────────

    [Theory]
    [InlineData(37.0, 25, 25)]
    [InlineData(38.0, 25, 50)]
    [InlineData(-13.0, 25, -25)]
    public void Snap_RoundsAwayFromZeroToTheStep(double value, int step, int expected)
    {
        Assert.Equal(expected, UserLayoutGeometry.Snap(value, step));
    }

    [Theory]
    [InlineData(19000, -17000)]
    [InlineData(-18000, 18000)]
    [InlineData(36500, 500)]
    public void NormalizeRotation_WrapsIntoOpenMinusClosedPlus(int centiDegrees, int expected)
    {
        var result = UserLayoutGeometry.NormalizeRotation(centiDegrees);

        Assert.Equal(expected, result);
        Assert.True(result > -18000 && result <= 18000);
    }

    // ── move & rotate ───────────────────────────────────────────────────

    [Fact]
    public void Move_MovesRotatedPivots_KeepsUnrotatedPivots_AndUntouchedKeys()
    {
        var keys = new List<UserLayoutKey>
        {
            new(0, 0, 100, 100, 9000, 250, 50, Hand.Left),
            new(200, 0, 100, 100, 0, 42, 43, Hand.Right),
            new(400, 0, 100, 100, 9000, 450, 50, Hand.Right),
        };

        var moved = UserLayoutGeometry.Move(keys, [0, 1], 30, -20);

        Assert.Equal((30, -20, 280, 30), (moved[0].X, moved[0].Y, moved[0].Rx, moved[0].Ry));
        Assert.Equal((230, -20, 42, 43), (moved[1].X, moved[1].Y, moved[1].Rx, moved[1].Ry));
        Assert.Equal(keys[2], moved[2]);
        // the source list is left alone
        Assert.Equal((0, 0, 250, 50), (keys[0].X, keys[0].Y, keys[0].Rx, keys[0].Ry));
    }

    [Fact]
    public void Rotate_AboutItsOwnCentre_KeepsTheKeyInPlace_AndPivotsIt()
    {
        var result = UserLayoutGeometry.Rotate([K(0, 0)], [0], 9000, 50, 50);

        Assert.Equal((0, 0, 9000, 50, 50),
            (result[0].X, result[0].Y, result[0].R, result[0].Rx, result[0].Ry));
    }

    [Fact]
    public void Rotate_AboutTheOrigin_SwingsTheWholeKey()
    {
        var result = UserLayoutGeometry.Rotate([K(0, 0)], [0], 9000, 0, 0);

        var (cx, cy) = UserLayoutGeometry.RotatedCenter(result[0]);
        Assert.Equal(-50, cx, 6);
        Assert.Equal(50, cy, 6);
        Assert.Equal((-100, 0, 9000), (result[0].X, result[0].Y, result[0].R));
    }

    [Fact]
    public void Rotate_PlusMinus90_CancelsBackToAnUnrotatedKey()
    {
        var once = UserLayoutGeometry.Rotate([K(0, 0)], [0], 9000, 0, 0);

        var twice = UserLayoutGeometry.Rotate(once, [0], -9000, 0, 0);

        Assert.Equal(K(0, 0), twice[0]);
    }

    // ── hands & split line ──────────────────────────────────────────────

    [Fact]
    public void ApplyHands_CentreExactlyOnTheSplit_IsRight()
    {
        var result = UserLayoutGeometry.ApplyHands([K(0, 0), K(100, 0)], 150);

        Assert.Equal(Hand.Left, result[0].Hand);
        // its centre 150 is not < 150
        Assert.Equal(Hand.Right, result[1].Hand);
    }

    [Fact]
    public void InitialSplitX_SitsInTheMiddleOfTheCleanGap()
    {
        var keys = new List<UserLayoutKey>
        {
            K(0, 0),
            K(100, 0),
            K(400, 0) with { Hand = Hand.Right },
            K(500, 0) with { Hand = Hand.Right },
        };

        // left centres ≤ 150, right centres from 450 → midpoint 300.
        Assert.Equal(300, UserLayoutGeometry.InitialSplitX(keys));
    }

    [Fact]
    public void InitialSplitX_InterleavedHands_FallBackToTheMidline()
    {
        var keys = new List<UserLayoutKey>
        {
            K(0, 0) with { Hand = Hand.Right },
            K(200, 0),
            K(300, 0) with { Hand = Hand.Right },
        };

        // left centre 250 is not left of right centre 50 — midline rule.
        var dtsi = new List<DtsiKeyPhysical>
        {
            new(100, 100, 0, 0, 0, 0, 0),
            new(100, 100, 200, 0, 0, 0, 0),
            new(100, 100, 300, 0, 0, 0, 0),
        };
        Assert.Equal(UserLayoutDraft.FindMidlineCentiU(dtsi),
            UserLayoutGeometry.InitialSplitX(keys));
    }

    // ── normalisation ───────────────────────────────────────────────────

    [Fact]
    public void Normalize_FlatBoard_StartsAtTheOrigin()
    {
        var result = UserLayoutGeometry.Normalize([K(500, 300)]);

        Assert.Equal((0, 0), (result[0].X, result[0].Y));
    }

    [Fact]
    public void Normalize_TiltedKey_KeepsTheWholeKeyOnTheCanvas()
    {
        var result = UserLayoutGeometry.Normalize([K(0, 0, 4500, 50, 50)]);

        // rotated bounds start at -20.71 → floor shifts everything by +21.
        Assert.Equal((21, 21, 4500, 71, 71),
            (result[0].X, result[0].Y, result[0].R, result[0].Rx, result[0].Ry));
        var (minX, minY, _, _) = UserLayoutGeometry.RotatedBounds(result);
        Assert.True(minX >= 0 && minY >= 0,
            $"tilted key is cut off: rotated bounds start at ({minX}, {minY})");
    }

    /// <summary>A 100×100 left-hand key, optionally rotated about (rx, ry).</summary>
    private static UserLayoutKey K(int x, int y, int r = 0, int rx = 0, int ry = 0) =>
        new(x, y, 100, 100, r, rx, ry, Hand.Left);
}
