namespace LViz.Core.Settings;

/// <summary>
/// Pure helpers for the per-keyboard-profile dictionaries on
/// <see cref="UserSettings"/> (outer key = profile id). Every writer returns a
/// fresh outer dictionary — deep-cloning the inner dictionary for the nested
/// overloads — so the result drops straight into a <c>record with { ... }</c>
/// without aliasing the loaded settings. Inputs are never mutated.
///
/// <para>Set and Remove are kept separate (rather than a single null-sentinel
/// API) so value-type entries (bool / int / enum) and reference-type entries
/// share one code path with no generic constraint split and no casts.</para>
/// </summary>
public static class PerProfile
{
    /// <summary>Reads <paramref name="profileId"/>'s entry, or
    /// <paramref name="fallback"/> when the profile has none.</summary>
    public static TValue GetForProfile<TValue>(
        this IReadOnlyDictionary<string, TValue> source,
        string profileId,
        TValue fallback)
        => source.TryGetValue(profileId, out var v) ? v : fallback;

    /// <summary>Returns a clone with <c>[profileId] = value</c>.</summary>
    public static Dictionary<string, TValue> Set<TValue>(
        IReadOnlyDictionary<string, TValue> source, string profileId, TValue value)
    {
        var clone = new Dictionary<string, TValue>(source);
        clone[profileId] = value;
        return clone;
    }

    /// <summary>Returns a clone without <paramref name="profileId"/>.</summary>
    public static Dictionary<string, TValue> Remove<TValue>(
        IReadOnlyDictionary<string, TValue> source, string profileId)
    {
        var clone = new Dictionary<string, TValue>(source);
        clone.Remove(profileId);
        return clone;
    }

    /// <summary>Sets the entry when <paramref name="keep"/> is true, otherwise
    /// removes it — collapses the <c>if (cond) d[id]=v; else d.Remove(id);</c>
    /// shape.</summary>
    public static Dictionary<string, TValue> SetOrRemove<TValue>(
        IReadOnlyDictionary<string, TValue> source, string profileId, bool keep, TValue value)
        => keep ? Set(source, profileId, value) : Remove(source, profileId);

    /// <summary>Nested: sets <c>[profileId][innerKey] = value</c>, deep-cloning
    /// every profile's inner dictionary so siblings are untouched.</summary>
    public static Dictionary<string, Dictionary<TKey, TValue>> SetInner<TKey, TValue>(
        IReadOnlyDictionary<string, Dictionary<TKey, TValue>> source,
        string profileId, TKey innerKey, TValue value)
        where TKey : notnull
    {
        var clone = DeepClone(source);
        if (!clone.TryGetValue(profileId, out var inner))
            clone[profileId] = inner = new Dictionary<TKey, TValue>();
        inner[innerKey] = value;
        return clone;
    }

    /// <summary>Nested: removes <c>[profileId][innerKey]</c>, pruning the
    /// profile bucket when its inner dictionary becomes empty.</summary>
    public static Dictionary<string, Dictionary<TKey, TValue>> RemoveInner<TKey, TValue>(
        IReadOnlyDictionary<string, Dictionary<TKey, TValue>> source,
        string profileId, TKey innerKey)
        where TKey : notnull
    {
        var clone = DeepClone(source);
        if (clone.TryGetValue(profileId, out var inner))
        {
            inner.Remove(innerKey);
            if (inner.Count == 0) clone.Remove(profileId);
        }
        return clone;
    }

    private static Dictionary<string, Dictionary<TKey, TValue>> DeepClone<TKey, TValue>(
        IReadOnlyDictionary<string, Dictionary<TKey, TValue>> source)
        where TKey : notnull
    {
        var clone = new Dictionary<string, Dictionary<TKey, TValue>>(source.Count);
        foreach (var (pid, inner) in source)
            clone[pid] = new Dictionary<TKey, TValue>(inner);
        return clone;
    }
}
