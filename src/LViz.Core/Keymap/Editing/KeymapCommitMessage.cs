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
    /// structure edits, renames, key changes, in that order. Joined with
    /// <c>\n</c>, no trailing newline. An empty plan is exactly
    /// <c>"LViz: no changes"</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    public static string Build(KeymapSavePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var adds = plan.StructureEdits.OfType<LayerStructureEdit.Add>().Count();
        var deletes = plan.StructureEdits.OfType<LayerStructureEdit.Delete>().Count();
        var moves = plan.StructureEdits.OfType<LayerStructureEdit.Move>().Count();

        var summary = new List<string>();
        Add(summary, plan.BindingChanges.Count, "edit", "key");
        Add(summary, plan.Renames.Count, "rename", "layer");
        Add(summary, adds, "add", "layer");
        Add(summary, deletes, "delete", "layer");
        Add(summary, moves, "move", "layer");

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
}
