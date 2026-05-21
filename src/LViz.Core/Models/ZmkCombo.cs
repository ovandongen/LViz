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
    /// <summary>True when this combo is active on every layer (i.e. <c>Layers == [-1]</c>).</summary>
    public bool AppliesToAllLayers => Layers.Count > 0 && Layers.Contains(-1);

    /// <summary>True when this combo is active on the given layer index.</summary>
    public bool AppliesToLayer(int layerIndex) => AppliesToAllLayers || Layers.Contains(layerIndex);
}
