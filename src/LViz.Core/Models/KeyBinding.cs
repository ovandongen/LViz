namespace LViz.Core.Models;

/// <summary>
/// A single key binding in a ZMK layer, flattened from the nested Moergo JSON format.
/// <para>
/// Examples:
/// <list type="bullet">
/// <item><c>&amp;kp A</c> → Behavior="&amp;kp", Params=["A"]</item>
/// <item><c>&amp;mo 1</c> → Behavior="&amp;mo", Params=["1"]</item>
/// <item><c>&amp;HRM_left_hand_v1_TKZ LSHFT A</c> → Behavior="&amp;HRM_left_hand_v1_TKZ", Params=["LSHFT", "A"]</item>
/// <item><c>&amp;trans</c> → Behavior="&amp;trans", Params=[]</item>
/// </list>
/// </para>
/// </summary>
public sealed record KeyBinding(string Behavior, IReadOnlyList<string> Params)
{
    public static readonly KeyBinding Transparent = new("&trans", Array.Empty<string>());
    public static readonly KeyBinding None = new("&none", Array.Empty<string>());

    /// <summary>
    /// User-authored label from the Moergo editor's <c>decoration.label</c>
    /// field. When non-empty this wins over any label derived from the
    /// behavior + params.
    /// </summary>
    public string? DecorationLabel { get; init; }

    /// <summary>
    /// User-authored background color (hex, e.g. <c>#fffec9</c>) from
    /// <c>decoration.background</c>. Overrides any automatic per-key fill
    /// (including layer-tint colors).
    /// </summary>
    public string? DecorationBackground { get; init; }

    /// <summary>
    /// User-authored icon identifier from <c>decoration.icon</c>. Moergo emits
    /// Font Awesome class names (e.g. <c>fa-paste</c>). When present, rendered
    /// in place of the derived glyph label.
    /// </summary>
    public string? DecorationIcon { get; init; }

    /// <summary>Formatted for display: "&amp;kp A" or just "&amp;trans".</summary>
    public string Display =>
        Params.Count == 0 ? Behavior : $"{Behavior} {string.Join(' ', Params)}";
}
