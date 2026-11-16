using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Stateless access to the per-key label overrides in
/// <see cref="UserSettings.KeyLabelOverrides"/> — a three-level dictionary
/// profile id → layer index → key index. <see cref="With"/> answers the same
/// question the settings hold with a new dictionary: it adds or replaces an
/// entry, or removes it (a null or <see cref="KeyLabelOverride.IsEmpty"/>
/// value) while pruning the layer and profile dictionaries an empty removal
/// leaves behind, so "absent" and "all defaults" stay indistinguishable.
/// The passed dictionary is never mutated — a settings collection is only
/// ever replaced wholesale inside <c>ISettingsService.Update</c>.
/// </summary>
public static class KeyLabelOverrides
{
    public static KeyLabelOverride? Get(UserSettings settings, string profileId, int layerIndex, int keyIndex)
    {
        if (settings.KeyLabelOverrides.TryGetValue(profileId, out var perLayer)
            && perLayer.TryGetValue(layerIndex, out var perKey)
            && perKey.TryGetValue(keyIndex, out var value))
            return value;
        return null;
    }

    public static Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> With(
        Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> current,
        string profileId, int layerIndex, int keyIndex, KeyLabelOverride? value)
    {
        var result = new Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>>(current);

        if (value is null || value.IsEmpty)
        {
            if (result.TryGetValue(profileId, out var perLayer)
                && perLayer.TryGetValue(layerIndex, out var perKey))
            {
                var layers = new Dictionary<int, Dictionary<int, KeyLabelOverride>>(perLayer);
                var keys = new Dictionary<int, KeyLabelOverride>(perKey);
                keys.Remove(keyIndex);
                if (keys.Count > 0) layers[layerIndex] = keys;
                else layers.Remove(layerIndex);
                if (layers.Count > 0) result[profileId] = layers;
                else result.Remove(profileId);
            }
            return result;
        }

        var byLayer = result.TryGetValue(profileId, out var existing)
            ? new Dictionary<int, Dictionary<int, KeyLabelOverride>>(existing)
            : new Dictionary<int, Dictionary<int, KeyLabelOverride>>();
        var byKey = byLayer.TryGetValue(layerIndex, out var existingKeys)
            ? new Dictionary<int, KeyLabelOverride>(existingKeys)
            : new Dictionary<int, KeyLabelOverride>();
        byKey[keyIndex] = value;
        byLayer[layerIndex] = byKey;
        result[profileId] = byLayer;
        return result;
    }
}
