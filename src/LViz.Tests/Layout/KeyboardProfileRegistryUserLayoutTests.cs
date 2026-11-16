using LViz.Core.Layout;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary>
/// User layouts inside a <see cref="KeyboardProfileRegistry"/> instance:
/// picker order (bundled first, user layouts appended), id resolution through
/// Resolve/TryResolve, the key-count auto-switch preferring user layouts, and
/// SetUserProfiles replacing rather than appending. Every test builds its own
/// registry — nothing is shared between tests.
/// </summary>
public sealed class KeyboardProfileRegistryUserLayoutTests
{
    [Fact]
    public void All_ListsUserProfilesAfterBundled_AndTryResolveFindsThem()
    {
        var user = new FakeKeyboardProfile("user:x");
        var registry = new KeyboardProfileRegistry([user]);

        var all = registry.All;
        Assert.Equal(KeyboardProfileRegistry.Bundled.Count + 1, all.Count);
        Assert.Equal(
            KeyboardProfileRegistry.Bundled.Select(p => p.Id),
            all.Take(KeyboardProfileRegistry.Bundled.Count).Select(p => p.Id));
        Assert.Same(user, all[^1]);

        Assert.True(registry.TryResolve("user:x", out var resolved));
        Assert.Same(user, resolved);
    }

    [Fact]
    public void FindByKeyCount_PrefersUserLayout_OverBundledCorne()
    {
        // Corne is bundled and also has 42 keys — the user layout must win.
        var user42 = new FakeKeyboardProfile("user:fortytwo", keyCount: 42);

        Assert.Same(user42, new KeyboardProfileRegistry([user42]).FindByKeyCount(42));

        // With no user layouts, the bundled Corne is the first 42-key
        // profile, exactly as before this feature.
        Assert.IsType<CorneProfile>(new KeyboardProfileRegistry().FindByKeyCount(42));
    }

    [Fact]
    public void FindByKeyCount_NoProfileHasCount_ReturnsNull()
    {
        var registry = new KeyboardProfileRegistry([new FakeKeyboardProfile("user:x", keyCount: 11)]);

        Assert.Null(registry.FindByKeyCount(12345));
    }

    [Fact]
    public void FindByKeyCount_SameKeyCount_PrefersBoardWhoseHidNameIsAttached()
    {
        // Sofle comes before GO60 in the registry; a plugged-in Go60 wins the tie.
        var registry = new KeyboardProfileRegistry();
        Assert.IsType<Go60Profile>(registry.FindByKeyCount(60, ["Go60 Left"]));
        Assert.IsType<SofleProfile>(registry.FindByKeyCount(60, ["Sofle"]));
    }

    [Fact]
    public void FindByKeyCount_NoAttachedNameMatches_KeepsRegistryOrder()
    {
        var registry = new KeyboardProfileRegistry();
        Assert.IsType<SofleProfile>(registry.FindByKeyCount(60));
        Assert.IsType<SofleProfile>(registry.FindByKeyCount(60, []));
        // A Glove80 is plugged in, but it has no 60-key profile to win with.
        Assert.IsType<SofleProfile>(registry.FindByKeyCount(60, ["Glove80 Left"]));
    }

    [Fact]
    public void FindByKeyCount_AttachedBundledBoard_WinsOverUnattachedUserLayout()
    {
        // FakeKeyboardProfile never matches a HID name.
        var user60 = new FakeKeyboardProfile("user:sixty", keyCount: 60);
        var registry = new KeyboardProfileRegistry([user60]);

        Assert.IsType<Go60Profile>(registry.FindByKeyCount(60, ["Go60"]));
        Assert.IsType<SofleProfile>(registry.FindByKeyCount(60, ["Sofle"]));
        // Nothing attached matches a 60-key board: the user layout wins, as before.
        Assert.Same(user60, registry.FindByKeyCount(60, ["Ploopy Bean"]));
    }

    [Fact]
    public void ResolveOrDefault_KnownId_ReturnsThatProfile()
    {
        Assert.IsType<Go60Profile>(new KeyboardProfileRegistry().ResolveOrDefault("GO60"));
    }

    [Fact]
    public void ResolveOrDefault_UnknownNullOrEmpty_ReturnsCorne()
    {
        var registry = new KeyboardProfileRegistry();
        Assert.IsType<CorneProfile>(registry.ResolveOrDefault("nope"));
        Assert.IsType<CorneProfile>(registry.ResolveOrDefault(null));
        Assert.IsType<CorneProfile>(registry.ResolveOrDefault(""));
    }

    [Fact]
    public void SetUserProfiles_ReplacesInsteadOfAppending()
    {
        var first = new FakeKeyboardProfile("user:first");
        var second = new FakeKeyboardProfile("user:second");
        var registry = new KeyboardProfileRegistry();

        registry.SetUserProfiles([first]);
        registry.SetUserProfiles([second]);

        Assert.False(registry.TryResolve("user:first", out _));
        Assert.True(registry.TryResolve("user:second", out var resolved));
        Assert.Same(second, resolved);
        Assert.DoesNotContain("user:first", registry.All.Select(p => p.Id));
        Assert.DoesNotContain(first, registry.All);
    }

    [Fact]
    public void TwoRegistries_DoNotShareUserProfiles()
    {
        var shared = new FakeKeyboardProfile("user:shared");
        var first = new KeyboardProfileRegistry();
        var second = new KeyboardProfileRegistry();

        first.SetUserProfiles([shared]);

        Assert.True(first.TryResolve("user:shared", out _));
        Assert.False(second.TryResolve("user:shared", out _));
        Assert.Equal(KeyboardProfileRegistry.Bundled.Count, second.All.Count);
    }

    [Fact]
    public void Bundled_KeepsTheShippedOrder()
    {
        // FindByKeyCount takes the first count match, so this order is
        // load-bearing — Corne (the startup default) stays first.
        Assert.Equal(7, KeyboardProfileRegistry.Bundled.Count);
        Assert.IsType<CorneProfile>(KeyboardProfileRegistry.Bundled[0]);
        Assert.Equal(
        [
            typeof(CorneProfile), typeof(ADuxProfile), typeof(KyriaProfile), typeof(Lily58Profile),
            typeof(SofleProfile), typeof(Go60Profile), typeof(Glove80Profile),
        ],
            KeyboardProfileRegistry.Bundled.Select(p => p.GetType()));
    }
}
