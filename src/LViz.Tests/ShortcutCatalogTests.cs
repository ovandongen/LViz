using Avalonia.Input;
using LViz.App.Localization;
using LViz.App.ViewModels;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The data behind the read-only shortcut window. The edit-mode rows must
/// resolve through <see cref="EditShortcuts"/> to the action they claim — that
/// is what keeps the printed list from drifting away from what the keys do —
/// every row must show keys or input text, and every resx key it names must
/// resolve (the keys live in a table, so <c>LocalizationKeyTests</c>' literal
/// scan can't see them).
/// </summary>
public class ShortcutCatalogTests
{
    [Fact]
    public void EveryGesture_WithMetaCommand_ResolvesToItsRowsAction()
    {
        foreach (var (definition, gesture) in EditModeGestures())
            Assert.Equal(definition.Action, Resolve(gesture, KeyModifiers.Meta));
    }

    // The same rows must be right on Windows/Linux, where Ctrl is the command key.
    [Fact]
    public void EveryGesture_WithControlCommand_ResolvesToItsRowsAction()
    {
        foreach (var (definition, gesture) in EditModeGestures())
            Assert.Equal(definition.Action, Resolve(gesture, KeyModifiers.Control));
    }

    [Fact]
    public void EveryEditShortcut_IsListedOnARow()
    {
        var listed = ShortcutCatalog.All.Select(definition => definition.Action).ToHashSet();
        var missing = string.Join(", ", Enum.GetValues<EditShortcut>()
            .Where(action => action != EditShortcut.None && !listed.Contains(action)));

        Assert.True(missing.Length == 0, $"No shortcut row triggers: {missing}.");
    }

    [Fact]
    public void EveryDefinition_HasKeysOrInputText()
    {
        foreach (var definition in ShortcutCatalog.All)
            Assert.True(
                definition.Gestures.Count > 0 || !string.IsNullOrEmpty(definition.InputTextKey),
                $"Row '{definition.DescriptionKey}' shows neither keys nor input text.");
    }

    // Only the two edit-mode groups are EditShortcuts; ring, dialog and mouse
    // rows exist to explain keys and clicks the resolver never sees.
    [Fact]
    public void OnlyBoardAndLayerRows_MapToAnEditShortcut()
    {
        foreach (var definition in ShortcutCatalog.All)
            Assert.Equal(
                definition.Group is ShortcutGroup.Board or ShortcutGroup.Layers,
                definition.Action != EditShortcut.None);
    }

    [Fact]
    public void EveryGroup_HasRows()
    {
        foreach (var group in Enum.GetValues<ShortcutGroup>())
            Assert.Contains(ShortcutCatalog.All, definition => definition.Group == group);
    }

    [Fact]
    public void Rows_AreGroupedInShortcutGroupOrder() =>
        Assert.Equal(
            Enum.GetValues<ShortcutGroup>(),
            ShortcutCatalog.All.Select(definition => definition.Group).Distinct());

    [Fact]
    public void EveryCatalogKey_Resolves()
    {
        foreach (var definition in ShortcutCatalog.All)
        {
            AssertResolves(
                Loc.Instance[definition.DescriptionKey],
                $"description key {definition.DescriptionKey}");

            if (definition.InputTextKey is { } inputKey)
                AssertResolves(Loc.Instance[inputKey], $"input text key {inputKey}");
        }

        foreach (var group in Enum.GetValues<ShortcutGroup>())
            AssertResolves(ShortcutCatalog.GroupTitle(group), $"group title of {group}");
    }

    [Theory]
    [InlineData(true, "⇧⌘N")]
    [InlineData(false, "Ctrl+Shift+N")]
    public void FormatGesture_CommandPlusShiftLetter(bool isMac, string expected) =>
        Assert.Equal(
            expected,
            ShortcutCatalog.FormatGesture(new ShortcutGesture(Key.N, Command: true, Shift: true), isMac));

    [Theory]
    [InlineData(true, "⌥⌘←")]
    [InlineData(false, "Ctrl+Alt+←")]
    public void FormatGesture_CommandPlusAltArrow(bool isMac, string expected) =>
        Assert.Equal(
            expected,
            ShortcutCatalog.FormatGesture(new ShortcutGesture(Key.Left, Command: true, Alt: true), isMac));

    [Theory]
    [InlineData(true, "⌫")]
    [InlineData(false, "Backspace")]
    public void FormatGesture_PlainBack(bool isMac, string expected) =>
        Assert.Equal(
            expected,
            ShortcutCatalog.FormatGesture(new ShortcutGesture(Key.Back), isMac));

    [Theory]
    [InlineData(true, "⇧⌘Z / ⌘Y")]
    [InlineData(false, "Ctrl+Shift+Z / Ctrl+Y")]
    public void FormatKeys_JoinsAlternativeGestures(bool isMac, string expected) =>
        Assert.Equal(expected, ShortcutCatalog.FormatKeys(Row(EditShortcut.Redo), isMac));

    [Fact]
    public void FormatKeys_TranslatesInputText_WithoutGestures() =>
        Assert.Equal(
            "Double-click a key",
            ShortcutCatalog.FormatKeys(Row("Shortcuts_DoubleClickKey"), isMac: false));

    /// <summary>Every (row, gesture) pair the resolver is supposed to agree with.</summary>
    private static IEnumerable<(ShortcutDefinition Definition, ShortcutGesture Gesture)> EditModeGestures() =>
        ShortcutCatalog.All
            .Where(definition => definition.Action != EditShortcut.None)
            .SelectMany(definition => definition.Gestures.Select(gesture => (definition, gesture)));

    /// <summary>The catalog gesture's press, spelled with <paramref name="commandModifier"/>
    /// as the platform command key.</summary>
    private static EditShortcut Resolve(ShortcutGesture gesture, KeyModifiers commandModifier)
    {
        var modifiers = KeyModifiers.None;
        if (gesture.Command) modifiers |= commandModifier;
        if (gesture.Shift) modifiers |= KeyModifiers.Shift;
        if (gesture.Alt) modifiers |= KeyModifiers.Alt;

        return EditShortcuts.Resolve(gesture.Key, modifiers, commandModifier);
    }

    private static ShortcutDefinition Row(EditShortcut action) =>
        ShortcutCatalog.All.First(definition => definition.Action == action);

    private static ShortcutDefinition Row(string inputTextKey) =>
        ShortcutCatalog.All.First(definition => definition.InputTextKey == inputTextKey);

    private static void AssertResolves(string text, string what) =>
        Assert.True(
            text.Length > 0 && !text.StartsWith("[", StringComparison.Ordinal),
            $"{what} is a missing resx key: '{text}'.");
}
