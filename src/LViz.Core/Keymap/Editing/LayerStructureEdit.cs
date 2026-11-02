using LViz.Core.Models;

namespace LViz.Core.Keymap.Editing;

/// <summary>
/// One structural change to a keymap's layer list. Shared contract between the
/// edit session (records and undoes these) and the layer writer (applies them to
/// the <c>.keymap</c> text). Every index is against the layer list as it is right
/// before this edit; a list of edits is applied in order.
/// </summary>
public abstract record LayerStructureEdit
{
    private LayerStructureEdit() { }

    /// <summary>
    /// Insert a new layer at <paramref name="AtIndex"/> (0 … layer count; equal to
    /// the count appends). <paramref name="Name"/> becomes its <c>display-name</c>.
    /// <paramref name="Bindings"/> has one binding per key (the keymap's key count).
    /// Duplicating a layer is an <see cref="Add"/> with copied bindings.
    /// </summary>
    public sealed record Add(int AtIndex, string Name, IReadOnlyList<KeyBinding> Bindings) : LayerStructureEdit;

    /// <summary>Remove the layer at <paramref name="Index"/>.</summary>
    public sealed record Delete(int Index) : LayerStructureEdit;

    /// <summary>
    /// Move the layer at <paramref name="From"/> so it ends up at
    /// <paramref name="To"/> (remove, then insert at <paramref name="To"/> in the
    /// shortened list). <paramref name="From"/> == <paramref name="To"/> is a no-op.
    /// </summary>
    public sealed record Move(int From, int To) : LayerStructureEdit;
}
