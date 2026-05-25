namespace LViz.Core.Settings;

/// <summary>
/// User-authored label override for a single combo, identified by the sorted
/// tuple of physical key positions it fires from (e.g. <c>"12,13"</c>). Combos
/// aren't layer-scoped — they have one global identity — so the override is
/// keyed by (profileId, key-positions string) rather than carrying a layer
/// index.
/// </summary>
public sealed record ComboLabelOverride
{
    public string MainLabel { get; init; } = "";

    public bool IsEmpty => this == new ComboLabelOverride();
}
