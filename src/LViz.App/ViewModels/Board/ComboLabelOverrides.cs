using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Stateless access to the per-combo label overrides in
/// <see cref="UserSettings.ComboLabelOverrides"/> — a two-level dictionary
/// profile id → key-positions key (combos aren't layer-scoped, so the inner
/// key is the combo's sorted-positions string instead of a (layer, key)
/// pair). <see cref="With"/> answers with a new dictionary: it adds or
/// replaces an entry, or removes it (a null or
/// <see cref="ComboLabelOverride.IsEmpty"/> value) while pruning the profile
/// dictionary an empty removal leaves behind. The passed dictionary is never
/// mutated — a settings collection is only ever replaced wholesale inside
/// <c>ISettingsService.Update</c>.
/// </summary>
public static class ComboLabelOverrides
{
    public static ComboLabelOverride? Get(UserSettings settings, string profileId, string keyPositionsKey)
    {
        if (settings.ComboLabelOverrides.TryGetValue(profileId, out var perCombo)
            && perCombo.TryGetValue(keyPositionsKey, out var value))
            return value;
        return null;
    }

    public static Dictionary<string, Dictionary<string, ComboLabelOverride>> With(
        Dictionary<string, Dictionary<string, ComboLabelOverride>> current,
        string profileId, string keyPositionsKey, ComboLabelOverride? value)
    {
        var result = new Dictionary<string, Dictionary<string, ComboLabelOverride>>(current);

        if (value is null || value.IsEmpty)
        {
            if (result.TryGetValue(profileId, out var perCombo))
            {
                var combos = new Dictionary<string, ComboLabelOverride>(perCombo);
                combos.Remove(keyPositionsKey);
                if (combos.Count > 0) result[profileId] = combos;
                else result.Remove(profileId);
            }
            return result;
        }

        var byCombo = result.TryGetValue(profileId, out var existing)
            ? new Dictionary<string, ComboLabelOverride>(existing)
            : new Dictionary<string, ComboLabelOverride>();
        byCombo[keyPositionsKey] = value;
        result[profileId] = byCombo;
        return result;
    }

    /// <summary>
    /// Canonical key-positions identifier for a combo: sorted ascending,
    /// comma-joined. Used as the dictionary key in <see cref="UserSettings.ComboLabelOverrides"/>
    /// so the same combo identity round-trips across keymap reloads regardless
    /// of how the user listed positions in the keymap source.
    /// </summary>
    public static string KeyPositionsToKey(IEnumerable<int> positions)
        => string.Join(',', positions.OrderBy(p => p));
}
