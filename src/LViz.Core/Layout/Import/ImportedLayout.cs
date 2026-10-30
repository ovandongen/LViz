using LViz.Core.Layout.Dtsi;

namespace LViz.Core.Layout.Import;

/// <summary>Which board-file format produced an <see cref="ImportedLayout"/>.</summary>
public enum LayoutSource
{
    PhysicalLayout,
    InfoJson,
    MatrixTransform,
}

/// <summary>
/// One rough layout imported from a board file — the shared result contract
/// of the three importers (physical-layout dtsi, keymap-editor/QMK <c>info.json</c>,
/// matrix transform). Keys are in ZMK units (centi-u / centi-degrees, see
/// <see cref="DtsiKeyPhysical"/>) in keymap binding order: the order the
/// source file declares them. The layout wizard refines what the file could
/// not know; <see cref="Warnings"/> carries notes about anything the importer
/// had to assume or could not carry over.
/// </summary>
public sealed record ImportedLayout(
    string Name,
    LayoutSource Source,
    IReadOnlyList<DtsiKeyPhysical> Keys,
    IReadOnlyList<string> Warnings);
