using LViz.Core.Models;

namespace LViz.Core.Keymap.Editing;

/// <summary>
/// The English commit message for a GitHub keymap save: a summary line counting
/// each kind of change, then one detail line per change. English only on
/// purpose — the commit lands in the user's repository, not in their UI, and is
/// deliberately not translated.
/// </summary>
public static class KeymapCommitMessage
{
    /// <summary>At most this many detail lines; a longer list is cut and ends
    /// with "… and N more".</summary>
    public const int MaxDetailLines = 20;

    /// <summary>
    /// Build the message for <paramref name="plan"/>: the
    /// <c>LViz: …</c> summary line, a blank line, then the detail lines —
    /// structure edits, renames, combo changes, key changes, in that order.
    /// Joined with <c>\n</c>, no trailing newline. An empty plan is exactly
    /// <c>"LViz: no changes"</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    public static string Build(KeymapSavePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var adds = plan.StructureEdits.OfType<LayerStructureEdit.Add>().Count();
        var deletes = plan.StructureEdits.OfType<LayerStructureEdit.Delete>().Count();
        var moves = plan.StructureEdits.OfType<LayerStructureEdit.Move>().Count();
        var comboAdds = plan.ComboChanges.Count(c => c.Edit is ComboEdit.Add);
        var comboEdits = plan.ComboChanges.Count(c => c.Edit is ComboEdit.Change);
        var comboDeletes = plan.ComboChanges.Count(c => c.Edit is ComboEdit.Delete);

        var summary = new List<string>();
        Add(summary, plan.BindingChanges.Count, "edit", "key");
        Add(summary, plan.Renames.Count, "rename", "layer");
        Add(summary, adds, "add", "layer");
        Add(summary, deletes, "delete", "layer");
        Add(summary, moves, "move", "layer");
        Add(summary, comboAdds, "add", "combo");
        Add(summary, comboEdits, "edit", "combo");
        Add(summary, comboDeletes, "delete", "combo");

        if (summary.Count == 0) return "LViz: no changes";

        var details = new List<string>();

        foreach (var edit in plan.StructureEdits)
            details.Add(edit switch
            {
                LayerStructureEdit.Add add => $"Add layer \"{add.Name}\" at {add.AtIndex}",
                LayerStructureEdit.Delete delete => $"Delete layer {delete.Index}",
                LayerStructureEdit.Move move => $"Move layer {move.From} to {move.To}",
                _ => throw new ArgumentException(
                    $"unsupported layer structure edit {edit.GetType().Name}.", nameof(plan)),
            });

        foreach (var rename in plan.Renames)
            details.Add($"Rename layer {rename.LayerIndex}: \"{rename.OldName}\" \u2192 \"{rename.NewName}\"");

        foreach (var change in plan.ComboChanges)
            switch (change.Edit)
            {
                case ComboEdit.Add add:
                    details.Add(
                        $"Add combo {add.Combo.NodeName} ({ComboKeys(add.Combo)}): {add.Combo.Binding.Display}");
                    break;

                case ComboEdit.Change:
                    if (change.Old is null || change.New is null)
                        throw new ArgumentException(
                            "combo change without both an old and a new combo.", nameof(plan));
                    var names = string.Equals(change.Old.NodeName, change.New.NodeName, StringComparison.Ordinal)
                        ? change.Old.NodeName
                        : $"{change.Old.NodeName} \u2192 {change.New.NodeName}";
                    var binding = string.Equals(
                        change.Old.Binding.Display, change.New.Binding.Display, StringComparison.Ordinal)
                        ? change.Old.Binding.Display
                        : $"{change.Old.Binding.Display} \u2192 {change.New.Binding.Display}";
                    details.Add($"Edit combo {names} ({ComboKeys(change.New)}): {binding}");
                    break;

                case ComboEdit.Delete delete:
                    if (change.Old is null)
                        throw new ArgumentException(
                            "combo delete without the deleted combo.", nameof(plan));
                    details.Add($"Delete combo {delete.NodeName} ({ComboKeys(change.Old)})");
                    break;

                default:
                    throw new ArgumentException(
                        $"unsupported combo edit {change.Edit.GetType().Name}.", nameof(plan));
            }

        foreach (var change in plan.BindingChanges)
            details.Add($"{change.LayerName} key {change.KeyIndex}: {change.Old.Display} \u2192 {change.New.Display}");

        if (details.Count > MaxDetailLines)
        {
            int rest = details.Count - MaxDetailLines;
            details.RemoveRange(MaxDetailLines, rest);
            details.Add($"\u2026 and {rest} more");
        }

        return string.Join("\n", [$"LViz: {string.Join(", ", summary)}", "", .. details]);
    }

    /// <summary>Adds "<c>verb N noun</c>" for a non-zero count, singular for 1,
    /// plural otherwise.</summary>
    private static void Add(List<string> summary, int count, string verb, string noun)
    {
        if (count == 0) return;
        summary.Add($"{verb} {count} {(count == 1 ? noun : noun + "s")}");
    }

    /// <summary>The combo's key positions in its own order, joined with
    /// <c>+</c> — <c>(10+11)</c>.</summary>
    private static string ComboKeys(ZmkCombo combo) => string.Join("+", combo.KeyPositions);
}
