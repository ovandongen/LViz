using LViz.Core.Keymap.Editing;

namespace LViz.Core.Models;

/// <summary>
/// A ZMK combo: pressing two or more keys at the same key positions fires
/// the wrapped binding instead of the underlying per-key bindings.
/// </summary>
/// <param name="Name">User-given combo name from the Moergo editor (e.g. "F12").</param>
/// <param name="Description">Optional free-text description from the editor.</param>
/// <param name="KeyPositions">Key indices that must all be pressed for the combo to fire.</param>
/// <param name="Layers">
/// Layer indices on which this combo is active. A single value of <c>-1</c> means
/// "all layers" (the default in the Moergo editor).
/// </param>
/// <param name="Binding">The behavior the combo fires.</param>
public sealed record ZmkCombo(
    string Name,
    string Description,
    IReadOnlyList<int> KeyPositions,
    IReadOnlyList<int> Layers,
    KeyBinding Binding)
{
    /// <summary>The devicetree node name (<c>combo_esc</c>) — the combo's
    /// identity in the file. Null for combos not read from a .keymap (MoErgo JSON).</summary>
    public string? NodeName { get; init; }

    /// <summary><c>timeout-ms</c>, or null when the file leaves it out (ZMK's default 50 applies).</summary>
    public int? TimeoutMs { get; init; }

    /// <summary><c>require-prior-idle-ms</c>, or null when left out (off).</summary>
    public int? RequirePriorIdleMs { get; init; }

    /// <summary>True when the node has <c>slow-release</c>.</summary>
    public bool SlowRelease { get; init; }

    /// <summary>True when this combo is active on every layer (i.e. <c>Layers == [-1]</c>).</summary>
    public bool AppliesToAllLayers => Layers.Count > 0 && Layers.Contains(-1);

    /// <summary>True when this combo is active on the given layer index.</summary>
    public bool AppliesToLayer(int layerIndex) => AppliesToAllLayers || Layers.Contains(layerIndex);

    /// <summary>Same combo as far as the file is concerned: same NodeName and
    /// Name (Ordinal), the same key positions as a set, the same layer scope
    /// (all-layers equals all-layers, otherwise the same set of layers), the
    /// same binding (behavior + ordinal params), and equal TimeoutMs,
    /// RequirePriorIdleMs and SlowRelease. Description and decorations are ignored.</summary>
    public bool IsSameAs(ZmkCombo other)
    {
        if (!string.Equals(NodeName, other.NodeName, StringComparison.Ordinal)) return false;
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal)) return false;
        if (!SameSet(KeyPositions, other.KeyPositions)) return false;
        if (!SameLayerScope(Layers, other.Layers)) return false;
        if (!KeymapEditSession.IsSameBinding(Binding, other.Binding)) return false;
        return TimeoutMs == other.TimeoutMs
            && RequirePriorIdleMs == other.RequirePriorIdleMs
            && SlowRelease == other.SlowRelease;
    }

    private static bool SameSet(IReadOnlyList<int> a, IReadOnlyList<int> b) =>
        a.Count == b.Count && new HashSet<int>(a).SetEquals(b);

    /// <summary>Layer scope as the file means it: a lone -1 (or an empty
    /// list, which no writer emits) is "all layers".</summary>
    private static bool SameLayerScope(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        bool allA = a.Count == 0 || a.Contains(-1);
        bool allB = b.Count == 0 || b.Contains(-1);
        return allA || allB ? allA == allB : SameSet(a, b);
    }
}
