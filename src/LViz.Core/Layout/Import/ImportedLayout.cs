using LViz.Core.Layout.Dtsi;

namespace LViz.Core.Layout.Import;

/// <summary>Which board-file format produced an <see cref="ImportedLayout"/>.</summary>
public enum LayoutSource
{
    UserLayout,
    PhysicalLayout,
    InfoJson,
    MatrixTransform,

    /// <summary>Built by <see cref="GridLayoutGenerator"/> from the wizard's questions.</summary>
    Generated,
}

/// <summary>
/// One rough layout imported from a board file — the shared result contract
/// of the three importers (physical-layout dtsi, keymap-editor/QMK <c>info.json</c>,
/// matrix transform) and of a <c>&lt;id&gt;.layout.json</c> the wizard saved
/// earlier. Keys are in ZMK units (centi-u / centi-degrees, see
/// <see cref="DtsiKeyPhysical"/>) in keymap binding order: the order the
/// source file declares them. The layout wizard refines what the file could
/// not know; <see cref="Warnings"/> codes anything the importer had to assume
/// or could not carry over — the App formats them into localized text.
/// </summary>
/// <param name="Hands">
/// One hand per key in <see cref="Keys"/> order, or null. Only a saved user
/// layout carries hands (the wizard asked for them); every importer leaves it
/// null and the wizard splits its preview without that knowledge.
/// </param>
/// <param name="SavedId">
/// The id of a saved user layout, or null for every importer. The wizard saves
/// such a candidate back under this id instead of making a new one, so
/// rescanning a config folder doesn't pile up <c>&lt;id&gt;-2</c> copies.
/// </param>
public sealed record ImportedLayout(
    string Name,
    LayoutSource Source,
    IReadOnlyList<DtsiKeyPhysical> Keys,
    IReadOnlyList<LayoutImportWarning> Warnings,
    IReadOnlyList<Hand>? Hands = null,
    string? SavedId = null);

/// <summary>Identifies a layout import warning; the App formats the text from <c>.resx</c>.</summary>
public enum LayoutWarningCode { MatrixTransformRough, MatrixTransformGrid, RotationWithoutPivot }

/// <summary>One import warning as a code plus the values its text needs —
/// <c>KeyIndex</c> for <see cref="LayoutWarningCode.RotationWithoutPivot"/>.</summary>
public sealed record LayoutImportWarning(LayoutWarningCode Code, int? KeyIndex = null);
