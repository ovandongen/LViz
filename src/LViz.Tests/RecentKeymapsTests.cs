using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Pure push/remove semantics of <see cref="RecentKeymaps"/>: move-to-front,
/// trimming at <see cref="RecentKeymaps.MaxEntries"/>, blank-path rejection,
/// and the copy-on-write guarantee (input never mutated).
/// </summary>
public class RecentKeymapsTests
{
    [Fact]
    public void Push_ToEmpty_PutsPathAtFront()
    {
        var result = RecentKeymaps.Push(Array.Empty<string>(), "/keymaps/a.keymap");

        Assert.Equal(new[] { "/keymaps/a.keymap" }, result);
    }

    [Fact]
    public void Push_NewPath_PutsItInFrontOfExistingEntries()
    {
        var result = RecentKeymaps.Push(new[] { "/a.keymap", "/b.keymap" }, "/c.keymap");

        Assert.Equal(new[] { "/c.keymap", "/a.keymap", "/b.keymap" }, result);
    }

    [Fact]
    public void Push_ExistingEntry_MovesToFrontWithoutDuplicate()
    {
        var result = RecentKeymaps.Push(new[] { "/a.keymap", "/b.keymap", "/c.keymap" }, "/c.keymap");

        Assert.Equal(new[] { "/c.keymap", "/a.keymap", "/b.keymap" }, result);
    }

    [Fact]
    public void Push_AtCapacityWithExistingEntry_KeepsLengthAtMax()
    {
        var current = Enumerable.Range(1, RecentKeymaps.MaxEntries)
            .Select(i => $"/{i}.keymap").ToList();

        var result = RecentKeymaps.Push(current, $"/{RecentKeymaps.MaxEntries}.keymap");

        Assert.Equal(RecentKeymaps.MaxEntries, result.Count);
        Assert.Equal($"/{RecentKeymaps.MaxEntries}.keymap", result[0]);
    }

    [Fact]
    public void Push_BeyondCapacity_TrimsOldestFromEnd()
    {
        var current = Enumerable.Range(1, RecentKeymaps.MaxEntries)
            .Select(i => $"/{i}.keymap").ToList();

        var result = RecentKeymaps.Push(current, "/fresh.keymap");

        Assert.Equal(RecentKeymaps.MaxEntries, result.Count);
        Assert.Equal("/fresh.keymap", result[0]);
        Assert.DoesNotContain($"/{RecentKeymaps.MaxEntries}.keymap", result);
    }

    [Fact]
    public void Push_NullPath_ReturnsUnchangedCopy()
    {
        var current = new[] { "/a.keymap", "/b.keymap" };

        var result = RecentKeymaps.Push(current, null!);

        Assert.Equal(current, result);
    }

    [Fact]
    public void Push_EmptyPath_ReturnsUnchangedCopy()
    {
        var current = new[] { "/a.keymap", "/b.keymap" };

        var result = RecentKeymaps.Push(current, "");

        Assert.Equal(current, result);
        Assert.NotSame(current, result);
    }

    [Fact]
    public void Push_WhitespacePath_ReturnsUnchangedCopy()
    {
        var current = new[] { "/a.keymap", "/b.keymap" };

        var result = RecentKeymaps.Push(current, "   ");

        Assert.Equal(current, result);
        Assert.NotSame(current, result);
    }

    [Fact]
    public void Remove_Present_RemovesOnlyThatEntry()
    {
        var result = RecentKeymaps.Remove(new[] { "/a.keymap", "/b.keymap", "/c.keymap" }, "/b.keymap");

        Assert.Equal(new[] { "/a.keymap", "/c.keymap" }, result);
    }

    [Fact]
    public void Remove_Absent_ReturnsUnchangedCopy()
    {
        var current = new[] { "/a.keymap", "/b.keymap" };

        var result = RecentKeymaps.Remove(current, "/zzz.keymap");

        Assert.Equal(current, result);
        Assert.NotSame(current, result);
    }

    [Fact]
    public void Remove_DifferentCasing_DoesNotRemove()
    {
        var current = new[] { "/keymaps/a.keymap", "/b.keymap" };

        var result = RecentKeymaps.Remove(current, "/Keymaps/A.keymap");

        Assert.Equal(current, result);
        Assert.NotSame(current, result);
    }

    [Fact]
    public void Push_DoesNotMutateInput()
    {
        var current = new List<string> { "/a.keymap", "/b.keymap" };

        var result = RecentKeymaps.Push(current, "/c.keymap");

        Assert.Equal(new[] { "/a.keymap", "/b.keymap" }, current);
        Assert.NotSame(current, result);
    }

    [Fact]
    public void Remove_DoesNotMutateInput()
    {
        var current = new List<string> { "/a.keymap", "/b.keymap" };

        var result = RecentKeymaps.Remove(current, "/a.keymap");

        Assert.Equal(new[] { "/a.keymap", "/b.keymap" }, current);
        Assert.NotSame(current, result);
    }
}
