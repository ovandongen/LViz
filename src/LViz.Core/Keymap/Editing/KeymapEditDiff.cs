using LViz.Core.Keymap.Writer;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Editing;

/// <summary>
/// What <see cref="KeymapEditDiff.Compute"/> found: the cells to rewrite and the
/// layers to rename, in exactly the shape
/// <see cref="KeymapWriter.Apply"/> takes.
/// </summary>
/// <param name="Bindings">One edit per changed cell, ordered by layer then key.</param>
/// <param name="Renames">One rename per layer whose <c>display-name</c> moved, ordered by layer.</param>
public sealed record KeymapEditDiffResult(IReadOnlyList<BindingEdit> Bindings, IReadOnlyList<LayerRename> Renames);

/// <summary>
/// Turns two keymaps of the same shape into writer input: every cell that is not
/// the same binding, and every layer that is not the same name.
/// <para>
/// This is the last step of saving an edit session. Structural edits (layers
/// added, deleted or moved) are applied to the <c>.keymap</c> text first, so by the
/// time <see cref="Compute"/> runs both sides have the same layer list and the same
/// key count per layer — the diff carries only what is left: bindings and names.
/// Both sides are compared by <b>position</b>, which is also how the writer
/// addresses the file.
/// </para>
/// </summary>
public static class KeymapEditDiff
{
    /// <exception cref="ArgumentNullException"><paramref name="from"/> or
    /// <paramref name="to"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The two keymaps have a different number of layers, or a layer whose binding
    /// count differs — nothing here can insert or remove a cell.
    /// </exception>
    public static KeymapEditDiffResult Compute(KeyboardConfig from, KeyboardConfig to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from.Layers.Count != to.Layers.Count)
            throw new ArgumentException(
                $"cannot diff keymaps with {from.Layers.Count} and {to.Layers.Count} layers.",
                nameof(to));

        var bindings = new List<BindingEdit>();
        var renames = new List<LayerRename>();

        for (int layer = 0; layer < from.Layers.Count; layer++)
        {
            var before = from.Layers[layer];
            var after = to.Layers[layer];

            if (before.Bindings.Count != after.Bindings.Count)
                throw new ArgumentException(
                    $"cannot diff layer {layer}: it has {before.Bindings.Count} binding(s) before "
                    + $"and {after.Bindings.Count} after.",
                    nameof(to));

            for (int key = 0; key < before.Bindings.Count; key++)
                if (!before.Bindings[key].SameAs(after.Bindings[key]))
                    bindings.Add(new BindingEdit(layer, key, after.Bindings[key]));

            if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
                renames.Add(new LayerRename(layer, after.Name));
        }

        return new KeymapEditDiffResult(bindings, renames);
    }
}
