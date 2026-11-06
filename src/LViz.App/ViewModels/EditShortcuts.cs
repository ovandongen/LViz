using Avalonia.Input;

namespace LViz.App.ViewModels;

/// <summary>
/// The edit-mode action a key press stands for, or <see cref="None"/> when the
/// press is nobody's business and the window keeps handling it normally.
/// </summary>
public enum EditShortcut
{
    /// <summary>Not an edit shortcut.</summary>
    None,

    /// <summary>Undo the last edit step.</summary>
    Undo,

    /// <summary>Redo the last undone step.</summary>
    Redo,

    /// <summary>Clear the selected key to <c>&amp;trans</c>.</summary>
    ClearToTransparent,

    /// <summary>Clear the selected key to <c>&amp;none</c>.</summary>
    ClearToNone,

    /// <summary>Drop the key selection.</summary>
    ClearSelection,

    /// <summary>Open the key dialog on the selected key.</summary>
    OpenKeyDialog,

    /// <summary>Open the key editor the setting does <em>not</em> pick —
    /// Shift+Enter always shows the dialog when the ring is the default,
    /// and the ring when the dialog is.</summary>
    OpenOtherKeyEditor,

    /// <summary>Append a new layer to the keymap.</summary>
    NewLayer,

    /// <summary>Copy the shown layer right after itself.</summary>
    DuplicateLayer,

    /// <summary>Move the shown layer one place earlier.</summary>
    MoveLayerLeft,

    /// <summary>Move the shown layer one place later.</summary>
    MoveLayerRight,
}

/// <summary>
/// Maps a raw key press to the edit-mode shortcut it triggers, without
/// touching the UI. Pure so the rules are testable without a window;
/// <see cref="MainWindow"/> just feeds it the event and dispatches the result.
/// </summary>
public static class EditShortcuts
{
    /// <summary>
    /// Classify <paramref name="key"/> + <paramref name="modifiers"/> against
    /// the edit-mode rules. <paramref name="commandModifier"/> is the platform's
    /// command key (<c>Meta</c> on macOS, <c>Control</c> elsewhere, from
    /// <c>PlatformSettings.HotkeyConfiguration.CommandModifiers</c>), so
    /// Cmd+Z on a Mac and Ctrl+Z elsewhere hit the same rule.
    /// <paramref name="physicalKey"/> is the pressed key's position
    /// (<c>KeyEventArgs.PhysicalKey</c>): the numpad Enter matches on it,
    /// because the <see cref="Key"/> value it reports differs per platform.
    /// </summary>
    public static EditShortcut Resolve(
        Key key,
        KeyModifiers modifiers,
        KeyModifiers commandModifier,
        PhysicalKey physicalKey = PhysicalKey.None)
    {
        // "cmd" means every flag of the platform command modifier is down, so
        // a Meta+Control setup could not be short-circuited by holding one.
        var cmd = modifiers.HasFlag(commandModifier);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);

        if (key == Key.N && cmd && shift && !alt)
            return EditShortcut.NewLayer;

        if (key == Key.D && cmd && shift && !alt)
            return EditShortcut.DuplicateLayer;

        if (key == Key.Left && cmd && alt && !shift)
            return EditShortcut.MoveLayerLeft;

        if (key == Key.Right && cmd && alt && !shift)
            return EditShortcut.MoveLayerRight;

        if (key == Key.Z && cmd)
            return shift ? EditShortcut.Redo : EditShortcut.Undo;

        if (key == Key.Y && cmd && !shift)
            return EditShortcut.Redo;

        if ((key == Key.Delete || key == Key.Back) && !cmd && !alt)
            return shift ? EditShortcut.ClearToNone : EditShortcut.ClearToTransparent;

        if (key == Key.Escape && modifiers == KeyModifiers.None)
            return EditShortcut.ClearSelection;

        if (key == Key.Enter && modifiers == KeyModifiers.None)
            return EditShortcut.OpenKeyDialog;

        // Numpad Enter: its Key value differs per platform, its physical
        // position does not, so match on the position and ignore the Key.
        if (physicalKey == PhysicalKey.NumPadEnter && modifiers == KeyModifiers.None)
            return EditShortcut.OpenKeyDialog;

        // Shift+Enter (either Enter) opens the other key editor. Cmd/Alt
        // combos stay nobody's business (Cmd+Enter is a text-field chord).
        if ((key == Key.Enter || physicalKey == PhysicalKey.NumPadEnter) && shift && !cmd && !alt)
            return EditShortcut.OpenOtherKeyEditor;

        return EditShortcut.None;
    }
}
