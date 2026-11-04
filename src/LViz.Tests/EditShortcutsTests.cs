using Avalonia.Input;
using LViz.App.ViewModels;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The edit-mode shortcut table. The command modifier is macOS's Meta except
/// where a row says otherwise — the same rules must fire with Control as the
/// command key on Windows/Linux.
/// </summary>
public class EditShortcutsTests
{
    [Theory]
    [InlineData(Key.Z, KeyModifiers.Meta, EditShortcut.Undo)]
    [InlineData(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift, EditShortcut.Redo)]
    [InlineData(Key.Y, KeyModifiers.Meta, EditShortcut.Redo)]
    [InlineData(Key.Y, KeyModifiers.Meta | KeyModifiers.Shift, EditShortcut.None)]
    [InlineData(Key.Z, KeyModifiers.Control, EditShortcut.None)]
    [InlineData(Key.Delete, KeyModifiers.None, EditShortcut.ClearToTransparent)]
    [InlineData(Key.Back, KeyModifiers.None, EditShortcut.ClearToTransparent)]
    [InlineData(Key.Delete, KeyModifiers.Shift, EditShortcut.ClearToNone)]
    [InlineData(Key.Back, KeyModifiers.Shift, EditShortcut.ClearToNone)]
    [InlineData(Key.Delete, KeyModifiers.Meta, EditShortcut.None)]
    [InlineData(Key.Back, KeyModifiers.Alt, EditShortcut.None)]
    [InlineData(Key.Escape, KeyModifiers.None, EditShortcut.ClearSelection)]
    [InlineData(Key.Escape, KeyModifiers.Shift, EditShortcut.None)]
    [InlineData(Key.A, KeyModifiers.None, EditShortcut.None)]
    public void Resolve_WithMetaCommandModifier(Key key, KeyModifiers modifiers, EditShortcut expected) =>
        Assert.Equal(expected, EditShortcuts.Resolve(key, modifiers, KeyModifiers.Meta));

    [Fact]
    public void Resolve_WithControlCommandModifier_ZClearsUndo() =>
        Assert.Equal(EditShortcut.Undo, EditShortcuts.Resolve(Key.Z, KeyModifiers.Control, KeyModifiers.Control));
}
