using LViz.Core.Settings;

namespace LViz.App.ViewModels;

public interface IKeyLabelOverrideService
{
    KeyLabelOverride? Get(string profileId, int layerIndex, int keyIndex);
    void SetOverrides(Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> overrides);
    void Set(string profileId, int layerIndex, int keyIndex, KeyLabelOverride? value);
    Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> Snapshot();
}

public sealed class KeyLabelOverrideService : IKeyLabelOverrideService
{
    private Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> _overrides = new();

    public KeyLabelOverride? Get(string profileId, int layerIndex, int keyIndex)
    {
        if (_overrides.TryGetValue(profileId, out var perLayer)
            && perLayer.TryGetValue(layerIndex, out var perKey)
            && perKey.TryGetValue(keyIndex, out var v))
            return v;
        return null;
    }

    public void SetOverrides(Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> overrides)
    {
        var copy = new Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>>();
        foreach (var (profileId, perLayer) in overrides)
        {
            var layerCopy = new Dictionary<int, Dictionary<int, KeyLabelOverride>>();
            foreach (var (layerIdx, perKey) in perLayer)
                layerCopy[layerIdx] = new Dictionary<int, KeyLabelOverride>(perKey);
            if (layerCopy.Count > 0)
                copy[profileId] = layerCopy;
        }
        _overrides = copy;
    }

    public void Set(string profileId, int layerIndex, int keyIndex, KeyLabelOverride? value)
    {
        if (value is null || value.IsEmpty)
        {
            if (_overrides.TryGetValue(profileId, out var perLayer)
                && perLayer.TryGetValue(layerIndex, out var perKey))
            {
                perKey.Remove(keyIndex);
                if (perKey.Count == 0) perLayer.Remove(layerIndex);
                if (perLayer.Count == 0) _overrides.Remove(profileId);
            }
            return;
        }

        if (!_overrides.TryGetValue(profileId, out var byLayer))
            _overrides[profileId] = byLayer = new Dictionary<int, Dictionary<int, KeyLabelOverride>>();
        if (!byLayer.TryGetValue(layerIndex, out var byKey))
            byLayer[layerIndex] = byKey = new Dictionary<int, KeyLabelOverride>();
        byKey[keyIndex] = value;
    }

    public Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> Snapshot()
    {
        var copy = new Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>>();
        foreach (var (profileId, perLayer) in _overrides)
        {
            var layerCopy = new Dictionary<int, Dictionary<int, KeyLabelOverride>>();
            foreach (var (layerIdx, perKey) in perLayer)
                layerCopy[layerIdx] = new Dictionary<int, KeyLabelOverride>(perKey);
            copy[profileId] = layerCopy;
        }
        return copy;
    }
}

public static class KeyLabelOverrides
{
    internal static readonly KeyLabelOverrideService Service = new();

    public static KeyLabelOverride? Get(string profileId, int layerIndex, int keyIndex)
        => Service.Get(profileId, layerIndex, keyIndex);

    public static void SetOverrides(Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> overrides)
        => Service.SetOverrides(overrides);

    public static void Set(string profileId, int layerIndex, int keyIndex, KeyLabelOverride? value)
        => Service.Set(profileId, layerIndex, keyIndex, value);

    public static Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> Snapshot()
        => Service.Snapshot();
}
