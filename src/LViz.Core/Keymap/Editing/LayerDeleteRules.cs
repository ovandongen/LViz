using LViz.Core.Keymap.Writer;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Editing;

/// <summary>
/// A place in the keymap <b>text</b> that names a layer and that the layer
/// writers do not rewrite: a numeric reference outside the layer blocks and the
/// combos node, or a use of the layer's own <c>#define</c> name. Facts only; the
/// text scan produces them, <see cref="LayerDeleteRules"/> decides what they mean.
/// </summary>
/// <param name="Layer">The layer (parsed index) that is named.</param>
/// <param name="Kind">How it is named: <see cref="LayerDeleteBlockerKind.ConditionalLayer"/>,
/// <see cref="LayerDeleteBlockerKind.Other"/> or <see cref="LayerDeleteBlockerKind.DefineUsage"/>.</param>
/// <param name="Name">The enclosing node name, or the define name for a define usage.</param>
/// <param name="Line">1-based line of the use.</param>
internal readonly record struct LayerTextUse(int Layer, LayerDeleteBlockerKind Kind, string Name, int Line);

/// <summary>
/// The policy of deleting a layer, on the parsed <see cref="KeyboardConfig"/>:
/// whether it may go, what it would clear, and how a refusal reads. Pure model
/// logic; the only thing taken from the text is the list of
/// <see cref="LayerTextUse"/> facts, which the caller scans once.
/// <see cref="KeymapLayerWriter"/> uses <see cref="Check"/> both for the dialog's
/// pre-check and as the refusal in <c>Apply</c>, so the two cannot disagree.
/// </summary>
internal static class LayerDeleteRules
{
    /// <summary>
    /// What deleting layer <paramref name="index"/> would do. Blockers come out
    /// last layer, then combos in config order, then <paramref name="textUses"/>
    /// of the layer in the order given (numeric references first, then define
    /// usages, each in file order). References inside layer bindings are not
    /// blockers — those are cleared, and counted by
    /// <see cref="LayerReferenceRemapper.CountClearedBindings"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is outside the parsed layers.
    /// </exception>
    public static LayerDeleteCheck Check(
        KeyboardConfig config, IReadOnlyList<LayerTextUse> textUses, int index)
    {
        int count = config.Layers.Count;
        if (index < 0 || index >= count)
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"layer {WriterText.Num(index)} is out of range 0…{WriterText.Num(count - 1)}.");

        var blockers = new List<LayerDeleteBlocker>();

        if (count == 1)
            blockers.Add(new(LayerDeleteBlockerKind.LastLayer, config.Layers[0].Name, 0));

        // The model first: the combo — not the line inside it — is the unit the
        // user has to fix, and its binding and its layer scope sit apart. A
        // combo's own number also turns up in the text scan; the caller leaves
        // it out so the combo is reported once.
        var holdTaps = LayerReferenceRemapper.HoldTapLookup(config);
        foreach (var combo in config.Combos)
            if (TargetLayerOf(combo.Binding, holdTaps) == index || combo.Layers.Contains(index))
                blockers.Add(new(LayerDeleteBlockerKind.Combo, combo.Name, 0));

        foreach (var use in textUses)
            if (use.Layer == index)
                blockers.Add(new(use.Kind, use.Name, use.Line));

        return new LayerDeleteCheck(blockers, LayerReferenceRemapper.CountClearedBindings(config, index));
    }

    /// <summary>
    /// One edit per binding that points at the deleted layer, carrying the cleared
    /// binding read back out of <paramref name="expected"/> — the model the
    /// remapper says the file holds afterwards. Reading it there instead of
    /// re-deriving the clear rule here is what guarantees the text pass and the
    /// final validation want the same result. Indices stay in the <b>old</b> space.
    /// </summary>
    public static List<BindingEdit> ClearedEdits(
        KeyboardConfig config, KeyboardConfig expected, int index)
    {
        var holdTaps = LayerReferenceRemapper.HoldTapLookup(config);
        var edits = new List<BindingEdit>();

        for (int layer = 0; layer < config.Layers.Count; layer++)
        {
            if (layer == index) continue;

            // Where this layer lands once the deleted one is gone: the row of
            // `expected` its cleared bindings were written into.
            int next = layer < index ? layer : layer - 1;
            var bindings = config.Layers[layer].Bindings;

            for (int key = 0; key < bindings.Count; key++)
                if (TargetLayerOf(bindings[key], holdTaps) == index)
                    edits.Add(new BindingEdit(layer, key, expected.Layers[next].Bindings[key]));
        }

        return edits;
    }

    /// <summary>
    /// The refusal for a caller that skipped the pre-check. The dialog translates
    /// the check itself; this string is for the log and the stack trace, so it
    /// names every blocker rather than the first.
    /// </summary>
    public static string DescribeBlockers(
        KeyboardConfig config, int index, IReadOnlyList<LayerDeleteBlocker> blockers)
    {
        string name = index >= 0 && index < config.Layers.Count
            ? $" '{config.Layers[index].Name}'"
            : string.Empty;

        return $"layer {WriterText.Num(index)}{name} cannot be deleted: "
            + string.Join("; ", blockers.Select(Describe)) + ".";
    }

    private static string Describe(LayerDeleteBlocker blocker) => blocker.Kind switch
    {
        LayerDeleteBlockerKind.LastLayer => "it is the keymap's only layer",
        LayerDeleteBlockerKind.Combo => $"combo '{blocker.Name}' uses it",
        LayerDeleteBlockerKind.ConditionalLayer =>
            $"conditional layer '{blocker.Name}' references it on line {WriterText.Num(blocker.Line)}",
        LayerDeleteBlockerKind.DefineUsage =>
            $"define '{blocker.Name}' is used outside the layer blocks on line {WriterText.Num(blocker.Line)}",
        _ => $"'{blocker.Name}' references it on line {WriterText.Num(blocker.Line)}",
    };

    /// <summary>Which layer a binding pushes, its hold-tap included; null when it
    /// pushes none. The same rule the remapper and the overlay use.</summary>
    private static int? TargetLayerOf(KeyBinding binding, Dictionary<string, HoldTap> holdTaps) =>
        LayerBindingResolver.ResolveTargetLayer(binding, holdTaps.GetValueOrDefault(binding.Behavior));
}
