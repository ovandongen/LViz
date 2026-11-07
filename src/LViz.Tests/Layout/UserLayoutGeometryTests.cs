using LViz.Core.Layout;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Layout.Import;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary>
/// Tests for <see cref="UserLayoutGeometry"/> (layout editing): rotated
/// centres and corners, bounds, hit-testing, snap/normalisation maths, snapping a
/// move-drag to neighbouring keys, the move/rotate/hand operations on key lists,
/// mirroring one half onto the other, and the normalisation saved drafts go
/// through. <see cref="K"/> is a 100×100 key in centi-u.
/// </summary>
public sealed class UserLayoutGeometryTests
{
    /// <summary>The split line of the hand-mirroring fixtures.</summary>
    private const int Split = 300;

    /// <summary>How far a drag edge may still be pulled to a neighbour, in
    /// centi-u — the snapping fixtures' tolerance (about 8 px at the editor's
    /// usual scale).</summary>
    private const double Tolerance = 10;

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

    // ── snapping a move-drag to neighbours ──────────────────────────────

    [Fact]
    public void SnapMove_DroppedOntoAKey_IsPushedFlushTheShortestWay()
    {
        // B's left would land at 50, half over A and far outside the snap
        // distance: the grid alone leaves them overlapping, so B is pushed out
        // to A's right edge (50 to the right beats 150 left or 100 up/down).
        var keys = new List<UserLayoutKey> { K(0, 0), K(300, 0) };

        var result = UserLayoutGeometry.SnapMove(keys, [1], -250, 0, Tolerance);

        Assert.Equal(-200, result.Dx);
        Assert.Equal(100.0, result.GuideX);
        Assert.Equal(0, result.Dy);
    }

    [Fact]
    public void SnapMove_MostlyBelowAKey_IsPushedDown()
    {
        // B ends right under A but 25 into it: down is the short way out.
        var keys = new List<UserLayoutKey> { K(0, 0), K(300, 0) };

        var result = UserLayoutGeometry.SnapMove(keys, [1], -300, 70, Tolerance);

        Assert.Equal(-300, result.Dx);
        Assert.Equal(100, result.Dy);
        Assert.Equal(100.0, result.GuideY);
    }

    [Fact]
    public void SnapMove_DroppedIntoAGap_LandsInTheGapNotOnTheNextKey()
    {
        // A (0..100) and C (200..300) with a 1u hole between them; B dropped at
        // x 150 overlaps C, and pushing it left fills the hole flush with both.
        var keys = new List<UserLayoutKey> { K(0, 0), K(200, 0), K(0, 300) };

        var result = UserLayoutGeometry.SnapMove(keys, [2], 150, -300, Tolerance);

        Assert.Equal(100, result.Dx);
        Assert.Equal(-300, result.Dy);
        Assert.Equal(200.0, result.GuideX);
    }

    [Fact]
    public void SnapMove_OverlapWithNoFreeSideways_TakesTheFreeVerticalFlush()
    {
        // A and C touch; B lands across both. Right of A is C and left of C is
        // A, so the shortest free way out is a full key up (ties keep up).
        var keys = new List<UserLayoutKey> { K(0, 0), K(100, 0), K(300, 0) };

        var result = UserLayoutGeometry.SnapMove(keys, [2], -240, 0, Tolerance);

        Assert.Equal(-250, result.Dx);
        Assert.Equal(-100, result.Dy);
        Assert.Equal(0.0, result.GuideY);
    }

    [Fact]
    public void SnapMove_RotatedKeys_AreNotPushedApart()
    {
        // A's bounding box at 45° reaches 120.71, but the keys may overlap
        // boxes in a thumb cluster: no push, the grid result stands.
        var keys = new List<UserLayoutKey> { K(0, 0, r: 4500, rx: 50, ry: 50), K(300, 0) };

        var result = UserLayoutGeometry.SnapMove(keys, [1], -250, 0, Tolerance);

        Assert.Equal(-250, result.Dx);
        Assert.Equal(0, result.Dy);
    }

    [Fact]
    public void SnapMove_FlushCountsAsAligned_EdgesTouchRatherThanGap()
    {
        // A stays put, B's drag leaves its left at 103 — three units short of
        // touching A, which in ZMK units is where the next key belongs.
        var keys = new List<UserLayoutKey> { K(0, 0), K(300, 0) };

        var result = UserLayoutGeometry.SnapMove(keys, [1], -197, 0, Tolerance);

        Assert.Equal(-200, result.Dx);
        Assert.NotNull(result.GuideX);
        Assert.Equal(100.0, result.GuideX.Value);
        // applying the delta leaves the pair flush, not 3 units apart
        var moved = UserLayoutGeometry.Move(keys, [1], result.Dx, result.Dy);
        Assert.Equal(100, moved[1].X);
        Assert.Equal(0, result.Dy);
    }

    [Fact]
    public void SnapMove_LeftEdgesTieWithCentresAndRights_KeepsTheLeftGuide()
    {
        // B's left would land at 8, so left-on-left, centre-on-centre and
        // right-on-right all ask for the same −8: the first candidate wins. B
        // sits a row lower, so lining up doesn't stack it on A (that would be
        // pushed out as an overlap).
        var keys = new List<UserLayoutKey> { K(0, 0), K(300, 100) };

        var result = UserLayoutGeometry.SnapMove(keys, [1], -292, 0, Tolerance);

        Assert.Equal(-300, result.Dx);
        Assert.NotNull(result.GuideX);
        Assert.Equal(0.0, result.GuideX.Value);
    }

    [Fact]
    public void SnapMove_NothingWithinTolerance_UsesTheGrid()
    {
        var keys = new List<UserLayoutKey> { K(0, 0), K(300, 0) };

        // left at 150 is 50 past A's right edge, far outside the tolerance
        var far = UserLayoutGeometry.SnapMove(keys, [1], -150, 0, Tolerance);
        Assert.Equal(-150, far.Dx);
        Assert.Null(far.GuideX);

        // the grid still does its own half-away rounding, guideless
        var grid = UserLayoutGeometry.SnapMove(keys, [1], -160, 0, Tolerance);
        Assert.Equal(-150, grid.Dx);
        Assert.Null(grid.GuideX);
    }

    [Fact]
    public void SnapMove_VerticalAxis_FlushesUnderTheKey()
    {
        var keys = new List<UserLayoutKey> { K(0, 0), K(0, 300) };

        var result = UserLayoutGeometry.SnapMove(keys, [1], 0, -195, Tolerance);

        Assert.Equal(-200, result.Dy);
        Assert.NotNull(result.GuideY);
        Assert.Equal(100.0, result.GuideY.Value);
        // X never left A's left edge, so it locks there instead of onto the grid
        Assert.Equal(0, result.Dx);
        Assert.NotNull(result.GuideX);
        Assert.Equal(0.0, result.GuideX.Value);
    }

    [Fact]
    public void SnapMove_CentresAlign_WhenTheEdgesAreFurtherAway()
    {
        var keys = new List<UserLayoutKey>
        {
            new(0, 0, 200, 100, 0, 0, 0, Hand.Left),      // A, centre X 100
            new(500, 200, 100, 100, 0, 0, 0, Hand.Right), // B, moving
        };

        // B's centre would stop at 104; its edges are 54 and 46 from A's.
        var result = UserLayoutGeometry.SnapMove(keys, [1], -446, 0, Tolerance);

        Assert.Equal(-450, result.Dx);
        Assert.NotNull(result.GuideX);
        Assert.Equal(100.0, result.GuideX.Value);
    }

    [Fact]
    public void SnapMove_BothAxesAtOnce_GuidesEachOneOnItsOwn()
    {
        var keys = new List<UserLayoutKey> { K(0, 0), K(300, 300) };

        var both = UserLayoutGeometry.SnapMove(keys, [1], -292, -195, Tolerance);
        Assert.Equal(-300, both.Dx);
        Assert.Equal(-200, both.Dy);
        Assert.NotNull(both.GuideX);
        Assert.NotNull(both.GuideY);
        Assert.Equal(0.0, both.GuideX.Value);
        Assert.Equal(100.0, both.GuideY.Value);

        // the axes are decided separately: X locks to A's right edge while Y,
        // 45 short, stays on the grid with no guide
        var mixed = UserLayoutGeometry.SnapMove(keys, [1], -197, -155, Tolerance);
        Assert.Equal(-200, mixed.Dx);
        Assert.NotNull(mixed.GuideX);
        Assert.Equal(100.0, mixed.GuideX.Value);
        Assert.Equal(-150, mixed.Dy);
        Assert.Null(mixed.GuideY);
    }

    [Fact]
    public void SnapMove_RotatedTarget_SnapsToItsRotatedBounds()
    {
        // R tilts about its own centre, so its box overhangs to −20.71 … 120.71.
        var keys = new List<UserLayoutKey>
        {
            K(0, 0), K(0, 200, 4500, 50, 250), K(300, 0),
        };

        // B's left would stop at 124: A's edge is 24 away, R's tilted edge 3.3.
        var result = UserLayoutGeometry.SnapMove(keys, [2], -176, 0, Tolerance);

        Assert.Equal(-179, result.Dx);
        Assert.NotNull(result.GuideX);
        Assert.Equal(120.71, result.GuideX.Value, 2);
    }

    [Fact]
    public void SnapMove_SmallestOffsetWins_EvenFromALaterKey()
    {
        // A (index 0) is 6 away flush-left, C (index 2) one row down is 2 away
        // left-on-left: the closer candidate takes it, whatever its key's index.
        var keys = new List<UserLayoutKey> { K(0, 0), K(300, 0), K(108, 200) };

        var result = UserLayoutGeometry.SnapMove(keys, [1], -194, 0, Tolerance);

        Assert.Equal(-192, result.Dx);
        Assert.NotNull(result.GuideX);
        Assert.Equal(108.0, result.GuideX.Value);
    }

    [Fact]
    public void SnapMove_TwoKeySelection_FlushesTheSelectionsOuterEdge()
    {
        var keys = new List<UserLayoutKey> { K(0, 0), K(300, 0), K(400, 0) };

        var result = UserLayoutGeometry.SnapMove(keys, [1, 2], -197, 0, Tolerance);

        // the whole selection is one box: its left edge goes flush to A's right
        Assert.Equal(-200, result.Dx);
        Assert.NotNull(result.GuideX);
        Assert.Equal(100.0, result.GuideX.Value);
        var moved = UserLayoutGeometry.Move(keys, [1, 2], result.Dx, result.Dy);
        Assert.Equal((100, 200), (moved[1].X, moved[2].X));
    }

    [Fact]
    public void SnapMove_WithoutStillNeighbours_UsesTheGridAndNoGuide()
    {
        var keys = new List<UserLayoutKey> { K(0, 0), K(300, 0) };

        // every key moves, so there is nothing to align to
        var all = UserLayoutGeometry.SnapMove(keys, [0, 1], -193, -195, Tolerance);
        Assert.Equal(-200, all.Dx);
        Assert.Equal(-200, all.Dy);
        Assert.Null(all.GuideX);
        Assert.Null(all.GuideY);

        // nothing selected, and indices outside the list, are the same case (the
        // delta alone can't tell grid from snap here, the null guide can)
        var none = UserLayoutGeometry.SnapMove(keys, [], -193, -195, Tolerance);
        Assert.Equal(-200, none.Dx);
        Assert.Null(none.GuideX);
        var bogus = UserLayoutGeometry.SnapMove(keys, [7], -193, -195, Tolerance);
        Assert.Equal(-200, bogus.Dx);
        Assert.Null(bogus.GuideX);

        // proof the nulls above mean something: with A still, that drag snaps
        var one = UserLayoutGeometry.SnapMove(keys, [1], -193, 0, Tolerance);
        Assert.NotNull(one.GuideX);
        Assert.Equal(100.0, one.GuideX.Value);
        Assert.NotNull(one.GuideY);
        Assert.Equal(0.0, one.GuideY.Value);
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

    // ── mirroring one half onto the other ───────────────────────────────

    [Fact]
    public void Mirror_AnAlreadyMirroredHalf_LeavesEveryKeyInPlace()
    {
        var keys = new List<UserLayoutKey>
        {
            K(0, 0, Hand.Left), K(100, 0, Hand.Left),
            K(400, 0, Hand.Right), K(500, 0, Hand.Right),
        };

        var mirrored = UserLayoutGeometry.Mirror(keys, Hand.Left, Split);

        Assert.NotNull(mirrored);
        Assert.Equal(keys, mirrored);
    }

    [Fact]
    public void Mirror_MovedSourceKey_MovesTheNearestTargetKey_KeepsIndices()
    {
        var keys = new List<UserLayoutKey>
        {
            K(0, 50, Hand.Left), K(100, 0, Hand.Left),
            K(400, 0, Hand.Right), K(500, 0, Hand.Right),
        };

        var mirrored = UserLayoutGeometry.Mirror(keys, Hand.Left, Split);

        Assert.NotNull(mirrored);
        // source 0's slot (550, 100) is nearest index 3 (centre 550, 50), so the
        // outer right key follows the dropped thumb; index 2 keeps its place.
        Assert.Equal((400, 0), (mirrored[2].X, mirrored[2].Y));
        Assert.Equal((500, 50), (mirrored[3].X, mirrored[3].Y));
        Assert.Equal(Hand.Right, mirrored[3].Hand);
        // the source half is untouched
        Assert.Equal((0, 50), (mirrored[0].X, mirrored[0].Y));
    }

    [Fact]
    public void Mirror_TargetsInSwappedOrder_KeepTheirOwnPositions()
    {
        // index 2 is the outer key, index 3 the inner one.
        var keys = new List<UserLayoutKey>
        {
            K(0, 0, Hand.Left), K(100, 0, Hand.Left),
            K(500, 0, Hand.Right), K(400, 0, Hand.Right),
        };

        var mirrored = UserLayoutGeometry.Mirror(keys, Hand.Left, Split);

        Assert.NotNull(mirrored);
        // nearest matching, so binding order decides nothing here: each key
        // stays on the side it was already on.
        Assert.Equal(500, mirrored[2].X);
        Assert.Equal(400, mirrored[3].X);
    }

    [Fact]
    public void Mirror_RotatedSourceKey_NegatesTheAngle_AndPivotsAtTheNewCentre()
    {
        var keys = new List<UserLayoutKey>
        {
            K(0, 0, Hand.Left), K(100, 0, Hand.Left, 1000),
            K(400, 0, Hand.Right), K(500, 0, Hand.Right),
        };

        var mirrored = UserLayoutGeometry.Mirror(keys, Hand.Left, Split);

        Assert.NotNull(mirrored);
        // source 1 spins about its own centre, so its slot stays (150, 50) →
        // (450, 50): the right key nearest X 450 takes the mirrored angle.
        Assert.Equal((-1000, 450, 50, 400, 0),
            (mirrored[2].R, mirrored[2].Rx, mirrored[2].Ry, mirrored[2].X, mirrored[2].Y));
        var (cx, cy) = UserLayoutGeometry.RotatedCenter(mirrored[2]);
        Assert.Equal(450, cx, 6);
        Assert.Equal(50, cy, 6);
        Assert.Equal(K(500, 0, Hand.Right), mirrored[3]);
    }

    [Fact]
    public void Mirror_RightToLeft_MovesTheLeftHalf_AndLeavesTheSourceAlone()
    {
        var keys = new List<UserLayoutKey>
        {
            K(0, 0, Hand.Left), K(100, 0, Hand.Left),
            K(400, 25, Hand.Right), K(500, 0, Hand.Right),
        };

        var mirrored = UserLayoutGeometry.Mirror(keys, Hand.Right, Split);

        Assert.NotNull(mirrored);
        // right source 2's centre (450, 75) → slot (150, 75) → the left key
        // nearest X 150 follows it down.
        Assert.Equal((100, 25), (mirrored[1].X, mirrored[1].Y));
        Assert.Equal((0, 0), (mirrored[0].X, mirrored[0].Y));
        Assert.Equal((400, 25), (mirrored[2].X, mirrored[2].Y));
        Assert.Equal(Hand.Right, mirrored[2].Hand);
    }

    [Fact]
    public void Mirror_TakesTheSourceSize()
    {
        var keys = new List<UserLayoutKey>
        {
            new(0, 0, 150, 100, 0, 0, 0, Hand.Left),
            new(400, 0, 100, 100, 0, 0, 0, Hand.Right),
        };

        var mirrored = UserLayoutGeometry.Mirror(keys, Hand.Left, Split);

        Assert.NotNull(mirrored);
        // centre 75 → 525, and the target widens to 150 → X 450.
        Assert.Equal((450, 150, 100), (mirrored[1].X, mirrored[1].W, mirrored[1].H));
        Assert.Equal(525, UserLayoutGeometry.RotatedCenter(mirrored[1]).X, 6);
    }

    [Fact]
    public void Mirror_UnequalHalves_ReturnNull()
    {
        var keys = new List<UserLayoutKey>
        {
            K(0, 0, Hand.Left), K(100, 0, Hand.Left),
            K(400, 0, Hand.Right), K(500, 0, Hand.Right), K(600, 0, Hand.Right),
        };

        Assert.Null(UserLayoutGeometry.Mirror(keys, Hand.Left, Split));
        Assert.Null(UserLayoutGeometry.Mirror(keys, Hand.Right, Split));
    }

    [Fact]
    public void Mirror_SingleHandedOrEmptyLayout_ReturnsNull()
    {
        var allLeft = new List<UserLayoutKey> { K(0, 0, Hand.Left), K(100, 0, Hand.Left) };

        // nothing to move onto / nothing to mirror
        Assert.Null(UserLayoutGeometry.Mirror(allLeft, Hand.Left, Split));
        Assert.Null(UserLayoutGeometry.Mirror(allLeft, Hand.Right, Split));
        Assert.Null(UserLayoutGeometry.Mirror(new List<UserLayoutKey>(), Hand.Left, Split));
    }

    [Theory]
    [InlineData("a_dux")]
    [InlineData("corne")]
    [InlineData("glove80")]
    [InlineData("go60")]
    [InlineData("kyria")]
    [InlineData("lily58")]
    [InlineData("sofle")]
    public void HasSplitGap_EveryBundledSplitBoard_IsSplit(string board)
    {
        var dtsi = DtsiEmbeddedResource.Load($"LViz.Core.Resources.{board}.dtsi");
        var keys = UserLayoutDraft.KeysFrom(
            new ImportedLayout(board, LayoutSource.PhysicalLayout, dtsi.Keys, []));

        Assert.True(UserLayoutGeometry.HasSplitGap(keys));
    }

    [Fact]
    public void HasSplitGap_GeneratedGrids_FollowTheSplitAnswer()
    {
        static IReadOnlyList<UserLayoutKey> Grid(bool split) =>
            UserLayoutDraft.KeysFrom(GridLayoutGenerator.Generate(new(split, 18, 6, 3)));

        Assert.True(UserLayoutGeometry.HasSplitGap(Grid(split: true)));
        Assert.False(UserLayoutGeometry.HasSplitGap(Grid(split: false)));
    }

    [Fact]
    public void HasSplitGap_TouchingOrSingleHandedLayouts_AreNotSplit()
    {
        // Centres 1.25u apart is the minimum; touching halves are one board.
        Assert.True(UserLayoutGeometry.HasSplitGap([K(0, 0, Hand.Left), K(125, 0, Hand.Right)]));
        Assert.False(UserLayoutGeometry.HasSplitGap([K(0, 0, Hand.Left), K(124, 0, Hand.Right)]));
        Assert.False(UserLayoutGeometry.HasSplitGap([K(0, 0, Hand.Left), K(100, 0, Hand.Right)]));
        Assert.False(UserLayoutGeometry.HasSplitGap([K(0, 0, Hand.Left), K(200, 0, Hand.Left)]));
    }

    [Fact]
    public void Mirror_WithoutAnAxis_FlipsAboutTheMiddleBetweenTheHalves()
    {
        // A 4u gap: the halves' average centres are 100 and 900, so the axis is
        // 500 — no split line involved, and the right half stays at 800/900.
        var keys = new List<UserLayoutKey>
        {
            K(0, 50, Hand.Left), K(100, 0, Hand.Left),
            K(800, 0, Hand.Right), K(900, 0, Hand.Right),
        };

        var mirrored = UserLayoutGeometry.Mirror(keys, Hand.Left);

        Assert.NotNull(mirrored);
        Assert.Equal((900, 50), (mirrored[3].X, mirrored[3].Y));
        Assert.Equal((800, 0), (mirrored[2].X, mirrored[2].Y));
        Assert.Equal(keys[0], mirrored[0]);
        Assert.Equal(keys[1], mirrored[1]);
    }

    [Fact]
    public void Mirror_WithoutAnAxis_UnequalOrEmptyHalves_ReturnNull()
    {
        var uneven = new List<UserLayoutKey>
        {
            K(0, 0, Hand.Left), K(100, 0, Hand.Left), K(400, 0, Hand.Right),
        };
        var allLeft = new List<UserLayoutKey> { K(0, 0, Hand.Left) };

        Assert.Null(UserLayoutGeometry.Mirror(uneven, Hand.Left));
        Assert.Null(UserLayoutGeometry.Mirror(allLeft, Hand.Left));
        Assert.Null(UserLayoutGeometry.Mirror(allLeft, Hand.Right));
    }

    [Fact]
    public void Mirror_ReturnsANewList_AndLeavesTheInputAlone()
    {
        var keys = new List<UserLayoutKey>
        {
            K(0, 50, Hand.Left), K(100, 0, Hand.Left),
            K(400, 0, Hand.Right), K(500, 10, Hand.Right),
        };
        var before = keys.ToList();

        var mirrored = UserLayoutGeometry.Mirror(keys, Hand.Left, Split);

        Assert.NotNull(mirrored);
        Assert.NotSame(keys, mirrored);
        Assert.Equal(before, keys);
        // not a no-op, so "unchanged" above means something
        Assert.Equal(50, mirrored[3].Y);
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

    /// <summary>A 100×100 key for <paramref name="hand"/>; when it is rotated,
    /// its pivot is its own centre — the shape <see cref="UserLayoutGeometry.Rotate"/>
    /// leaves behind.</summary>
    private static UserLayoutKey K(int x, int y, Hand hand, int r = 0) =>
        new(x, y, 100, 100, r, r != 0 ? x + 50 : 0, r != 0 ? y + 50 : 0, hand);
}
