using System.Text;
using Avalonia.Input;
using LViz.App.Localization;

namespace LViz.App.ViewModels;

/// <summary>
/// Which part of the app a shortcut row belongs to: the board and the layer
/// tabs while editing, the key ring, the key dialog, or the mouse. The order of
/// the members is the order the shortcut window lists them.
/// </summary>
public enum ShortcutGroup
{
    /// <summary>Edit-mode keys over the board.</summary>
    Board,

    /// <summary>Edit-mode keys over the layer tabs.</summary>
    Layers,

    /// <summary>Keys of the radial key editor.</summary>
    KeyRing,

    /// <summary>Keys of the key dialog.</summary>
    KeyDialog,

    /// <summary>Mouse actions.</summary>
    Mouse,
}

/// <summary>
/// One key combination; <see cref="Command"/> is the platform command key
/// (⌘ on macOS, Ctrl elsewhere).
/// </summary>
public sealed record ShortcutGesture(
    Key Key, bool Command = false, bool Shift = false, bool Alt = false);

/// <summary>
/// One row of the shortcut list: either key gestures (alternatives) or a
/// translated mouse/typing text, a translated description, and the edit
/// shortcut the gestures trigger (None for rows outside EditShortcuts).
/// Everything but <see cref="Action"/> is a resx key rather than resolved
/// text, so the list keeps following a language switch at runtime.
/// </summary>
public sealed record ShortcutDefinition(
    ShortcutGroup Group,
    IReadOnlyList<ShortcutGesture> Gestures,
    string? InputTextKey,
    string DescriptionKey,
    EditShortcut Action);

/// <summary>
/// Every shortcut and mouse action of keymap editing, the key ring and the key
/// dialog, as the read-only "Keyboard shortcuts" window lists them. The
/// edit-mode rows mirror <see cref="EditShortcuts.Resolve"/> — that is pinned by
/// a test, so the list can't drift from what the keys actually do. Keys are
/// spelled for the platform by <see cref="FormatGesture"/>.
/// </summary>
public static class ShortcutCatalog
{
    /// <summary>Every row, grouped in <see cref="ShortcutGroup"/> order.</summary>
    public static IReadOnlyList<ShortcutDefinition> All { get; } =
    [
        // Board (edit mode)
        KeyRow(ShortcutGroup.Board, "Shortcuts_EditKey", EditShortcut.OpenKeyDialog,
            Gesture(Key.Enter)),
        KeyRow(ShortcutGroup.Board, "Shortcuts_ClearTrans", EditShortcut.ClearToTransparent,
            Gesture(Key.Delete), Gesture(Key.Back)),
        KeyRow(ShortcutGroup.Board, "Shortcuts_ClearNone", EditShortcut.ClearToNone,
            Gesture(Key.Delete, shift: true), Gesture(Key.Back, shift: true)),
        KeyRow(ShortcutGroup.Board, "Shortcuts_Deselect", EditShortcut.ClearSelection,
            Gesture(Key.Escape)),
        KeyRow(ShortcutGroup.Board, "Shortcuts_Undo", EditShortcut.Undo,
            Gesture(Key.Z, command: true)),
        KeyRow(ShortcutGroup.Board, "Shortcuts_Redo", EditShortcut.Redo,
            Gesture(Key.Z, command: true, shift: true), Gesture(Key.Y, command: true)),

        // Layers (edit mode)
        KeyRow(ShortcutGroup.Layers, "Shortcuts_NewLayer", EditShortcut.NewLayer,
            Gesture(Key.N, command: true, shift: true)),
        KeyRow(ShortcutGroup.Layers, "Shortcuts_DuplicateLayer", EditShortcut.DuplicateLayer,
            Gesture(Key.D, command: true, shift: true)),
        KeyRow(ShortcutGroup.Layers, "Shortcuts_MoveLayerLeft", EditShortcut.MoveLayerLeft,
            Gesture(Key.Left, command: true, alt: true)),
        KeyRow(ShortcutGroup.Layers, "Shortcuts_MoveLayerRight", EditShortcut.MoveLayerRight,
            Gesture(Key.Right, command: true, alt: true)),

        // Key ring
        KeyRow(ShortcutGroup.KeyRing, "Shortcuts_RingAround", EditShortcut.None,
            Gesture(Key.Left), Gesture(Key.Right)),
        KeyRow(ShortcutGroup.KeyRing, "Shortcuts_RingList", EditShortcut.None,
            Gesture(Key.Up), Gesture(Key.Down)),
        KeyRow(ShortcutGroup.KeyRing, "Shortcuts_RingBand", EditShortcut.None,
            Gesture(Key.Tab)),
        KeyRow(ShortcutGroup.KeyRing, "Shortcuts_RingEnter", EditShortcut.None,
            Gesture(Key.Enter)),
        KeyRow(ShortcutGroup.KeyRing, "Shortcuts_RingBack", EditShortcut.None,
            Gesture(Key.Back)),
        InputRow(ShortcutGroup.KeyRing, "Shortcuts_Typing", "Shortcuts_RingSearch"),
        KeyRow(ShortcutGroup.KeyRing, "Shortcuts_Cancel", EditShortcut.None,
            Gesture(Key.Escape)),

        // Key dialog
        KeyRow(ShortcutGroup.KeyDialog, "Shortcuts_DialogTabs", EditShortcut.None,
            Gesture(Key.Left, command: true), Gesture(Key.Right, command: true)),
        KeyRow(ShortcutGroup.KeyDialog, "Shortcuts_Cancel", EditShortcut.None,
            Gesture(Key.Escape)),

        // Mouse
        InputRow(ShortcutGroup.Mouse, "Shortcuts_DoubleClickKey", "Shortcuts_EditKeyMouse"),
        InputRow(ShortcutGroup.Mouse, "Shortcuts_RightClickKey", "Shortcuts_EditLabel"),
        InputRow(ShortcutGroup.Mouse, "Shortcuts_RightClickRing", "Shortcuts_RingBack"),
        InputRow(ShortcutGroup.Mouse, "Shortcuts_DoubleClickChoice", "Shortcuts_PickAndSave"),
        InputRow(ShortcutGroup.Mouse, "Shortcuts_DoubleClickCombo", "Shortcuts_EditCombo"),
    ];

    /// <summary>A gesture with the platform command key spelled as a flag.</summary>
    private static ShortcutGesture Gesture(
        Key key, bool command = false, bool shift = false, bool alt = false) =>
        new(key, command, shift, alt);

    /// <summary>A row of key gestures (alternatives for the same action).</summary>
    private static ShortcutDefinition KeyRow(
        ShortcutGroup group, string descriptionKey, EditShortcut action,
        params ShortcutGesture[] gestures) =>
        new(group, gestures, null, descriptionKey, action);

    /// <summary>A row whose left column is translated text instead of keys.</summary>
    private static ShortcutDefinition InputRow(
        ShortcutGroup group, string inputTextKey, string descriptionKey) =>
        new(group, [], inputTextKey, descriptionKey, EditShortcut.None);

    /// <summary>
    /// Render one combination for <paramref name="isMac"/>: Apple's modifier
    /// symbols with no separator there, <c>Ctrl+</c>-style prefixes elsewhere.
    /// </summary>
    public static string FormatGesture(ShortcutGesture gesture, bool isMac)
    {
        var text = new StringBuilder();

        if (isMac)
        {
            // Apple's order: control, option, shift, command.
            if (gesture.Alt) text.Append("⌥");
            if (gesture.Shift) text.Append("⇧");
            if (gesture.Command) text.Append("⌘");
        }
        else
        {
            if (gesture.Command) text.Append("Ctrl+");
            if (gesture.Alt) text.Append("Alt+");
            if (gesture.Shift) text.Append("Shift+");
        }

        return text.Append(KeyText(gesture.Key, isMac)).ToString();
    }

    /// <summary>
    /// The left column of a row: its gestures joined with " / ", or the
    /// translated mouse/typing text of a row without gestures.
    /// </summary>
    public static string FormatKeys(ShortcutDefinition definition, bool isMac)
    {
        if (definition.Gestures.Count > 0)
            return string.Join(" / ", definition.Gestures.Select(gesture => FormatGesture(gesture, isMac)));

        return definition.InputTextKey is { } key ? Loc.Instance[key] : "";
    }

    /// <summary>The translated heading of a group.</summary>
    public static string GroupTitle(ShortcutGroup group) => Loc.Instance[$"Shortcuts_Group_{group}"];

    /// <summary>What the window shows for one key; letters show as the letter.</summary>
    private static string KeyText(Key key, bool isMac) => key switch
    {
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Enter => "Enter",
        Key.Escape => "Esc",
        Key.Delete => "Delete",
        Key.Back => isMac ? "⌫" : "Backspace",
        Key.Tab => "Tab",
        _ => key.ToString(),
    };
}
