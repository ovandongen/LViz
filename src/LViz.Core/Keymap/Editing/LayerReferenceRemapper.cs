using System.Globalization;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Editing;

/// <summary>
/// Applies a <see cref="LayerStructureEdit"/> to a parsed <see cref="KeyboardConfig"/>:
/// the layer list changes and every layer reference (layer bindings, combo
/// bindings and layer scopes, macro bodies) is renumbered to match. What counts
/// as a layer reference is delegated to
/// <see cref="LayerBindingResolver.ResolveTargetLayer"/> — this class never
/// re-derives that rule.
/// <para>
/// Deleting a still-referenced layer clears the reference inside layer
/// bindings: <c>&amp;mo/&amp;to/&amp;tog/&amp;sl X → &amp;none</c>,
/// <c>&amp;lt X K… → &amp;kp K…</c>, and a hold-tap whose hold is a layer
/// behavior → <c>&amp;none</c>. A reference from a combo (binding or
/// <c>layers</c>) or a macro makes the delete impossible; the writer refuses
/// those deletes up front, so reaching one here is a programming error and
/// throws <see cref="InvalidOperationException"/>.
/// </para>
/// </summary>
public static class LayerReferenceRemapper
{
    private static readonly IReadOnlyList<string> EmptyParams = Array.Empty<string>();

    /// <summary>
    /// For each old layer index, its new index after <paramref name="edit"/>
    /// (<c>null</c> = deleted). An <see cref="LayerStructureEdit.Add"/> yields
    /// <paramref name="layerCount"/> entries — the new layer has no old index.
    /// Out-of-range edit indices throw <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public static IReadOnlyList<int?> IndexMap(int layerCount, LayerStructureEdit edit)
    {
        var map = new int?[layerCount];
        switch (edit)
        {
            case LayerStructureEdit.Move move:
                RequireIndex(move.From, layerCount, "Move.From");
                // The insert happens in the shortened list, so To also tops out
                // at layerCount - 1 (which appends).
                RequireIndex(move.To, layerCount, "Move.To");
                var order = new List<int>(layerCount);
                for (int i = 0; i < layerCount; i++)
                    if (i != move.From) order.Add(i);
                order.Insert(move.To, move.From);
                for (int pos = 0; pos < order.Count; pos++) map[order[pos]] = pos;
                return map;

            case LayerStructureEdit.Add add:
                if (add.AtIndex < 0 || add.AtIndex > layerCount)
                    throw new ArgumentOutOfRangeException(nameof(edit), add.AtIndex,
                        $"Add.AtIndex {add.AtIndex} is out of range 0…{layerCount}.");
                for (int i = 0; i < layerCount; i++)
                    map[i] = i < add.AtIndex ? i : i + 1;
                return map;

            case LayerStructureEdit.Delete delete:
                RequireIndex(delete.Index, layerCount, "Delete.Index");
                for (int i = 0; i < layerCount; i++)
                    map[i] = i < delete.Index ? i : i > delete.Index ? i - 1 : null;
                return map;

            default:
                throw new ArgumentException(
                    $"Unknown layer edit '{edit.GetType().Name}'.", nameof(edit));
        }
    }

    /// <summary>
    /// Returns a new <see cref="KeyboardConfig"/> with the edit applied and all
    /// layer references renumbered via <see cref="IndexMap"/>. <c>HoldTaps</c>
    /// and <c>KeyboardId</c> are carried over unchanged, and bindings that
    /// need no rewrite keep their instances. A <c>Move</c> with
    /// <c>From == To</c> returns <paramref name="config"/> itself.
    /// </summary>
    public static KeyboardConfig Apply(KeyboardConfig config, LayerStructureEdit edit)
    {
        int count = config.Layers.Count;

        // Index validity first (ArgumentOutOfRangeException), then the
        // semantic refusals.
        var map = IndexMap(count, edit);

        if (edit is LayerStructureEdit.Add add)
        {
            if (count == 0)
                throw new ArgumentException(
                    "Cannot add a layer to a keymap without layers.", nameof(edit));
            if (add.Bindings.Count != config.Layers[0].Bindings.Count)
                throw new ArgumentException(
                    $"Add.Bindings must hold one binding per key " +
                    $"({config.Layers[0].Bindings.Count.ToString(CultureInfo.InvariantCulture)}), got " +
                    $"{add.Bindings.Count.ToString(CultureInfo.InvariantCulture)}.",
                    nameof(edit));
        }
        else if (edit is LayerStructureEdit.Delete && count == 1)
        {
            throw new InvalidOperationException(
                "Cannot delete the only layer of a keymap.");
        }

        if (edit is LayerStructureEdit.Move { From: var from, To: var to } && from == to)
            return config;

        var holdTaps = HoldTapLookup(config);

        var layers = new List<Layer>(config.Layers);
        switch (edit)
        {
            case LayerStructureEdit.Add a:
                layers.Insert(a.AtIndex, new Layer(a.AtIndex, a.Name, a.Bindings));
                break;
            case LayerStructureEdit.Delete d:
                layers.RemoveAt(d.Index);
                break;
            case LayerStructureEdit.Move m:
                var moved = layers[m.From];
                layers.RemoveAt(m.From);
                layers.Insert(m.To, moved);
                break;
        }

        var newLayers = new List<Layer>(layers.Count);
        for (int pos = 0; pos < layers.Count; pos++)
        {
            var layer = layers[pos];
            // The added layer's bindings are in the old index space too — a
            // duplicated layer pointing at old index 3 must follow it when an
            // insert happens before it.
            var (bindings, bindingsChanged) =
                RewriteBindings(layer.Bindings, map, holdTaps, throwOnDeleted: false, owner: null);
            if (bindingsChanged || pos != layer.Index)
                layer = layer with { Index = pos, Bindings = bindings };
            newLayers.Add(layer);
        }

        var newCombos = new List<ZmkCombo>(config.Combos.Count);
        foreach (var combo in config.Combos)
        {
            var (binding, bindingChanged) = RewriteBinding(
                combo.Binding, map, holdTaps, throwOnDeleted: true, owner: $"combo '{combo.Name}'");

            var scopes = new List<int>(combo.Layers.Count);
            bool scopesChanged = false;
            foreach (int scope in combo.Layers)
            {
                // -1 means "all layers"; anything outside the old index space
                // is left untouched.
                if (scope < 0 || scope >= map.Count)
                {
                    scopes.Add(scope);
                    continue;
                }
                if (map[scope] is not int newScope)
                    throw new InvalidOperationException(
                        $"Cannot delete layer {scope.ToString(CultureInfo.InvariantCulture)}: " +
                        $"combo '{combo.Name}' references it.");
                if (newScope != scope) scopesChanged = true;
                scopes.Add(newScope);
            }

            newCombos.Add(bindingChanged || scopesChanged
                ? combo with { Binding = binding, Layers = scopes }
                : combo);
        }

        var newMacros = new List<ZmkMacro>(config.Macros.Count);
        foreach (var macro in config.Macros)
        {
            var (bindings, changed) = RewriteBindings(
                macro.Bindings, map, holdTaps, throwOnDeleted: true, owner: $"macro '{macro.Name}'");
            newMacros.Add(changed ? macro with { Bindings = bindings } : macro);
        }

        return config with { Layers = newLayers, Combos = newCombos, Macros = newMacros };
    }

    /// <summary>Applies <paramref name="edits"/> in order. An empty list
    /// returns <paramref name="config"/> itself.</summary>
    public static KeyboardConfig ApplyAll(KeyboardConfig config, IReadOnlyList<LayerStructureEdit> edits)
    {
        foreach (var edit in edits) config = Apply(config, edit);
        return config;
    }

    /// <summary>Names (ZmkCombo.Name) of the combos in <paramref name="config"/>
    /// that use layer <paramref name="layerIndex"/>: their binding targets it
    /// (the same layer-param rules <see cref="Apply"/> uses) or their Layers
    /// list holds it. Config order.</summary>
    public static IReadOnlyList<string> CombosUsingLayer(KeyboardConfig config, int layerIndex)
    {
        var holdTaps = HoldTapLookup(config);
        var names = new List<string>();
        foreach (var combo in config.Combos)
            if (ReferencedLayer(combo.Binding, config.Layers.Count, holdTaps) == layerIndex
                || combo.Layers.Contains(layerIndex))
                names.Add(combo.Name);
        return names;
    }

    /// <summary>
    /// Number of layer bindings outside the layer at <paramref name="deletedIndex"/>
    /// that target it — the delete dialog shows this before clearing them.
    /// </summary>
    public static int CountClearedBindings(KeyboardConfig config, int deletedIndex)
    {
        var holdTaps = HoldTapLookup(config);
        int cleared = 0;
        for (int i = 0; i < config.Layers.Count; i++)
        {
            if (i == deletedIndex) continue;
            foreach (var binding in config.Layers[i].Bindings)
                if (LayerBindingResolver.ResolveTargetLayer(
                        binding, holdTaps.GetValueOrDefault(binding.Behavior)) == deletedIndex)
                    cleared++;
        }
        return cleared;
    }

    private static (IReadOnlyList<KeyBinding> Bindings, bool Changed) RewriteBindings(
        IReadOnlyList<KeyBinding> bindings,
        IReadOnlyList<int?> map,
        Dictionary<string, HoldTap> holdTaps,
        bool throwOnDeleted,
        string? owner)
    {
        var rewritten = new List<KeyBinding>(bindings.Count);
        bool changed = false;
        foreach (var binding in bindings)
        {
            var (nb, wasRewritten) = RewriteBinding(binding, map, holdTaps, throwOnDeleted, owner);
            rewritten.Add(nb);
            changed |= wasRewritten;
        }

        return changed ? (rewritten, true) : (bindings, false);
    }

    private static (KeyBinding Binding, bool Changed) RewriteBinding(
        KeyBinding binding,
        IReadOnlyList<int?> map,
        Dictionary<string, HoldTap> holdTaps,
        bool throwOnDeleted,
        string? owner)
    {
        if (ReferencedLayer(binding, map.Count, holdTaps) is not int target)
            return (binding, false);

        if (map[target] is int mapped)
            return mapped == target
                ? (binding, false)
                : (binding with { Params = ReplaceFirstParam(binding, mapped) }, true);

        if (throwOnDeleted)
            throw new InvalidOperationException(
                $"Cannot delete layer {target.ToString(CultureInfo.InvariantCulture)}: " +
                $"{owner} references it.");

        return (ClearDeletedLayerReference(binding), true);
    }

    /// <summary>
    /// The layer index <paramref name="binding"/> references — layer behaviors
    /// and layer hold-taps, per
    /// <see cref="LayerBindingResolver.ResolveTargetLayer(KeyBinding, HoldTap?)"/>
    /// — or <see langword="null"/> when it references none or the target lies
    /// outside the <c>0 … layerCount − 1</c> index space. The one rule behind
    /// both <see cref="RewriteBinding"/> and <see cref="CombosUsingLayer"/>.
    /// </summary>
    private static int? ReferencedLayer(
        KeyBinding binding, int layerCount, Dictionary<string, HoldTap> holdTaps)
    {
        if (LayerBindingResolver.ResolveTargetLayer(
                binding, holdTaps.GetValueOrDefault(binding.Behavior)) is not int target
            || target < 0 || target >= layerCount)
            return null;
        return target;
    }

    /// <summary>
    /// Clears a layer binding that targeted the deleted layer: <c>&amp;lt X K…</c>
    /// keeps its tap side as <c>&amp;kp K…</c>, everything else (bare layer
    /// behaviors and layer-holding hold-taps alike) becomes <c>&amp;none</c>.
    /// Rewritten via <c>with</c> so <c>Decoration*</c> values survive.
    /// </summary>
    private static KeyBinding ClearDeletedLayerReference(KeyBinding binding)
        => binding.Behavior == "&lt"
            ? binding with { Behavior = "&kp", Params = binding.Params.Skip(1).ToList() }
            : binding with { Behavior = "&none", Params = EmptyParams };

    private static IReadOnlyList<string> ReplaceFirstParam(KeyBinding binding, int mapped)
    {
        var parameters = new List<string>(binding.Params);
        parameters[0] = mapped.ToString(CultureInfo.InvariantCulture);
        return parameters;
    }

    /// <summary>Hold-taps keyed by the behavior name a binding uses; a name
    /// defined twice keeps the last. The layer writer uses this same lookup so
    /// its delete pre-check, its cleared bindings and this model agree.</summary>
    internal static Dictionary<string, HoldTap> HoldTapLookup(KeyboardConfig config)
    {
        var lookup = new Dictionary<string, HoldTap>(StringComparer.Ordinal);
        foreach (var holdTap in config.HoldTaps) lookup[holdTap.Name] = holdTap;
        return lookup;
    }

    private static void RequireIndex(int index, int layerCount, string name)
    {
        if (index < 0 || index >= layerCount)
            throw new ArgumentOutOfRangeException("edit", index,
                $"{name} {index.ToString(CultureInfo.InvariantCulture)} is out of range " +
                $"0…{(layerCount - 1).ToString(CultureInfo.InvariantCulture)}.");
    }
}
