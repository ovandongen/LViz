using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Pure add/remove semantics of <see cref="HiddenLayers"/>: the never-hide-0
/// rule, ascending duplicate-free lists, empty-profile pruning, and the
/// copy-on-write guarantee (input dictionary and its lists never mutated).
/// </summary>
public class HiddenLayersTests
{
    [Fact]
    public void IsHidden_UnknownProfile_ReturnsFalse()
    {
        var source = new Dictionary<string, List<int>> { ["Corne"] = new() { 2 } };

        Assert.False(HiddenLayers.IsHidden(source, "Glove80", 2));
    }

    [Fact]
    public void IsHidden_ListedIndex_ReturnsTrue()
    {
        var source = new Dictionary<string, List<int>> { ["Corne"] = new() { 2 } };

        Assert.True(HiddenLayers.IsHidden(source, "Corne", 2));
        Assert.False(HiddenLayers.IsHidden(source, "Corne", 1));
    }

    [Fact]
    public void IsHidden_LayerZero_AlwaysFalse()
    {
        // Even a corrupt list that contains 0 must not hide the base layer.
        var source = new Dictionary<string, List<int>> { ["Corne"] = new() { 0 } };

        Assert.False(HiddenLayers.IsHidden(source, "Corne", 0));
    }

    [Fact]
    public void With_Hidden_CreatesProfileList()
    {
        var result = HiddenLayers.With(new Dictionary<string, List<int>>(), "Corne", 3, hidden: true);

        Assert.Equal(new[] { 3 }, result["Corne"]);
    }

    [Fact]
    public void With_AddTwice_KeepsSingleEntry()
    {
        var once = HiddenLayers.With(new Dictionary<string, List<int>>(), "Corne", 3, hidden: true);

        var twice = HiddenLayers.With(once, "Corne", 3, hidden: true);

        Assert.Equal(new[] { 3 }, twice["Corne"]);
    }

    [Fact]
    public void With_UnhideLastIndex_DropsEmptyProfile()
    {
        var source = new Dictionary<string, List<int>> { ["Corne"] = new() { 3, 7 } };

        var result = HiddenLayers.With(source, "Corne", 3, hidden: false);
        result = HiddenLayers.With(result, "Corne", 7, hidden: false);

        Assert.False(result.ContainsKey("Corne"));
    }

    [Fact]
    public void With_RemoveAbsentIndex_ReturnsEqualContent()
    {
        var source = new Dictionary<string, List<int>> { ["Corne"] = new() { 3 } };

        var result = HiddenLayers.With(source, "Corne", 9, hidden: false);

        Assert.Equal(new[] { 3 }, result["Corne"]);
    }

    [Fact]
    public void With_LayerZero_HiddenRequestIsIgnored()
    {
        var result = HiddenLayers.With(new Dictionary<string, List<int>>(), "Corne", 0, hidden: true);

        Assert.False(result.ContainsKey("Corne"));
    }

    [Fact]
    public void With_NegativeIndex_HiddenRequestIsIgnored()
    {
        var result = HiddenLayers.With(new Dictionary<string, List<int>>(), "Corne", -2, hidden: true);

        Assert.False(result.ContainsKey("Corne"));
    }

    [Fact]
    public void With_AddOutOfOrder_ResultSortedAscending()
    {
        var once = HiddenLayers.With(new Dictionary<string, List<int>>(), "Corne", 5, hidden: true);

        var result = HiddenLayers.With(once, "Corne", 2, hidden: true);

        Assert.Equal(new[] { 2, 5 }, result["Corne"]);
    }

    [Fact]
    public void With_OtherProfileList_Untouched()
    {
        var source = new Dictionary<string, List<int>> { ["Glove80"] = new() { 1, 4 } };

        var result = HiddenLayers.With(source, "Corne", 2, hidden: true);

        Assert.Equal(new[] { 1, 4 }, result["Glove80"]);
    }

    [Fact]
    public void With_Add_DoesNotMutateInputDictionaryOrLists()
    {
        var inner = new List<int> { 2 };
        var source = new Dictionary<string, List<int>> { ["Corne"] = inner };

        var result = HiddenLayers.With(source, "Corne", 5, hidden: true);
        result["Corne"].Add(99); // mutating the result must not leak back

        Assert.Equal(new[] { 2 }, source["Corne"]);
        Assert.Equal(2, inner[0]);
        Assert.False(source.ContainsKey("Glove80"));
    }

    [Fact]
    public void With_Remove_DoesNotMutateInputDictionaryOrLists()
    {
        var inner = new List<int> { 2, 5 };
        var source = new Dictionary<string, List<int>> { ["Corne"] = inner };

        var result = HiddenLayers.With(source, "Corne", 2, hidden: false);

        Assert.Equal(new[] { 2, 5 }, inner);
        Assert.Equal(new[] { 5 }, result["Corne"]);
    }
}
