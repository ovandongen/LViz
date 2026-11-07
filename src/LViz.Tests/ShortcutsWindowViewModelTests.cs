using LViz.App.Localization;
using LViz.App.ViewModels;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The read-only view model behind the "Keyboard shortcuts" window: it lists
/// every <see cref="ShortcutCatalog"/> row, grouped in enum order and spelled
/// for the platform it was built for.
/// </summary>
public class ShortcutsWindowViewModelTests
{
    [Fact]
    public void Groups_AreTheCatalogGroupsInEnumOrder()
    {
        var expected = new[]
        {
            ShortcutCatalog.GroupTitle(ShortcutGroup.Board),
            ShortcutCatalog.GroupTitle(ShortcutGroup.Layers),
            ShortcutCatalog.GroupTitle(ShortcutGroup.KeyRing),
            ShortcutCatalog.GroupTitle(ShortcutGroup.KeyDialog),
            ShortcutCatalog.GroupTitle(ShortcutGroup.Mouse),
        };

        Assert.Equal(expected, new ShortcutsWindowViewModel(isMac: true).Groups.Select(g => g.Title));
    }

    [Fact]
    public void Layers_NewLayerRow_ShowsPlatformKeysAndResolvedDescription()
    {
        var expectedDescription = Loc.Instance["Shortcuts_NewLayer"];

        var mac = LayersOf(new ShortcutsWindowViewModel(isMac: true))
            .Single(row => row.Description == expectedDescription);
        Assert.Equal("⇧⌘N", mac.Keys);

        var other = LayersOf(new ShortcutsWindowViewModel(isMac: false))
            .Single(row => row.Description == expectedDescription);
        Assert.Equal("Ctrl+Shift+N", other.Keys);
    }

    [Fact]
    public void EveryCatalogRow_IsListed()
    {
        var total = new ShortcutsWindowViewModel(isMac: true).Groups.Sum(g => g.Rows.Count);
        Assert.Equal(ShortcutCatalog.All.Count, total);
    }

    private static IReadOnlyList<ShortcutRow> LayersOf(ShortcutsWindowViewModel viewModel) =>
        viewModel.Groups
            .Single(group => group.Title == ShortcutCatalog.GroupTitle(ShortcutGroup.Layers))
            .Rows;
}
