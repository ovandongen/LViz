using LViz.Core.ZmkConfig;
using Xunit;

namespace LViz.Tests.ZmkConfig;

/// <summary>
/// <see cref="TextLineDiff"/>: the LCS line diff behind the setup wizard's
/// review step — line-ending-insensitive comparison, removals before additions
/// within a hunk, and the context-trimmed view with null gaps.
/// </summary>
public class TextLineDiffTests
{
    private static TextDiffLine Same(string text) => new(TextDiffKind.Same, text);
    private static TextDiffLine Added(string text) => new(TextDiffKind.Added, text);
    private static TextDiffLine Removed(string text) => new(TextDiffKind.Removed, text);

    [Fact]
    public void Compute_ReplacedLine_RemovedComesBeforeAdded()
    {
        var diff = TextLineDiff.Compute("a\nb\nc\n", "a\nx\nc\n");

        Assert.Equal([Same("a"), Removed("b"), Added("x"), Same("c")], diff);
    }

    [Fact]
    public void Compute_NullOldText_EveryLineAdded()
    {
        var diff = TextLineDiff.Compute(null, "a\nb");

        Assert.Equal([Added("a"), Added("b")], diff);
    }

    [Fact]
    public void Compute_CrlfOldAgainstLfNew_EqualLinesAreSame()
    {
        var diff = TextLineDiff.Compute("a\r\nb\r\nc\r\n", "a\nb\nc");

        Assert.Equal([Same("a"), Same("b"), Same("c")], diff);
    }

    [Fact]
    public void Compute_BeyondCellBudget_FallsBackToEverythingChanged()
    {
        // 2001 × 2000 lines is just over the 4,000,000-cell budget, so even
        // identical texts come back as all removed, then all added.
        var oldText = string.Join('\n', Enumerable.Range(1, 2001).Select(i => i.ToString())) + "\n";
        var newText = string.Join('\n', Enumerable.Range(1, 2000).Select(i => i.ToString())) + "\n";

        var diff = TextLineDiff.Compute(oldText, newText);

        Assert.Equal(4001, diff.Count);
        Assert.All(diff.Take(2001), line => Assert.Equal(TextDiffKind.Removed, line.Kind));
        Assert.All(diff.Skip(2001), line => Assert.Equal(TextDiffKind.Added, line.Kind));
    }

    [Fact]
    public void WithContext_KeepsNeighboursAndMarksGapsWithNull()
    {
        var oldText = string.Join('\n', Enumerable.Range(1, 10)) + "\n";
        var newText = string.Join('\n',
            Enumerable.Range(1, 10).Select(i => i == 6 ? "six" : i.ToString())) + "\n";
        var diff = TextLineDiff.Compute(oldText, newText);

        var kept = TextLineDiff.WithContext(diff, 1);

        Assert.Equal<TextDiffLine?>(
            [null, Same("5"), Removed("6"), Added("six"), Same("7"), null],
            kept);
    }

    [Fact]
    public void WithContext_NoChanges_YieldsEmptyList()
    {
        var diff = TextLineDiff.Compute("a\nb\n", "a\nb\n");

        Assert.Empty(TextLineDiff.WithContext(diff, 2));
    }
}
