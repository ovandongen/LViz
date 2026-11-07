using LViz.Core.Models;

namespace LViz.Core.Keymap.Editing;

/// <summary>
/// The net layer edits between a baseline layer list and the current one,
/// derived purely from layer ids: baseline layer <c>i</c> has id <c>i</c>, and
/// an id at or above the baseline count is a layer added later. Nothing here
/// knows about the undo history, so reversed steps leave no trace.
/// </summary>
internal static class LayerIdDiff
{
    /// <summary>
    /// The adds, deletes and moves that turn the baseline list into
    /// <paramref name="currentIds"/>, in replay order: deletes from the highest
    /// baseline position down, then moves (the longest run already in baseline
    /// order stays put), then adds at their final positions, lowest first.
    /// Empty when the ids are back to the baseline list.
    /// </summary>
    /// <param name="baselineCount">Number of baseline layers (ids <c>0 … n−1</c>).</param>
    /// <param name="currentIds">Layer id at each current position.</param>
    /// <param name="makeAdd">Builds the add edit for an added layer from its
    /// current position and id (name and bindings come from the caller).</param>
    public static IReadOnlyList<LayerStructureEdit> NetEdits(
        int baselineCount,
        IReadOnlyList<int> currentIds,
        Func<int, int, LayerStructureEdit.Add> makeAdd)
    {
        if (MatchesBaseline(baselineCount, currentIds)) return [];

        var edits = new List<LayerStructureEdit>();
        var work = Enumerable.Range(0, baselineCount).ToList();
        var kept = currentIds.Where(id => id < baselineCount).ToList();
        var keptSet = kept.ToHashSet();

        for (int i = work.Count - 1; i >= 0; i--)
        {
            if (keptSet.Contains(work[i])) continue;
            edits.Add(new LayerStructureEdit.Delete(i));
            work.RemoveAt(i);
        }

        // Each moved layer lands right after its predecessor in the current
        // order. Processing them in that order keeps everything placed so far,
        // plus the run that stays, in current order.
        var stays = LongestIncreasingRun(kept);
        for (int p = 0; p < kept.Count; p++)
        {
            var id = kept[p];
            if (stays.Contains(id)) continue;
            var from = work.IndexOf(id);
            work.RemoveAt(from);
            var to = p == 0 ? 0 : work.IndexOf(kept[p - 1]) + 1;
            work.Insert(to, id);
            if (from != to) edits.Add(new LayerStructureEdit.Move(from, to));
        }

        for (int p = 0; p < currentIds.Count; p++)
        {
            if (currentIds[p] < baselineCount) continue;
            edits.Add(makeAdd(p, currentIds[p]));
        }

        return edits;
    }

    /// <summary>
    /// One entry per baseline layer: its position in <paramref name="currentIds"/>,
    /// or <see langword="null"/> when it is gone. A new array on every call.
    /// </summary>
    public static int?[] BaselineMap(int baselineCount, IReadOnlyList<int> currentIds)
    {
        var map = new int?[baselineCount];
        for (int position = 0; position < currentIds.Count; position++)
        {
            var id = currentIds[position];
            if (id < baselineCount) map[id] = position;
        }
        return map;
    }

    /// <summary>Are the ids still the baseline's, in the baseline's order?</summary>
    public static bool MatchesBaseline(int baselineCount, IReadOnlyList<int> currentIds)
    {
        if (currentIds.Count != baselineCount) return false;
        for (int i = 0; i < currentIds.Count; i++)
            if (currentIds[i] != i) return false;
        return true;
    }

    /// <summary>The ids on one longest strictly increasing subsequence of
    /// <paramref name="ids"/>: the layers a reorder can leave where they are.</summary>
    internal static HashSet<int> LongestIncreasingRun(IReadOnlyList<int> ids)
    {
        var length = new int[ids.Count];
        var previous = new int[ids.Count];
        var best = -1;
        for (int i = 0; i < ids.Count; i++)
        {
            length[i] = 1;
            previous[i] = -1;
            for (int j = 0; j < i; j++)
                if (ids[j] < ids[i] && length[j] + 1 > length[i])
                {
                    length[i] = length[j] + 1;
                    previous[i] = j;
                }
            if (best < 0 || length[i] > length[best]) best = i;
        }

        var run = new HashSet<int>();
        for (var i = best; i >= 0; i = previous[i]) run.Add(ids[i]);
        return run;
    }
}
