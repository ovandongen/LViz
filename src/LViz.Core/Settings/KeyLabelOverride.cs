namespace LViz.Core.Settings;

/// <summary>
/// User-authored label override for a single (profile, layer, key) slot.
/// Present entries fully replace the formatter-computed text on that key —
/// every field below substitutes its corresponding piece of the rendered
/// output. An override that collapses to <see cref="IsEmpty"/> is pruned by
/// the persistence layer so absent and "all defaults" are indistinguishable
/// on disk.
/// </summary>
public sealed record KeyLabelOverride
{
    /// <summary>Centre glyph. Empty = render nothing in the main slot.</summary>
    public string MainLabel { get; init; } = "";

    /// <summary>Smaller text below the main label.</summary>
    public string Subscript { get; init; } = "";

    /// <summary>Italic corner tag (e.g. "To Layer", "Hold-Tap"). Empty = no badge.</summary>
    public string TopLeftBadge { get; init; } = "";

    /// <summary>
    /// Font Awesome icon name as understood by <c>&lt;i:Icon Value="…"/&gt;</c>
    /// from projektanker.icons.avalonia — e.g. <c>fa-coffee</c>. Empty = no
    /// icon. Selected via <c>IconPickerDialog</c> driven by
    /// <c>FontAwesomeCatalog</c>.
    /// </summary>
    public string Icon { get; init; } = "";

    /// <summary>
    /// Explicit point size for the main label. <c>null</c> = keep
    /// <c>KeyViewModel</c>'s length-based auto-scaling. Set when a longer
    /// label needs to shrink further or a short one should grow.
    /// </summary>
    public double? FontSize { get; init; }

    /// <summary>Upgrades the main label from <c>SemiBold</c> to <c>Bold</c>.</summary>
    public bool Bold { get; init; }

    /// <summary>
    /// True when every field carries its default value — used by the
    /// persistence layer to prune the entry rather than storing a no-op
    /// override.
    /// </summary>
    public bool IsEmpty =>
        string.IsNullOrEmpty(MainLabel)
        && string.IsNullOrEmpty(Subscript)
        && string.IsNullOrEmpty(TopLeftBadge)
        && string.IsNullOrEmpty(Icon)
        && FontSize is null
        && !Bold;
}
