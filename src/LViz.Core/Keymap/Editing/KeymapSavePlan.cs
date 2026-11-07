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

/// <summary>One combo the save adds, rewrites or removes. Old is the combo
/// as the text has it (null for an add), New what the save writes (null for
/// a delete).</summary>
public sealed record KeymapComboChange(ComboEdit Edit, ZmkCombo? Old, ZmkCombo? New);

/// <summary>
/// What saving an edit session amounts to: the complete new <c>.keymap</c> text,
/// plus the human-readable lists of everything that changes in it — structural
/// edits, key changes, renames and combo changes. When the user presses Save the
/// review dialog shows these lists and a GitHub save commits with
/// <see cref="KeymapCommitMessage.Build"/> over the same plan.
/// </summary>
/// <param name="NewSource">The text to write to the file.</param>
/// <param name="StructureEdits">The layer adds/deletes/moves replayed into the
/// text, in replay order: the session's net <see cref="KeymapEditSession.PendingStructureEdits"/>.</param>
/// <param name="BindingChanges">The keys the save rewrites, ordered by layer then key.</param>
/// <param name="Renames">The layers the save renames, ordered by layer.</param>
public sealed record KeymapSavePlan(
    string NewSource,
    IReadOnlyList<LayerStructureEdit> StructureEdits,
    IReadOnlyList<KeymapBindingChange> BindingChanges,
    IReadOnlyList<KeymapLayerRenameChange> Renames)
{
    /// <summary>The combos the save adds, rewrites or removes, in the order
    /// the combo writer applies them.</summary>
    public IReadOnlyList<KeymapComboChange> ComboChanges { get; init; } = [];

    /// <summary>Nothing to write and nothing to report.</summary>
    public bool IsEmpty =>
        StructureEdits.Count == 0 && BindingChanges.Count == 0
        && Renames.Count == 0 && ComboChanges.Count == 0;

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
    /// the remaining bindings and names into the text,</item>
    /// <item>match <see cref="KeymapEditSession.PendingComboEdits"/> against the
    /// re-parsed combo list, drop the changes the structural step already
    /// renumbered in the text, and write the rest with
    /// <see cref="KeymapComboWriter.Apply"/>.</item>
    /// </list>
    /// <para>
    /// Keys a structural edit only renumbered (<c>&amp;mo 2</c> → <c>&amp;mo 3</c>)
    /// are already in the structural result, so they never appear as binding
    /// changes — the renumbering is visible through the structure edit itself.
    /// Combos renumbered the same way drop out of <see cref="ComboChanges"/>
    /// the same manner.
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

        var comboEdits = new List<ComboEdit>();
        var comboChanges = new List<KeymapComboChange>();
        foreach (var edit in session.PendingComboEdits)
            switch (edit)
            {
                case ComboEdit.Add add:
                    comboEdits.Add(add);
                    comboChanges.Add(new KeymapComboChange(add, null, add.Combo));
                    break;

                case ComboEdit.Change change:
                    var target = FindParsedCombo(parsed, change.NodeName);
                    // A structural edit already renumbered the layer refs in
                    // this combo's text — writing it again would be a no-op.
                    if (!change.Combo.IsSameAs(target))
                    {
                        comboEdits.Add(change);
                        comboChanges.Add(new KeymapComboChange(change, target, change.Combo));
                    }
                    break;

                case ComboEdit.Delete delete:
                    var removed = FindParsedCombo(parsed, delete.NodeName);
                    comboEdits.Add(delete);
                    comboChanges.Add(new KeymapComboChange(delete, removed, null));
                    break;

                default:
                    throw new ArgumentException(
                        $"unknown combo edit '{edit.GetType().Name}'.", nameof(session));
            }

        if (comboEdits.Count > 0)
            newSource = KeymapComboWriter.Apply(newSource, WithSwapSafeRenames(comboEdits, parsed));

        return new KeymapSavePlan(newSource, structure, bindingChanges, renames)
        {
            ComboChanges = comboChanges,
        };
    }

    /// <summary>
    /// The writer's edit list for <paramref name="edits"/> (deletes, changes,
    /// adds), safe for renames that trade names. Each rename is checked against
    /// the text as it stands when it runs, so swapping two combos' names
    /// (<c>A → B</c>, <c>B → A</c>) would hit the name the other still holds.
    /// When any rename aims at a node name the text still has after the
    /// deletes, every renamed combo first moves to a fresh temporary node name,
    /// and its real change then runs from there; with all old names gone, the
    /// final names can't collide in any order. Otherwise the list is returned
    /// as is.
    /// </summary>
    private static IReadOnlyList<ComboEdit> WithSwapSafeRenames(
        IReadOnlyList<ComboEdit> edits, KeyboardConfig parsed)
    {
        var deleted = edits.OfType<ComboEdit.Delete>().Select(d => d.NodeName).ToHashSet(StringComparer.Ordinal);
        var inText = parsed.Combos
            .Select(c => c.NodeName)
            .OfType<string>()
            .Where(n => !deleted.Contains(n))
            .ToHashSet(StringComparer.Ordinal);

        static bool Renames(ComboEdit.Change change) =>
            change.Combo.NodeName is { } next && !string.Equals(next, change.NodeName, StringComparison.Ordinal);

        var renames = edits.OfType<ComboEdit.Change>().Where(Renames).ToList();
        if (!renames.Any(r => inText.Contains(r.Combo.NodeName!))) return edits;

        // Temporary names avoid every name in the text and every final name.
        var taken = new HashSet<string>(inText, StringComparer.Ordinal);
        foreach (var edit in edits)
            if (edit is ComboEdit.Add { Combo.NodeName: { } added }) taken.Add(added);
        foreach (var rename in renames) taken.Add(rename.Combo.NodeName!);

        var temporary = new Dictionary<ComboEdit.Change, string>();
        int n = 1;
        foreach (var rename in renames)
        {
            string name;
            do name = $"lviz_rename_{n++}"; while (!taken.Add(name));
            temporary[rename] = name;
        }

        var result = new List<ComboEdit>(edits.Count + renames.Count);
        result.AddRange(edits.OfType<ComboEdit.Delete>());
        foreach (var rename in renames)
        {
            // Only the name moves here: every other property stays as the text has it.
            var asInText = FindParsedCombo(parsed, rename.NodeName);
            result.Add(new ComboEdit.Change(rename.NodeName, asInText with { NodeName = temporary[rename] }));
        }

        foreach (var edit in edits)
        {
            switch (edit)
            {
                case ComboEdit.Change change when temporary.TryGetValue(change, out var from):
                    result.Add(new ComboEdit.Change(from, change.Combo));
                    break;
                case ComboEdit.Change or ComboEdit.Add:
                    result.Add(edit);
                    break;
            }
        }

        return result;
    }

    /// <summary>The combo of <paramref name="parsed"/> whose devicetree node is
    /// <paramref name="nodeName"/>.</summary>
    /// <exception cref="ArgumentException">No combo of the replayed text has
    /// that node — the session was built over a different keymap.</exception>
    private static ZmkCombo FindParsedCombo(KeyboardConfig parsed, string nodeName) =>
        parsed.Combos.FirstOrDefault(c => string.Equals(c.NodeName, nodeName, StringComparison.Ordinal))
        ?? throw new ArgumentException(
            $"combo edit targets node '{nodeName}', which the keymap text does not have — "
            + "the edit session and the file disagree.",
            nameof(parsed));
}
