using LViz.Core.Models;

namespace LViz.Core.Keymap.Editing;

/// <summary>
/// One change to a keymap's combo list. Shared contract between the edit
/// session (records and undoes these) and the combo writer (applies them to
/// the <c>.keymap</c> text). A combo is named by its devicetree node name —
/// the identity the file gives it — never by its position in the parsed list.
/// A list of edits is applied in order, each against the previous result.
/// </summary>
public abstract record ComboEdit
{
    private ComboEdit() { }

    /// <summary>Append a new combo; <c>Combo.NodeName</c> is its node name.</summary>
    public sealed record Add(ZmkCombo Combo) : ComboEdit;

    /// <summary>Rewrite the combo whose node is <paramref name="NodeName"/> in
    /// the text; <paramref name="Combo"/> holds every new value
    /// (<c>Combo.NodeName</c> may differ: a rename).</summary>
    public sealed record Change(string NodeName, ZmkCombo Combo) : ComboEdit;

    /// <summary>Remove the combo whose node is <paramref name="NodeName"/>.</summary>
    public sealed record Delete(string NodeName) : ComboEdit;
}
