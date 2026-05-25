using LViz.App.ViewModels;
using LViz.Core.Models;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Per-combo label overrides — keyed by (profileId, sorted-key-positions
/// string). Mirrors the per-key override pattern but combos aren't
/// layer-scoped, so there's no layer index.
/// </summary>
public class ComboLabelOverrideTests
{
    [Fact]
    public void KeyPositionsToKey_SortsAscendingAndCommaJoins()
    {
        Assert.Equal("5,8,12", ComboLabelOverrides.KeyPositionsToKey(new[] { 12, 5, 8 }));
        Assert.Equal("0,1", ComboLabelOverrides.KeyPositionsToKey(new[] { 1, 0 }));
        Assert.Equal("7", ComboLabelOverrides.KeyPositionsToKey(new[] { 7 }));
    }

    [Fact]
    public void Service_SetThenGet_RoundTrips()
    {
        var svc = new ComboLabelOverrideService();
        svc.Set("Corne", "12,13", new ComboLabelOverride { MainLabel = "[]" });

        var got = svc.Get("Corne", "12,13");
        Assert.NotNull(got);
        Assert.Equal("[]", got!.MainLabel);
    }

    [Fact]
    public void Service_SetEmpty_RemovesEntry()
    {
        var svc = new ComboLabelOverrideService();
        svc.Set("Corne", "12,13", new ComboLabelOverride { MainLabel = "[]" });
        Assert.NotNull(svc.Get("Corne", "12,13"));

        svc.Set("Corne", "12,13", new ComboLabelOverride()); // IsEmpty
        Assert.Null(svc.Get("Corne", "12,13"));
    }

    [Fact]
    public void Service_SetNull_RemovesEntry_AndPrunesEmptyProfileBucket()
    {
        var svc = new ComboLabelOverrideService();
        svc.Set("Corne", "12,13", new ComboLabelOverride { MainLabel = "[]" });
        svc.Set("Corne", "12,13", null);

        Assert.Empty(svc.Snapshot());
    }

    [Fact]
    public void Service_Snapshot_DeepCopies()
    {
        var svc = new ComboLabelOverrideService();
        svc.Set("Corne", "12,13", new ComboLabelOverride { MainLabel = "[]" });
        var snap = svc.Snapshot();

        // Mutating the snapshot must not affect the service.
        snap["Corne"]["12,13"] = new ComboLabelOverride { MainLabel = "tampered" };
        Assert.Equal("[]", svc.Get("Corne", "12,13")!.MainLabel);
    }

    [Fact]
    public void ComboViewModel_UsesOverrideWhenSet_FallsBackToComputedWhenNot()
    {
        var combo = new ZmkCombo(
            "BracketCOMBO", "",
            KeyPositions: new[] { 12, 13 },
            Layers: new[] { -1 },
            Binding: new KeyBinding("&kp", new[] { "A" }));

        var noOverride = new ComboViewModel(1, combo, _ => "");
        Assert.Equal("A", noOverride.Label);
        Assert.Equal("A", noOverride.DefaultLabel);

        var withOverride = new ComboViewModel(1, combo, _ => "",
            overrideEntry: new ComboLabelOverride { MainLabel = "[]" });
        Assert.Equal("[]", withOverride.Label);
        Assert.Equal("A", withOverride.DefaultLabel); // default still surfaces for the editor hint
    }

    [Fact]
    public void ComboViewModel_KeyPositionsKey_IsCanonicalSortedJoin()
    {
        var combo = new ZmkCombo("c", "", new[] { 13, 5, 12 }, new[] { -1 }, KeyBinding.None);
        var vm = new ComboViewModel(1, combo, _ => "");
        Assert.Equal("5,12,13", vm.KeyPositionsKey);
    }
}
