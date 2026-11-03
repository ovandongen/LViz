namespace LViz.Core.Keymap.Writer;

/// <summary>
/// What stands between a layer and being deleted. Kinds and names only — the
/// UI owns the sentences, so a new language or a reworded dialog never touches
/// Core.
/// </summary>
public enum LayerDeleteBlockerKind
{
    /// <summary>The keymap's only layer: deleting it would leave no keymap.</summary>
    LastLayer,

    /// <summary>A combo fires a binding that targets the layer, or is scoped to
    /// it. Dropping the layer would silently widen or mis-aim the combo, and
    /// there is no combo editor to fix that afterwards.</summary>
    Combo,

    /// <summary>A <c>zmk,conditional-layers</c> node reaches the layer through
    /// <c>if-layers</c> / <c>then-layer</c>.</summary>
    ConditionalLayer,

    /// <summary>The layer's own <c>#define</c> name is used somewhere outside the
    /// layer blocks — a hold-tap built with <c>ZMK_TD_LAYER</c>, an input
    /// listener's <c>layers</c> — where rewriting it is out of reach.</summary>
    DefineUsage,

    /// <summary>Anything else that names the layer: a numeric reference in a
    /// body the layer writers do not rewrite.</summary>
    Other,
}

/// <summary>
/// One reason a layer cannot be deleted.
/// </summary>
/// <param name="Name">The combo name, the conditional-layer node name, the
/// define name, or the name of the node the reference sits in (<c>""</c> when it
/// sits outside any named node). For <see cref="LayerDeleteBlockerKind.LastLayer"/>
/// it is the surviving layer's name.</param>
/// <param name="Line">1-based line of the offending text, or <c>0</c> for
/// <see cref="LayerDeleteBlockerKind.LastLayer"/> and
/// <see cref="LayerDeleteBlockerKind.Combo"/>, which have no single line: a
/// combo's <c>layers</c> list and its binding may sit far apart, and the combo
/// is the unit the user has to fix.</param>
public sealed record LayerDeleteBlocker(LayerDeleteBlockerKind Kind, string Name, int Line);

/// <summary>
/// What <see cref="KeymapLayerWriter.CheckDelete"/> answers: whether the delete
/// is possible and what it would do to the keys.
/// </summary>
/// <param name="Blockers">Every reason the delete is refused, empty when it is
/// possible. Ordered last layer, then combos in config order, then numeric
/// references in file order, then define usages in file order.</param>
/// <param name="ClearedBindings">How many layer bindings point at the layer and
/// would be cleared (<c>&amp;mo … → &amp;none</c>, <c>&amp;lt X K → &amp;kp K</c>)
/// if it were deleted.</param>
public sealed record LayerDeleteCheck(
    IReadOnlyList<LayerDeleteBlocker> Blockers,
    int ClearedBindings)
{
    /// <summary>True when nothing stands in the way, i.e.
    /// <see cref="KeymapLayerWriter.Apply"/> would perform the delete.</summary>
    public bool CanDelete => Blockers.Count == 0;
}
