using LViz.Core.Keymap.Editing;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

public class LayerIdDiffTests
{
    private static IReadOnlyList<LayerStructureEdit> Net(int baseline, params int[] ids) =>
        LayerIdDiff.NetEdits(baseline, ids, (p, id) => new LayerStructureEdit.Add(p, $"L{id}", []));

    [Fact]
    public void Identity_IsEmpty()
    {
        Assert.Empty(Net(3, 0, 1, 2));
        Assert.Empty(Net(0));
    }

    [Fact]
    public void SingleMove_IsOneMove()
    {
        var edits = Net(4, 0, 2, 3, 1);
        var move = Assert.IsType<LayerStructureEdit.Move>(Assert.Single(edits));
        Assert.Equal(new LayerStructureEdit.Move(1, 3), move);
    }

    [Fact]
    public void DeleteThenAdd_DeletesFirstAddsAtFinalPosition()
    {
        var edits = Net(3, 0, 2, 3);
        Assert.Equal(2, edits.Count);
        Assert.Equal(new LayerStructureEdit.Delete(1), edits[0]);
        var add = Assert.IsType<LayerStructureEdit.Add>(edits[1]);
        Assert.Equal(2, add.AtIndex);
        Assert.Equal("L3", add.Name);
    }

    [Fact]
    public void Deletes_RunFromHighestIndexDown()
    {
        var edits = Net(4, 1, 3);
        Assert.Equal(
            [new LayerStructureEdit.Delete(2), new LayerStructureEdit.Delete(0)],
            edits.Cast<LayerStructureEdit.Delete>());
    }

    [Fact]
    public void ReversedList_MovesAllButOne()
    {
        // Longest in-order run of [2,1,0] has length 1, so two layers move.
        Assert.Equal(2, Net(3, 2, 1, 0).Count);
    }

    [Fact]
    public void LongestRun_StaysInPlace()
    {
        // [1,2,3,0]: 1,2,3 stay; only layer 0 moves, from 0 to the end.
        var move = Assert.IsType<LayerStructureEdit.Move>(Assert.Single(Net(4, 1, 2, 3, 0)));
        Assert.Equal(new LayerStructureEdit.Move(0, 3), move);
    }

    [Fact]
    public void LongestIncreasingRun_PicksALongestSubsequence()
    {
        var run = LayerIdDiff.LongestIncreasingRun([3, 0, 1, 2]);
        Assert.Equal(new HashSet<int> { 0, 1, 2 }, run);
        Assert.Empty(LayerIdDiff.LongestIncreasingRun([]));
    }

    [Fact]
    public void ThereAndBack_IsEmpty()
    {
        // Add a layer (id 3) then delete it again: ids are the baseline list.
        Assert.Empty(Net(3, 0, 1, 2));
        Assert.True(LayerIdDiff.MatchesBaseline(3, [0, 1, 2]));
        Assert.False(LayerIdDiff.MatchesBaseline(3, [0, 2, 1]));
        Assert.False(LayerIdDiff.MatchesBaseline(3, [0, 1]));
    }

    [Fact]
    public void BaselineMap_ReportsPositionsAndDeletes()
    {
        // Baseline 0..3: layer 1 deleted, 3 moved first, id 7 added.
        var map = LayerIdDiff.BaselineMap(4, [3, 0, 7, 2]);
        Assert.Equal([1, null, 3, 0], map);
        Assert.Equal([0, 1, 2], LayerIdDiff.BaselineMap(3, [0, 1, 2]).Select(x => x!.Value));
    }
}
