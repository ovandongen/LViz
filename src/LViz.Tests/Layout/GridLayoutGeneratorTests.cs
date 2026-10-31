using LViz.Core.Layout;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Layout.Import;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary>
/// Tests for <see cref="GridLayoutGenerator"/> (part 4 of the guided
/// questions): a plain grid built from the wizard's answers. Binding order
/// is row by row with the thumb row last; split halves gap 2u; thumbs run
/// inward-to-outward per half (split) or sit centred (unibody).
/// </summary>
public class GridLayoutGeneratorTests
{
    // ── key count ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, 3, 5, 3, 36)]
    [InlineData(false, 4, 12, 2, 50)]
    [InlineData(true, 1, 1, 0, 2)]
    public void KeyCount_CountsBothHalves(bool split, int rows, int columns, int thumbs, int expected)
    {
        Assert.Equal(expected,
            GridLayoutGenerator.KeyCount(new GridLayoutOptions(split, rows, columns, thumbs)));
    }

    // ── split grid, Corne-like ──────────────────────────────────────────

    [Fact]
    public void CorneLike_FortyTwoKeysInBindingOrder()
    {
        var layout = GridLayoutGenerator.Generate(new GridLayoutOptions(true, 3, 6, 3));

        Assert.Equal(42, layout.Keys.Count);
        AssertXY(layout, 0, 0, 0);      // left half, first column
        AssertXY(layout, 5, 500, 0);    // left half, last column
        AssertXY(layout, 6, 800, 0);    // first key of the right half (2u gap)
        AssertXY(layout, 12, 0, 100);   // next row restarts at the left half
    }

    [Fact]
    public void CorneLike_ThumbRowRunsFromInnerColumnOutward()
    {
        var layout = GridLayoutGenerator.Generate(new GridLayoutOptions(true, 3, 6, 3));

        // Thumb row starts at index 36 (both main rows first), at y = 3·100.
        AssertXY(layout, 36, 300, 300);
        AssertXY(layout, 37, 400, 300);
        AssertXY(layout, 38, 500, 300); // left thumbs end at the inner column
        AssertXY(layout, 39, 800, 300);
        AssertXY(layout, 40, 900, 300);
        AssertXY(layout, 41, 1000, 300);
    }

    [Fact]
    public void CorneLike_HandsFollowTheHalves()
    {
        var layout = GridLayoutGenerator.Generate(new GridLayoutOptions(true, 3, 6, 3));

        AssertHands(layout,
            [.. Enumerable.Repeat(Hand.Left, 6), .. Enumerable.Repeat(Hand.Right, 6),
             .. Enumerable.Repeat(Hand.Left, 6), .. Enumerable.Repeat(Hand.Right, 6),
             .. Enumerable.Repeat(Hand.Left, 6), .. Enumerable.Repeat(Hand.Right, 6),
             .. Enumerable.Repeat(Hand.Left, 3), .. Enumerable.Repeat(Hand.Right, 3)]);
    }

    [Fact]
    public void MoreThumbsThanColumns_OuterLeftThumbsGoNegative()
    {
        var layout = GridLayoutGenerator.Generate(new GridLayoutOptions(true, 1, 2, 3));

        // Main row (2 + 2 keys) at indices 0–3, thumbs from index 4; saving
        // normalises the negative x away.
        Assert.Equal(-100, layout.Keys[4].X);
        Assert.Equal(0, layout.Keys[5].X);
        Assert.Equal(100, layout.Keys[6].X);
    }

    // ── unibody ─────────────────────────────────────────────────────────

    [Fact]
    public void Unibody_ThumbsCentredBelowTheRows()
    {
        var layout = GridLayoutGenerator.Generate(new GridLayoutOptions(false, 2, 4, 2));

        Assert.Equal(10, layout.Keys.Count);
        AssertXY(layout, 8, 100, 200);
        AssertXY(layout, 9, 200, 200);
    }

    [Fact]
    public void Unibody_HandsSplitAtTheBoardMidline()
    {
        var layout = GridLayoutGenerator.Generate(new GridLayoutOptions(false, 2, 4, 2));

        // Centre X < 4·50 = 200 is Left: columns 0–1 and thumb 0.
        AssertHands(layout,
            [.. Enumerable.Repeat(Hand.Left, 2), .. Enumerable.Repeat(Hand.Right, 2),
             .. Enumerable.Repeat(Hand.Left, 2), .. Enumerable.Repeat(Hand.Right, 2),
             Hand.Left, Hand.Right]);
    }

    [Fact]
    public void Unibody_OddCentring_GivesHalfSteps()
    {
        var layout = GridLayoutGenerator.Generate(new GridLayoutOptions(false, 1, 3, 2));

        AssertXY(layout, 3, 50, 100);
        AssertXY(layout, 4, 150, 100);
    }

    // ── no thumbs / invariants ──────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoThumbKeys_NoThumbRow(bool split)
    {
        var layout = GridLayoutGenerator.Generate(new GridLayoutOptions(split, 2, 3, 0));

        Assert.Equal(split ? 12 : 6, layout.Keys.Count);
        Assert.DoesNotContain(layout.Keys, k => k.Y == 200); // nothing at y = Rows·100
    }

    [Theory]
    [InlineData(true, 3, 6, 3)]
    [InlineData(false, 4, 12, 2)]
    [InlineData(true, 1, 2, 3)]
    [InlineData(false, 1, 3, 0)]
    public void EveryKey_IsAnUprightOneUKey(bool split, int rows, int columns, int thumbs)
    {
        var layout = GridLayoutGenerator.Generate(new GridLayoutOptions(split, rows, columns, thumbs));

        Assert.All(layout.Keys, key =>
        {
            Assert.Equal(100, key.Width);
            Assert.Equal(100, key.Height);
            Assert.Equal(0, key.Rotation);
            Assert.Equal(0, key.Rx);
            Assert.Equal(0, key.Ry);
        });
        Assert.Equal("grid", layout.Name);
        Assert.Equal(LayoutSource.Generated, layout.Source);
        Assert.Empty(layout.Warnings);
        Assert.NotNull(layout.Hands);
        Assert.Equal(layout.Keys.Count, layout.Hands.Count);
    }

    // ── validation ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, 0, 6, 3)]  // rows below MinRows
    [InlineData(false, 0, 6, 3)]
    [InlineData(true, 9, 6, 3)]  // rows above MaxRows
    [InlineData(false, 9, 6, 3)]
    [InlineData(true, 3, 0, 3)]  // columns below MinColumns
    [InlineData(false, 3, 0, 3)]
    [InlineData(true, 3, 13, 3)] // columns above MaxColumns
    [InlineData(false, 3, 13, 3)]
    [InlineData(true, 3, 6, -1)] // thumbs below MinThumbKeys
    [InlineData(false, 3, 6, -1)]
    [InlineData(true, 3, 6, 9)]  // thumbs above MaxThumbKeys
    [InlineData(false, 3, 6, 9)]
    public void OutOfRange_ThrowsFromBothMethods(
        bool split, int rows, int columns, int thumbs)
    {
        var options = new GridLayoutOptions(split, rows, columns, thumbs);

        Assert.Throws<ArgumentOutOfRangeException>(() => GridLayoutGenerator.KeyCount(options));
        Assert.Throws<ArgumentOutOfRangeException>(() => GridLayoutGenerator.Generate(options));
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static void AssertXY(ImportedLayout layout, int index, int x, int y) =>
        Assert.Equal((x, y), (layout.Keys[index].X, layout.Keys[index].Y));

    private static void AssertHands(ImportedLayout layout, IReadOnlyList<Hand> expected)
    {
        Assert.NotNull(layout.Hands);
        Assert.Equal(expected, layout.Hands);
    }
}
