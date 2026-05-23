using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Service contract for per-keyboard, per-layer, per-key label overrides.
/// Mirrors <see cref="ILayerColorService"/>'s split: code with DI access
/// should resolve this interface; XAML converters and the rendering view
/// model use the static <see cref="KeyLabelOverrides"/> facade for an
/// allocation-free read path.
/// </summary>
public interface IKeyLabelOverrideService
{
    KeyLabelOverride? Get(string profileId, int layerIndex, int keyIndex);
    void SetOverrides(Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> overrides);
    void Set(string profileId, int layerIndex, int keyIndex, KeyLabelOverride? value);
    Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> Snapshot();
}

/// <summary>
/// Default <see cref="IKeyLabelOverrideService"/> backing the static
/// <see cref="KeyLabelOverrides"/> facade. Registered as the singleton in
/// <c>AppServices</c> so DI consumers see the same state the static does.
/// </summary>
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
        if (overrides is not null)
        {
            foreach (var (profileId, perLayer) in overrides)
            {
                if (perLayer is null) continue;
                var layerCopy = new Dictionary<int, Dictionary<int, KeyLabelOverride>>();
                foreach (var (layerIdx, perKey) in perLayer)
                {
                    if (perKey is null) continue;
                    layerCopy[layerIdx] = new Dictionary<int, KeyLabelOverride>(perKey);
                }
                if (layerCopy.Count > 0)
                    copy[profileId] = layerCopy;
            }
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

/// <summary>
/// Static facade over the shared <see cref="KeyLabelOverrideService"/>.
/// <see cref="KeyViewModel.ApplyBinding"/> reads through this for every key
/// on every layer switch, so the static keeps the read path allocation-free
/// while DI consumers and tests get the interface.
/// </summary>
public static class KeyLabelOverrides
{
    /// <summary>Single instance shared with DI. Mutations and reads land in the same state.</summary>
    internal static readonly KeyLabelOverrideService Service = new();

    /// <summary>Override for the given (profile, layer, key) slot, or <c>null</c> when none is set.</summary>
    public static KeyLabelOverride? Get(string profileId, int layerIndex, int keyIndex)
        => Service.Get(profileId, layerIndex, keyIndex);

    /// <summary>
    /// Replaces the override map wholesale. Called once at startup from
    /// <c>MainWindowViewModel</c> with the persisted dictionary.
    /// </summary>
    public static void SetOverrides(Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> overrides)
        => Service.SetOverrides(overrides);

    /// <summary>
    /// Sets or clears a single override. Pass <c>null</c> or a
    /// <see cref="KeyLabelOverride.IsEmpty"/> value to remove the entry and
    /// revert to formatter-computed labels.
    /// </summary>
    public static void Set(string profileId, int layerIndex, int keyIndex, KeyLabelOverride? value)
        => Service.Set(profileId, layerIndex, keyIndex, value);

    /// <summary>Deep copy of the current overrides — used when serializing back to <c>UserSettings</c>.</summary>
    public static Dictionary<string, Dictionary<int, Dictionary<int, KeyLabelOverride>>> Snapshot()
        => Service.Snapshot();
}
