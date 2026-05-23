using LViz.App.Services;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Smoke tests for the reflective loader of the Font Awesome catalog
/// embedded in <c>Projektanker.Icons.Avalonia.FontAwesome</c>. A package
/// bump that renames the manifest resource or restructures the JSON would
/// break the picker silently — these tests fail loudly instead.
/// </summary>
public class FontAwesomeCatalogTests
{
    [Fact]
    public void All_IsNonEmpty()
    {
        Assert.NotEmpty(FontAwesomeCatalog.All);
    }

    [Fact]
    public void All_EntriesAreFaPrefixed()
    {
        foreach (var e in FontAwesomeCatalog.All)
        {
            Assert.False(string.IsNullOrEmpty(e.Name));
            Assert.StartsWith("fa-", e.Name);
        }
    }

    [Fact]
    public void Contains_WellKnownIcons()
    {
        // fa-house and fa-mug-saucer are stable FA 6 icons (the former
        // "fa-home" / "fa-coffee" were renamed upstream). If both vanish
        // on a future package bump the catalog wiring is probably broken.
        var names = FontAwesomeCatalog.All.Select(e => e.Name).ToHashSet();
        Assert.Contains("fa-house", names);
        Assert.Contains("fa-mug-saucer", names);
    }

    [Fact]
    public void Filter_MatchesByName()
    {
        var result = FontAwesomeCatalog.Filter("house");
        Assert.Contains(result, e => e.Name == "fa-house");
    }

    [Fact]
    public void Filter_MatchesBySearchTerm()
    {
        // "mug" is a search term on fa-mug-saucer (per upstream FA metadata).
        var result = FontAwesomeCatalog.Filter("mug");
        Assert.Contains(result, e => e.Name == "fa-mug-saucer");
    }

    [Fact]
    public void Filter_EmptyQueryReturnsAll()
    {
        var result = FontAwesomeCatalog.Filter("");
        Assert.Equal(FontAwesomeCatalog.All.Count, result.Count);
    }
}
