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
