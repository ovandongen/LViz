using LViz.App.ViewModels;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

public class KeyLabelOverrideServiceTests
{
    [Fact]
    public void SetAndGet_RoundTripsValue()
    {
        var svc = new KeyLabelOverrideService();
        var ov = new KeyLabelOverride { MainLabel = "nav", Bold = true };

        svc.Set("corne", 1, 17, ov);

        Assert.Equal(ov, svc.Get("corne", 1, 17));
    }

    [Fact]
    public void Get_ReturnsNull_WhenSlotEmpty()
    {
        var svc = new KeyLabelOverrideService();
        Assert.Null(svc.Get("corne", 0, 0));
    }

    [Fact]
    public void SetNull_RemovesEntryAndPrunesEmptyMaps()
    {
        var svc = new KeyLabelOverrideService();
        svc.Set("corne", 2, 5, new KeyLabelOverride { MainLabel = "x" });

        svc.Set("corne", 2, 5, null);

        Assert.Null(svc.Get("corne", 2, 5));
        // Snapshot proves the profile/layer entries collapsed away rather than
        // lingering as empty inner dicts.
        Assert.Empty(svc.Snapshot());
    }

    [Fact]
    public void SetIsEmpty_TreatedAsRemoval()
    {
        var svc = new KeyLabelOverrideService();
        svc.Set("corne", 0, 1, new KeyLabelOverride { MainLabel = "abc" });

        svc.Set("corne", 0, 1, new KeyLabelOverride()); // all defaults → IsEmpty

        Assert.Null(svc.Get("corne", 0, 1));
        Assert.Empty(svc.Snapshot());
    }

    [Fact]
    public void Snapshot_DeepCopies()
    {
        var svc = new KeyLabelOverrideService();
        svc.Set("a", 0, 1, new KeyLabelOverride { MainLabel = "A" });
        svc.Set("a", 0, 2, new KeyLabelOverride { MainLabel = "B" });

        var snap = svc.Snapshot();
        snap["a"][0].Remove(1);
        snap.Remove("a");

        // Service is untouched — caller's mutations don't leak in.
        Assert.NotNull(svc.Get("a", 0, 1));
        Assert.NotNull(svc.Get("a", 0, 2));
    }

    [Fact]
    public void SetOverrides_DeepCopiesIncomingMap()
    {
        var svc = new KeyLabelOverrideService();
        var src = new Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>>
        {
            ["x"] = new() { [0] = new() { [3] = new KeyLabelOverride { MainLabel = "Q" } } },
        };

        svc.SetOverrides(src);
        src["x"][0].Remove(3);
        src.Remove("x");

        Assert.Equal("Q", svc.Get("x", 0, 3)?.MainLabel);
    }

    [Fact]
    public void SetOverrides_DropsEmptyInnerMaps()
    {
        var svc = new KeyLabelOverrideService();
        var src = new Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>>
        {
            ["x"] = new(), // no layers — should not appear in the service state
        };

        svc.SetOverrides(src);

        Assert.Empty(svc.Snapshot());
    }

    [Fact]
    public void MultipleSlots_RemainIndependent()
    {
        var svc = new KeyLabelOverrideService();
        svc.Set("corne", 0, 1, new KeyLabelOverride { MainLabel = "base" });
        svc.Set("corne", 1, 1, new KeyLabelOverride { MainLabel = "lower" });
        svc.Set("other", 0, 1, new KeyLabelOverride { MainLabel = "other" });

        Assert.Equal("base", svc.Get("corne", 0, 1)?.MainLabel);
        Assert.Equal("lower", svc.Get("corne", 1, 1)?.MainLabel);
        Assert.Equal("other", svc.Get("other", 0, 1)?.MainLabel);
    }
}
