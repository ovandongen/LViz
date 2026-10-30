namespace LViz.Core.Layout.Dtsi;

/// <summary>
/// One <c>&amp;key_physical_attrs</c> entry from a ZMK physical-layouts dtsi.
/// All values are in the dtsi's native units: centi-keyboard-units for the
/// geometry (100 = 1u) and centi-degrees for rotation. Negative rotations
/// appear as <c>(-3700)</c> in the source and arrive here as ordinary negatives.
/// Conversion to pixel coords happens at the profile boundary so this stays
/// format-pure.
/// </summary>
public sealed record DtsiKeyPhysical(
    int Width,
    int Height,
    int X,
    int Y,
    int Rotation,
    int Rx,
    int Ry);

/// <summary>One parsed <c>zmk,physical-layout</c> node: its devicetree node
/// name (label prefix stripped), its optional <c>display-name</c>, and the
/// keys list in dtsi declaration order.
/// Both shipped boards' local dtsi files are arranged so this order matches
/// Moergo's JSON <c>bindings[]</c> index. The upstream Go60 dtsi at
/// <c>~/Desktop/Projects/Moergo/dtsi/go60.dtsi</c> disagrees for the row-5 +
/// thumb block — a resync must re-apply our local reorder. See CLAUDE.md.</summary>
public sealed class DtsiPhysicalLayout
{
    /// <summary>Node name as written, without a <c>label:</c> prefix
    /// (e.g. <c>physical_layout_0</c>).</summary>
    public string NodeName { get; }

    /// <summary>The node's <c>display-name</c> property, or <c>null</c> when absent.</summary>
    public string? DisplayName { get; }

    public IReadOnlyList<DtsiKeyPhysical> Keys { get; }

    public DtsiPhysicalLayout(
        string nodeName,
        string? displayName,
        IReadOnlyList<DtsiKeyPhysical> keys)
    {
        NodeName = nodeName;
        DisplayName = displayName;
        Keys = keys;
    }
}
