using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Editing;

/// <summary>
/// One key the save rewrites: which cell, which layer it is in (named as the
/// editor shows it), and the bindings on both sides.
/// </summary>
/// <param name="LayerIndex">Position of the layer in the saved keymap.</param>
/// <param name="LayerName">The layer's <c>display-name</c> after the save.</param>
/// <param name="KeyIndex">Position of the key inside that layer's bindings.</param>
/// <param name="Old">The binding as it stands in the <c>.keymap</c> text.</param>
/// <param name="New">The binding the save writes.</param>
public sealed record KeymapBindingChange(int LayerIndex, string LayerName, int KeyIndex, KeyBinding Old, KeyBinding New);

/// <summary>
/// One layer whose <c>display-name</c> the save rewrites.
/// </summary>
/// <param name="LayerIndex">Position of the layer in the saved keymap.</param>
/// <param name="OldName">The name in the <c>.keymap</c> text.</param>
/// <param name="NewName">The name the save writes.</param>
public sealed record KeymapLayerRenameChange(int LayerIndex, string OldName, string NewName);

/// <summary>
/// What saving an edit session amounts to: the complete new <c>.keymap</c> text,
/// plus the human-readable lists of everything that changes in it — structural
/// edits, key changes and renames. When the user presses Save the review dialog
/// shows these lists and a GitHub save commits with
/// <see cref="KeymapCommitMessage.Build"/> over the same plan.
/// </summary>
/// <param name="NewSource">The text to write to the file.</param>
/// <param name="StructureEdits">The layer adds/deletes/moves replayed into the
/// text, oldest first — the session's <see cref="KeymapEditSession.PendingStructureEdits"/>.</param>
/// <param name="BindingChanges">The keys the save rewrites, ordered by layer then key.</param>
/// <param name="Renames">The layers the save renames, ordered by layer.</param>
public sealed record KeymapSavePlan(
    string NewSource,
    IReadOnlyList<LayerStructureEdit> StructureEdits,
    IReadOnlyList<KeymapBindingChange> BindingChanges,
    IReadOnlyList<KeymapLayerRenameChange> Renames)
{
    /// <summary>Nothing to write and nothing to report.</summary>
    public bool IsEmpty => StructureEdits.Count == 0 && BindingChanges.Count == 0 && Renames.Count == 0;

    /// <summary>
    /// Turn <paramref name="session"/> into the text to save and the lists of
    /// what changed, by replaying the structural edits into
    /// <paramref name="baselineSource"/> first and writing the binding/rename
    /// diff against the result on top:
    /// <list type="number">
    /// <item>apply <see cref="KeymapEditSession.PendingStructureEdits"/> to the text,</item>
    /// <item>re-parse that text — it is what the file would look like after the
    /// structural phase, references and all,</item>
    /// <item>diff it against <see cref="KeymapEditSession.Current"/> and patch
    /// the remaining bindings and names into the text.</item>
    /// </list>
    /// <para>
    /// Keys a structural edit only renumbered (<c>&amp;mo 2</c> → <c>&amp;mo 3</c>)
    /// are already in the structural result, so they never appear as binding
    /// changes — the renumbering is visible through the structure edit itself.
    /// </para>
    /// <para>
    /// Callers catch <see cref="ZmkKeymapParseException"/> (a writer or the
    /// loader refused the text) and <see cref="ArgumentException"/> (the session
    /// does not match the baseline text; <see cref="ArgumentOutOfRangeException"/>
    /// derives from it). Everything propagates from here unchanged. Nothing in
    /// this method does I/O — the text comes in as a string and goes out as one.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="baselineSource"/>
    /// or <paramref name="session"/> is null.</exception>
    /// <exception cref="ZmkKeymapParseException">The loader or a writer refused
    /// the text.</exception>
    /// <exception cref="ArgumentException">The replayed text and the session's
    /// <see cref="KeymapEditSession.Current"/> cannot be aligned — different
    /// layer counts or binding counts.</exception>
    public static KeymapSavePlan Build(string baselineSource, KeymapEditSession session)
    {
        ArgumentNullException.ThrowIfNull(baselineSource);
        ArgumentNullException.ThrowIfNull(session);

        var structure = session.PendingStructureEdits;
        var afterStructure = KeymapLayerWriter.ApplyAll(baselineSource, structure);
        var parsed = ZmkKeymapLoader.LoadFromText(afterStructure, "writer");
        var diff = KeymapEditDiff.Compute(parsed, session.Current);

        var newSource = diff.Bindings.Count == 0 && diff.Renames.Count == 0
            ? afterStructure
            : KeymapWriter.Apply(afterStructure, diff.Bindings, diff.Renames);

        var bindingChanges = new List<KeymapBindingChange>(diff.Bindings.Count);
        foreach (var edit in diff.Bindings)
            bindingChanges.Add(new KeymapBindingChange(
                edit.LayerIndex,
                session.Current.Layers[edit.LayerIndex].Name,
                edit.KeyIndex,
                parsed.Layers[edit.LayerIndex].Bindings[edit.KeyIndex],
                edit.Binding));

        var renames = new List<KeymapLayerRenameChange>(diff.Renames.Count);
        foreach (var rename in diff.Renames)
            renames.Add(new KeymapLayerRenameChange(
                rename.LayerIndex,
                parsed.Layers[rename.LayerIndex].Name,
                rename.Name));

        return new KeymapSavePlan(newSource, structure, bindingChanges, renames);
    }
}
