using LViz.Core.Settings;

namespace LViz.App.ViewModels;

public interface IComboLabelOverrideService
{
    ComboLabelOverride? Get(string profileId, string keyPositionsKey);
    void SetOverrides(Dictionary<string, Dictionary<string, ComboLabelOverride>> overrides);
    void Set(string profileId, string keyPositionsKey, ComboLabelOverride? value);
    Dictionary<string, Dictionary<string, ComboLabelOverride>> Snapshot();
}

/// <summary>
/// In-memory store of per-combo label overrides, mirroring
/// <see cref="KeyLabelOverrideService"/>. Combos aren't layer-scoped, so the
/// inner dictionary key is the combo's sorted-positions string instead of a
/// (layer, key) pair.
/// </summary>
public sealed class ComboLabelOverrideService : IComboLabelOverrideService
{
    private Dictionary<string, Dictionary<string, ComboLabelOverride>> _overrides = new();

    public ComboLabelOverride? Get(string profileId, string keyPositionsKey)
    {
        if (_overrides.TryGetValue(profileId, out var perCombo)
            && perCombo.TryGetValue(keyPositionsKey, out var v))
            return v;
        return null;
    }

    public void SetOverrides(Dictionary<string, Dictionary<string, ComboLabelOverride>> overrides)
    {
        var copy = new Dictionary<string, Dictionary<string, ComboLabelOverride>>();
        foreach (var (profileId, perCombo) in overrides)
        {
            if (perCombo.Count > 0)
                copy[profileId] = new Dictionary<string, ComboLabelOverride>(perCombo);
        }
        _overrides = copy;
    }

    public void Set(string profileId, string keyPositionsKey, ComboLabelOverride? value)
    {
        if (value is null || value.IsEmpty)
        {
            if (_overrides.TryGetValue(profileId, out var perCombo))
            {
                perCombo.Remove(keyPositionsKey);
                if (perCombo.Count == 0) _overrides.Remove(profileId);
            }
            return;
        }

        if (!_overrides.TryGetValue(profileId, out var byCombo))
            _overrides[profileId] = byCombo = new Dictionary<string, ComboLabelOverride>();
        byCombo[keyPositionsKey] = value;
    }

    public Dictionary<string, Dictionary<string, ComboLabelOverride>> Snapshot()
    {
        var copy = new Dictionary<string, Dictionary<string, ComboLabelOverride>>();
        foreach (var (profileId, perCombo) in _overrides)
            copy[profileId] = new Dictionary<string, ComboLabelOverride>(perCombo);
        return copy;
    }
}

public static class ComboLabelOverrides
{
    internal static readonly ComboLabelOverrideService Service = new();

    public static ComboLabelOverride? Get(string profileId, string keyPositionsKey)
        => Service.Get(profileId, keyPositionsKey);

    public static void SetOverrides(Dictionary<string, Dictionary<string, ComboLabelOverride>> overrides)
        => Service.SetOverrides(overrides);

    public static void Set(string profileId, string keyPositionsKey, ComboLabelOverride? value)
        => Service.Set(profileId, keyPositionsKey, value);

    public static Dictionary<string, Dictionary<string, ComboLabelOverride>> Snapshot()
        => Service.Snapshot();

    /// <summary>
    /// Canonical key-positions identifier for a combo: sorted ascending,
    /// comma-joined. Used as the dictionary key in <see cref="UserSettings.ComboLabelOverrides"/>
    /// so the same combo identity round-trips across keymap reloads regardless
    /// of how the user listed positions in the keymap source.
    /// </summary>
    public static string KeyPositionsToKey(IEnumerable<int> positions)
        => string.Join(',', positions.OrderBy(p => p));
}
