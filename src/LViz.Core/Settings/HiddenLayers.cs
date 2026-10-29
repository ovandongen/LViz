namespace LViz.Core.Settings;

/// <summary>
/// Pure bookkeeping for the per-profile "hidden layer buttons" selection
/// persisted in <see cref="UserSettings.HiddenLayers"/>. Both helpers are
/// copy-on-write: they return a new dictionary with fresh lists and never
/// mutate their input, so callers can hand them live settings dictionaries
/// directly.
/// </summary>
public static class HiddenLayers
{
    /// <summary>
    /// True when <paramref name="profileId"/> hides
    /// <paramref name="layerIndex"/>. An unknown profile hides nothing, and
    /// layer 0 (base) is never hidden.
    /// </summary>
    public static bool IsHidden(IReadOnlyDictionary<string, List<int>> source, string profileId, int layerIndex)
    {
        if (layerIndex <= 0) return false;
        return source.TryGetValue(profileId, out var hidden) && hidden.Contains(layerIndex);
    }

    /// <summary>
    /// Returns a deep clone with <paramref name="layerIndex"/> added
    /// (<paramref name="hidden"/> = true) or removed (false) for
    /// <paramref name="profileId"/>. A layer index &lt;= 0 can never be
    /// hidden, so it yields an unchanged clone; so does adding an
    /// already-present or removing an absent index. The modified profile's
    /// list stays sorted ascending and duplicate-free, and a profile whose
    /// list becomes empty is dropped from the result entirely. Other
    /// profiles are carried over untouched.
    /// </summary>
    public static Dictionary<string, List<int>> With(
        IReadOnlyDictionary<string, List<int>> source, string profileId, int layerIndex, bool hidden)
    {
        var result = new Dictionary<string, List<int>>(source.Count);
        foreach (var entry in source)
            result[entry.Key] = new List<int>(entry.Value);

        if (layerIndex <= 0) return result;

        if (hidden)
        {
            var list = result.GetValueOrDefault(profileId) ?? new List<int>();
            result[profileId] = list.Append(layerIndex).Distinct().Order().ToList();
        }
        else if (result.TryGetValue(profileId, out var existing))
        {
            existing.Remove(layerIndex);
            if (existing.Count == 0) result.Remove(profileId);
        }

        return result;
    }
}
