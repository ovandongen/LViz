using LViz.Core.Keymap.Catalog;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// The bundled ZMK keycode catalog: load order, lookup by canonical name /
/// alias / deprecated name, per-category listing and ranked search.
/// Expectations are pinned against <c>Resources/zmk-keycodes.json</c>.
/// </summary>
public class ZmkKeycodeCatalogTests
{
    [Fact]
    public void All_LoadsEveryEntry_InFileOrder()
    {
        Assert.Equal(373, ZmkKeycodeCatalog.All.Count);
        Assert.Equal("SYSTEM_POWER", ZmkKeycodeCatalog.All[0].Name);
    }

    [Theory]
    [InlineData(ZmkKeycodeCategory.Letters, 26)]
    [InlineData(ZmkKeycodeCategory.Digits, 10)]
    [InlineData(ZmkKeycodeCategory.Modifiers, 8)]
    [InlineData(ZmkKeycodeCategory.Function, 24)]
    [InlineData(ZmkKeycodeCategory.Navigation, 8)]
    public void InCategory_CountsMatchZmkHeader(ZmkKeycodeCategory category, int expected)
    {
        Assert.Equal(expected, ZmkKeycodeCatalog.InCategory(category).Count);
    }

    [Fact]
    public void Find_ResolvesAliases()
    {
        Assert.Equal("NUMBER_1", ZmkKeycodeCatalog.Find("N1")!.Name);
    }

    [Fact]
    public void Find_ResolvesDeprecatedNames()
    {
        Assert.Equal("NUMBER_1", ZmkKeycodeCatalog.Find("NUM_1")!.Name);
    }

    [Fact]
    public void Find_IsCaseSensitive()
    {
        // ZMK names are upper-case macros; a lower-case query is a different string.
        Assert.Null(ZmkKeycodeCatalog.Find("n1"));
    }

    [Fact]
    public void Find_UnknownName_ReturnsNull()
    {
        Assert.Null(ZmkKeycodeCatalog.Find("FOO"));
    }

    [Fact]
    public void Find_ReportsShiftOf()
    {
        Assert.Equal("NUMBER_1", ZmkKeycodeCatalog.Find("EXCL")!.ShiftOf);
    }

    [Fact]
    public void Find_KeepsZmkDescriptions()
    {
        Assert.Equal("Keyboard a and A", ZmkKeycodeCatalog.Find("A")!.Description);
    }

    [Fact]
    public void Find_ReportsCategory()
    {
        var lshift = ZmkKeycodeCatalog.Find("LSHFT")!;
        Assert.Equal("LEFT_SHIFT", lshift.Name);
        Assert.Equal(ZmkKeycodeCategory.Modifiers, lshift.Category);
    }

    [Theory]
    [InlineData("ret", "RETURN")]       // alias RET equals the query
    [InlineData("a", "A")]              // canonical name equals the query
    [InlineData("bspc", "BACKSPACE")]   // alias BSPC equals the query
    [InlineData("page up", "PAGE_UP")]  // space-to-underscore variant equals the name
    [InlineData("excl", "EXCLAMATION")] // alias EXCL equals the query
    public void Search_RanksExactNameMatchesFirst(string query, string expectedFirst)
    {
        var results = ZmkKeycodeCatalog.Search(query);
        Assert.NotEmpty(results);
        Assert.Equal(expectedFirst, results[0].Name);
    }

    [Fact]
    public void Search_EmptyQueryWithCategory_ReturnsCategoryInFileOrder()
    {
        var names = ZmkKeycodeCatalog.Search("", ZmkKeycodeCategory.Digits).Select(k => k.Name).ToList();
        Assert.Equal(
            new[]
            {
                "NUMBER_1", "NUMBER_2", "NUMBER_3", "NUMBER_4", "NUMBER_5",
                "NUMBER_6", "NUMBER_7", "NUMBER_8", "NUMBER_9", "NUMBER_0",
            },
            names);
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        Assert.Empty(ZmkKeycodeCatalog.Search("zzzz"));
    }

    [Fact]
    public void Search_CategoryFilter_KeepsOnlyMatchingEntries()
    {
        var names = ZmkKeycodeCatalog.Search("arrow", ZmkKeycodeCategory.Navigation).Select(k => k.Name).ToList();
        Assert.Equal(new[] { "RIGHT_ARROW", "LEFT_ARROW", "DOWN_ARROW", "UP_ARROW" }, names);
    }

    [Fact]
    public void EveryEntry_HasNameAndDescription_AndResolvableShiftOf()
    {
        foreach (var keycode in ZmkKeycodeCatalog.All)
        {
            Assert.False(string.IsNullOrEmpty(keycode.Name));
            Assert.False(string.IsNullOrEmpty(keycode.Description));
            if (keycode.ShiftOf is not null)
            {
                Assert.NotNull(ZmkKeycodeCatalog.Find(keycode.ShiftOf));
            }
        }
    }
}
