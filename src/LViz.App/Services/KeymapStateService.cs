using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;

namespace LViz.App.Services;

/// <summary>
/// Owns the currently-loaded keymap and the parse lifecycle. Pull-based: the
/// view model calls <see cref="Load"/>, inspects the result, and drives
/// rendering / status / profile-auto-switch from it. This service never
/// touches UI, Avalonia, or the view model's observable collections.
/// </summary>
public interface IKeymapStateService
{
    /// <summary>The loaded keymap, or null when nothing is loaded.</summary>
    LoadedKeymap? Current { get; }

    bool HasLayout { get; }

    /// <summary>Path of the keymap currently loaded, or null.</summary>
    string? LoadedPath { get; }

    /// <summary>Last failed-load reason ("path: Type: message"), or null. Kept
    /// across <see cref="Clear"/> for diagnostics.</summary>
    string? LastLoadError { get; }

    /// <summary>
    /// Classifies the visible binding at (<paramref name="layer"/>,
    /// <paramref name="key"/>) for the transparent/empty-key exit gesture.
    /// Reads <see cref="Current"/> at call time, so the push coordinator can
    /// bind this once and layout swaps need no re-wiring. Null when no layout
    /// is loaded.
    /// </summary>
    TransparentBindingKind? ClassifyBinding(int layer, int key);

    /// <summary>
    /// Parses <paramref name="path"/> and, on success, atomically commits it as
    /// <see cref="Current"/>. All parse / IO failures are caught and returned
    /// as <see cref="KeymapLoadResult.Failure"/> — they never escape, so the
    /// caller's post-load work (profile switch, render) only runs on a
    /// validated keymap.
    /// </summary>
    KeymapLoadResult Load(string path, string profileId);

    /// <summary>Drops the loaded keymap (e.g. a profile switch whose layout no
    /// longer fits). Leaves <see cref="LastLoadError"/> intact.</summary>
    void Clear();
}

/// <inheritdoc cref="IKeymapStateService"/>
public sealed class KeymapStateService : IKeymapStateService
{
    public LoadedKeymap? Current { get; private set; }
    public bool HasLayout => Current is not null;
    public string? LoadedPath { get; private set; }
    public string? LastLoadError { get; private set; }

    public TransparentBindingKind? ClassifyBinding(int layer, int key)
        => Current?.ClassifyBinding(layer, key);

    public KeymapLoadResult Load(string path, string profileId)
    {
        try
        {
            var config = ZmkKeymapLoader.Load(path, profileId);
            var keymap = new LoadedKeymap(config);
            Current = keymap;
            LoadedPath = path;
            LastLoadError = null;
            var firstLayerBindingCount = config.LayerCount > 0 ? config.Layers[0].Bindings.Count : 0;
            return new KeymapLoadResult.Success(keymap, path, config.LayerCount, firstLayerBindingCount);
        }
        catch (Exception ex)
        {
            LastLoadError = $"{path}: {ex.GetType().Name}: {ex.Message}";
            return new KeymapLoadResult.Failure(path, ex);
        }
    }

    public void Clear()
    {
        Current = null;
        LoadedPath = null;
    }
}

/// <summary>Outcome of <see cref="IKeymapStateService.Load"/>.</summary>
public abstract record KeymapLoadResult
{
    public sealed record Success(
        LoadedKeymap Keymap,
        string Path,
        int LayerCount,
        int FirstLayerBindingCount) : KeymapLoadResult;

    public sealed record Failure(string Path, Exception Error) : KeymapLoadResult;
}
