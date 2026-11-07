using LViz.Core.Layout;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary>
/// User layouts inside the static <see cref="KeyboardProfileRegistry"/>:
/// picker order (bundled first, user layouts appended), id resolution through
/// Resolve/TryResolve, the key-count auto-switch preferring user layouts, and
/// SetUserProfiles replacing rather than appending.
/// <para>
/// The suite runs sequentially (AssemblyInfo.cs), so mutating the static
/// registry is safe — every test restores the empty user list in finally.
/// </para>
/// </summary>
public sealed class KeyboardProfileRegistryUserLayoutTests
{
    [Fact]
    public void All_ListsUserProfilesAfterBundled_AndTryResolveFindsThem()
    {
        var bundled = KeyboardProfileRegistry.All;
        var user = new FakeKeyboardProfile("user:x");

        try
        {
            KeyboardProfileRegistry.SetUserProfiles([user]);

            var all = KeyboardProfileRegistry.All;
            Assert.Equal(bundled.Count + 1, all.Count);
            Assert.Equal(bundled.Select(p => p.Id), all.Take(bundled.Count).Select(p => p.Id));
            Assert.Same(user, all[^1]);

            Assert.True(KeyboardProfileRegistry.TryResolve("user:x", out var resolved));
            Assert.Same(user, resolved);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void FindByKeyCount_PrefersUserLayout_OverBundledCorne()
    {
        // Corne is bundled and also has 42 keys — the user layout must win.
        var user42 = new FakeKeyboardProfile("user:fortytwo", keyCount: 42);

        try
        {
            KeyboardProfileRegistry.SetUserProfiles([user42]);

            Assert.Same(user42, KeyboardProfileRegistry.FindByKeyCount(42));
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }

        // With the user list cleared again, the bundled Corne is the first
        // 42-key profile, exactly as before this feature.
        Assert.IsType<CorneProfile>(KeyboardProfileRegistry.FindByKeyCount(42));
    }

    [Fact]
    public void FindByKeyCount_NoProfileHasCount_ReturnsNull()
    {
        try
        {
            KeyboardProfileRegistry.SetUserProfiles([new FakeKeyboardProfile("user:x", keyCount: 11)]);

            Assert.Null(KeyboardProfileRegistry.FindByKeyCount(12345));
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void FindByKeyCount_SameKeyCount_PrefersBoardWhoseHidNameIsAttached()
    {
        // Sofle comes before GO60 in the registry; a plugged-in Go60 wins the tie.
        Assert.IsType<Go60Profile>(KeyboardProfileRegistry.FindByKeyCount(60, ["Go60 Left"]));
        Assert.IsType<SofleProfile>(KeyboardProfileRegistry.FindByKeyCount(60, ["Sofle"]));
    }

    [Fact]
    public void FindByKeyCount_NoAttachedNameMatches_KeepsRegistryOrder()
    {
        Assert.IsType<SofleProfile>(KeyboardProfileRegistry.FindByKeyCount(60));
        Assert.IsType<SofleProfile>(KeyboardProfileRegistry.FindByKeyCount(60, []));
        // A Glove80 is plugged in, but it has no 60-key profile to win with.
        Assert.IsType<SofleProfile>(KeyboardProfileRegistry.FindByKeyCount(60, ["Glove80 Left"]));
    }

    [Fact]
    public void FindByKeyCount_AttachedBundledBoard_WinsOverUnattachedUserLayout()
    {
        // FakeKeyboardProfile never matches a HID name.
        var user60 = new FakeKeyboardProfile("user:sixty", keyCount: 60);

        try
        {
            KeyboardProfileRegistry.SetUserProfiles([user60]);

            Assert.IsType<Go60Profile>(KeyboardProfileRegistry.FindByKeyCount(60, ["Go60"]));
            Assert.IsType<SofleProfile>(KeyboardProfileRegistry.FindByKeyCount(60, ["Sofle"]));
            // Nothing attached matches a 60-key board: the user layout wins, as before.
            Assert.Same(user60, KeyboardProfileRegistry.FindByKeyCount(60, ["Ploopy Bean"]));
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }

    [Fact]
    public void SetUserProfiles_ReplacesInsteadOfAppending()
    {
        var first = new FakeKeyboardProfile("user:first");
        var second = new FakeKeyboardProfile("user:second");

        try
        {
            KeyboardProfileRegistry.SetUserProfiles([first]);
            KeyboardProfileRegistry.SetUserProfiles([second]);

            Assert.False(KeyboardProfileRegistry.TryResolve("user:first", out _));
            Assert.True(KeyboardProfileRegistry.TryResolve("user:second", out var resolved));
            Assert.Same(second, resolved);
            Assert.DoesNotContain("user:first", KeyboardProfileRegistry.All.Select(p => p.Id));
            Assert.DoesNotContain(first, KeyboardProfileRegistry.All);
        }
        finally
        {
            KeyboardProfileRegistry.SetUserProfiles([]);
        }
    }
}
