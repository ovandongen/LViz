using LViz.App.ViewModels;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The stateless override dictionaries. <c>Get</c> reads a settings snapshot;
/// <c>With</c> answers with a new dictionary — add, replace, and remove
/// (null or <c>IsEmpty</c>) while pruning the inner dictionaries an empty
/// removal leaves behind — and never mutates the dictionary it was given.
/// </summary>
public class LabelOverridesTests
{
    private static Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> KeyDict(
        params (string Profile, int Layer, int Key, string Label)[] entries)
    {
        var dict = new Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>>();
        foreach (var (profile, layer, key, label) in entries)
        {
            if (!dict.TryGetValue(profile, out var layers)) dict[profile] = layers = new();
            if (!layers.TryGetValue(layer, out var keys)) layers[layer] = keys = new();
            keys[key] = new KeyLabelOverride { MainLabel = label };
        }
        return dict;
    }

    private static Dictionary<string, Dictionary<string, ComboLabelOverride>> ComboDict(
        params (string Profile, string Positions, string Label)[] entries)
    {
        var dict = new Dictionary<string, Dictionary<string, ComboLabelOverride>>();
        foreach (var (profile, positions, label) in entries)
        {
            if (!dict.TryGetValue(profile, out var combos)) dict[profile] = combos = new();
            combos[positions] = new ComboLabelOverride { MainLabel = label };
        }
        return dict;
    }

    // ---- KeyLabelOverrides ----

    [Fact]
    public void Keys_Get_MissingProfileLayerOrKey_ReturnsNull()
    {
        var settings = new UserSettings { KeyLabelOverrides = KeyDict(("p", 0, 7, "A")) };

        Assert.Null(KeyLabelOverrides.Get(settings, "other", 0, 7));
        Assert.Null(KeyLabelOverrides.Get(settings, "p", 1, 7));
        Assert.Null(KeyLabelOverrides.Get(settings, "p", 0, 8));
        Assert.Equal("A", KeyLabelOverrides.Get(settings, "p", 0, 7)!.MainLabel);
    }

    [Fact]
    public void Keys_With_Add()
    {
        var result = KeyLabelOverrides.With(new(), "p", 0, 7, new KeyLabelOverride { MainLabel = "A" });

        Assert.Equal("A", result["p"][0][7].MainLabel);
    }

    [Fact]
    public void Keys_With_Replace()
    {
        var current = KeyDict(("p", 0, 7, "A"));

        var result = KeyLabelOverrides.With(current, "p", 0, 7, new KeyLabelOverride { MainLabel = "B" });

        Assert.Equal("B", result["p"][0][7].MainLabel);
        Assert.Equal("A", current["p"][0][7].MainLabel);
    }

    [Fact]
    public void Keys_With_RemoveLastEntry_PrunesEmptyLayerAndProfileDictionaries()
    {
        var current = KeyDict(("p", 0, 7, "A"));

        var result = KeyLabelOverrides.With(current, "p", 0, 7, null);

        Assert.Empty(result);
    }

    [Fact]
    public void Keys_With_Remove_KeepsSiblings()
    {
        var current = KeyDict(("p", 0, 7, "A"), ("p", 0, 8, "B"), ("p", 1, 7, "C"), ("q", 0, 7, "D"));

        var result = KeyLabelOverrides.With(current, "p", 0, 7, null);

        Assert.False(result["p"][0].ContainsKey(7));
        Assert.Equal("B", result["p"][0][8].MainLabel);
        Assert.Equal("C", result["p"][1][7].MainLabel);
        Assert.Equal("D", result["q"][0][7].MainLabel);
    }

    [Fact]
    public void Keys_With_RemoveMissing_ReturnsEqualCopy()
    {
        var current = KeyDict(("p", 0, 7, "A"));

        var result = KeyLabelOverrides.With(current, "p", 3, 9, null);

        Assert.NotSame(current, result);
        Assert.Single(result);
        Assert.Equal("A", result["p"][0][7].MainLabel);
    }

    [Fact]
    public void Keys_With_EmptyValue_Removes()
    {
        var current = KeyDict(("p", 0, 7, "A"));

        var result = KeyLabelOverrides.With(current, "p", 0, 7, new KeyLabelOverride());

        Assert.Empty(result);
    }

    [Fact]
    public void Keys_With_NeverMutatesCurrent()
    {
        var current = KeyDict(("p", 0, 7, "A"), ("q", 1, 2, "D"));
        var profileRef = current["p"];
        var layerRef = current["p"][0];

        var added = KeyLabelOverrides.With(current, "p", 0, 8, new KeyLabelOverride { MainLabel = "B" });
        var removed = KeyLabelOverrides.With(added, "p", 0, 8, null);

        // The changed path is copied, not shared with the input.
        Assert.NotSame(profileRef, added["p"]);
        Assert.NotSame(layerRef, added["p"][0]);
        // The add and the remove both leave current exactly as it was.
        Assert.Equal(2, current.Count);
        Assert.Single(current["p"][0]);
        Assert.Equal("A", current["p"][0][7].MainLabel);
        Assert.Equal("D", current["q"][1][2].MainLabel);
        // And the round trip is back to current's content.
        Assert.Equal("A", removed["p"][0][7].MainLabel);
        Assert.DoesNotContain(8, removed["p"][0].Keys);
    }

    [Fact]
    public void Keys_MultipleSlots_RemainIndependent()
    {
        var dict = KeyLabelOverrides.With(new(), "corne", 0, 1, new KeyLabelOverride { MainLabel = "base" });
        dict = KeyLabelOverrides.With(dict, "corne", 1, 1, new KeyLabelOverride { MainLabel = "lower" });
        dict = KeyLabelOverrides.With(dict, "other", 0, 1, new KeyLabelOverride { MainLabel = "other" });

        var removed = KeyLabelOverrides.With(dict, "corne", 0, 1, null);

        // Dropping one slot leaves the other layer and the other profile alone.
        Assert.Equal(2, removed.Count);
        Assert.False(removed["corne"].ContainsKey(0));
        Assert.Equal("lower", removed["corne"][1][1].MainLabel);
        Assert.Equal("other", removed["other"][0][1].MainLabel);
    }

    // ---- ComboLabelOverrides ----

    [Fact]
    public void Combos_Get_MissingProfileOrPositions_ReturnsNull()
    {
        var settings = new UserSettings { ComboLabelOverrides = ComboDict(("p", "0,1", "A")) };

        Assert.Null(ComboLabelOverrides.Get(settings, "other", "0,1"));
        Assert.Null(ComboLabelOverrides.Get(settings, "p", "2,3"));
        Assert.Equal("A", ComboLabelOverrides.Get(settings, "p", "0,1")!.MainLabel);
    }

    [Fact]
    public void Combos_With_Add()
    {
        var result = ComboLabelOverrides.With(new(), "p", "0,1", new ComboLabelOverride { MainLabel = "A" });

        Assert.Equal("A", result["p"]["0,1"].MainLabel);
    }

    [Fact]
    public void Combos_With_Replace()
    {
        var current = ComboDict(("p", "0,1", "A"));

        var result = ComboLabelOverrides.With(current, "p", "0,1", new ComboLabelOverride { MainLabel = "B" });

        Assert.Equal("B", result["p"]["0,1"].MainLabel);
        Assert.Equal("A", current["p"]["0,1"].MainLabel);
    }

    [Fact]
    public void Combos_With_RemoveLastEntry_PrunesEmptyProfileDictionary()
    {
        var current = ComboDict(("p", "0,1", "A"));

        var result = ComboLabelOverrides.With(current, "p", "0,1", null);

        Assert.Empty(result);
    }

    [Fact]
    public void Combos_With_Remove_KeepsSiblings()
    {
        var current = ComboDict(("p", "0,1", "A"), ("p", "2,3", "B"), ("q", "0,1", "C"));

        var result = ComboLabelOverrides.With(current, "p", "0,1", null);

        Assert.False(result["p"].ContainsKey("0,1"));
        Assert.Equal("B", result["p"]["2,3"].MainLabel);
        Assert.Equal("C", result["q"]["0,1"].MainLabel);
    }

    [Fact]
    public void Combos_With_RemoveMissing_ReturnsEqualCopy()
    {
        var current = ComboDict(("p", "0,1", "A"));

        var result = ComboLabelOverrides.With(current, "p", "9,9", null);

        Assert.NotSame(current, result);
        Assert.Single(result);
        Assert.Equal("A", result["p"]["0,1"].MainLabel);
    }

    [Fact]
    public void Combos_With_EmptyValue_Removes()
    {
        var current = ComboDict(("p", "0,1", "A"));

        var result = ComboLabelOverrides.With(current, "p", "0,1", new ComboLabelOverride());

        Assert.Empty(result);
    }

    [Fact]
    public void Combos_With_NeverMutatesCurrent()
    {
        var current = ComboDict(("p", "0,1", "A"), ("q", "4,5", "C"));
        var profileRef = current["p"];

        var added = ComboLabelOverrides.With(current, "p", "2,3", new ComboLabelOverride { MainLabel = "B" });
        var removed = ComboLabelOverrides.With(added, "p", "2,3", null);

        // The changed path is copied, not shared with the input.
        Assert.NotSame(profileRef, added["p"]);
        // The add and the remove both leave current exactly as it was.
        Assert.Equal(2, current.Count);
        Assert.Single(current["p"]);
        Assert.Equal("A", current["p"]["0,1"].MainLabel);
        Assert.Equal("C", current["q"]["4,5"].MainLabel);
        // And the round trip is back to current's content.
        Assert.Equal("A", removed["p"]["0,1"].MainLabel);
        Assert.DoesNotContain("2,3", removed["p"].Keys);
    }
}
