using LViz.Core.Models;

namespace LViz.Core.Keymap;

/// <summary>
/// A parsed keymap ready to render: the <see cref="KeyboardConfig"/>, its
/// <see cref="LayerBindingResolver"/>, and the hold-tap / macro lookups every
/// per-key resolution needs. Immutable after construction, so it's safe to
/// read from any thread once handed off.
///
/// <para>The single home for the "what actually fires at (layer, key)"
/// kernel — <see cref="Resolve"/> — which the view model previously inlined
/// (and duplicated) at every render and every edit-dialog open.</para>
/// </summary>
public sealed class LoadedKeymap
{
    private readonly IReadOnlyDictionary<string, HoldTap> _holdTapsByName;

    public LoadedKeymap(KeyboardConfig config)
    {
        Config = config;
        Resolver = new LayerBindingResolver(config);
        // StringComparer.Ordinal mirrors the comparer the VM used when it built
        // these per call — behaviors are case-sensitive ZMK phandles.
        _holdTapsByName = config.HoldTaps.ToDictionary(h => h.Name, StringComparer.Ordinal);
        MacrosByName = config.Macros.ToDictionary(m => m.Name, StringComparer.Ordinal);
    }

    public KeyboardConfig Config { get; }
    public LayerBindingResolver Resolver { get; }
    public IReadOnlyDictionary<string, ZmkMacro> MacrosByName { get; }

    public int LayerCount => Config.LayerCount;
    public IReadOnlyList<Layer> Layers => Config.Layers;
    public IReadOnlyList<ZmkCombo> Combos => Config.Combos;

    /// <summary>
    /// Resolves the binding that actually fires at (<paramref name="layerIndex"/>,
    /// <paramref name="keyIndex"/>) — walking <c>&amp;trans</c> fall-through —
    /// plus the hold-tap, target layer, and target-layer name a label needs.
    /// </summary>
    public ResolvedBinding Resolve(int layerIndex, int keyIndex)
    {
        var binding = Resolver.ResolveEffectiveBinding(layerIndex, keyIndex);
        _holdTapsByName.TryGetValue(binding.Behavior, out var holdTap);
        var targetLayer = LayerBindingResolver.ResolveTargetLayer(binding, holdTap);
        var targetLayerName = targetLayer is int tl && tl >= 0 && tl < Config.Layers.Count
            ? Config.Layers[tl].Name
            : null;
        return new ResolvedBinding(binding, holdTap, targetLayer, targetLayerName);
    }

    /// <summary>
    /// Classifies the *visible* binding at (<paramref name="layer"/>,
    /// <paramref name="key"/>) as <see cref="TransparentBindingKind.Transparent"/>
    /// for <c>&amp;trans</c>, <see cref="TransparentBindingKind.Empty"/> for
    /// <c>&amp;none</c>, or null for anything else (including out-of-range
    /// indices). No fall-through walk — the exit gesture fires on the literal
    /// binding only.
    /// </summary>
    public TransparentBindingKind? ClassifyBinding(int layer, int key)
    {
        if (layer < 0 || layer >= Config.Layers.Count) return null;
        var bindings = Config.Layers[layer].Bindings;
        if (key < 0 || key >= bindings.Count) return null;
        return bindings[key].Behavior switch
        {
            "&trans" => TransparentBindingKind.Transparent,
            "&none" => TransparentBindingKind.Empty,
            _ => null,
        };
    }
}

/// <summary>
/// The per-key resolution result: the effective binding plus the hold-tap and
/// layer-target context a formatter needs to render it.
/// </summary>
public readonly record struct ResolvedBinding(
    KeyBinding Binding,
    HoldTap? HoldTap,
    int? TargetLayer,
    string? TargetLayerName);
