namespace LViz.Core.Settings;

/// <summary>
/// User-authored label override for a single (profile, layer, key) slot.
/// </summary>
public sealed record KeyLabelOverride
{
    public string MainLabel { get; init; } = "";
    public string Subscript { get; init; } = "";
    public string TopLeftBadge { get; init; } = "";
    public string Icon { get; init; } = "";
    public double? FontSize { get; init; }
    public bool Bold { get; init; }

    public bool IsEmpty => this == new KeyLabelOverride();
}
