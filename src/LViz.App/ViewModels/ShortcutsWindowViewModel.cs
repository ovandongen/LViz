using LViz.App.Localization;

namespace LViz.App.ViewModels;

/// <summary>
/// One row of the shortcuts window: the key text (or translated
/// mouse/typing text) on the left, its translated description on the right.
/// </summary>
public sealed record ShortcutRow(string Keys, string Description);

/// <summary>
/// One section of the shortcuts window: the translated group heading and
/// its rows in catalog order.
/// </summary>
public sealed record ShortcutGroupView(string Title, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// Read-only view model for the "Keyboard shortcuts" window: every
/// <see cref="ShortcutCatalog"/> row resolved to display text for the host
/// platform (⌘ symbols on macOS, Ctrl+ prefixes elsewhere) and grouped in
/// <see cref="ShortcutGroup"/> order, skipping groups with no rows. Text is
/// resolved once at construction — the window is modal, and reopening it
/// after a language switch resolves the new language.
/// </summary>
public sealed class ShortcutsWindowViewModel
{
    public ShortcutsWindowViewModel(bool isMac)
    {
        Groups = Enum.GetValues<ShortcutGroup>()
            .Select(group => new ShortcutGroupView(
                ShortcutCatalog.GroupTitle(group),
                ShortcutCatalog.All
                    .Where(definition => definition.Group == group)
                    .Select(definition => new ShortcutRow(
                        ShortcutCatalog.FormatKeys(definition, isMac),
                        Loc.Instance[definition.DescriptionKey]))
                    .ToList()))
            .Where(group => group.Rows.Count > 0)
            .ToList();
    }

    /// <summary>One view per group that has rows, in enum order.</summary>
    public IReadOnlyList<ShortcutGroupView> Groups { get; }
}
