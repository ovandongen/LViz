using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Editing;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// The layer tab strip: the tabs, their colours, the per-profile hidden
/// layers and the edit-mode layer actions. The main VM feeds it only through
/// <see cref="SetEditing"/>, <see cref="Rebuild"/> and <see cref="SetActive"/>
/// (which tab is highlighted) and routes <see cref="LayerPicked"/>; the shown
/// layer itself (<c>ActiveLayerIndex</c>) stays on the main VM, which paints
/// the keys. The active board comes in as a <c>Func&lt;string&gt;</c>.
/// </summary>
public sealed partial class LayerTabsViewModel : ObservableObject
{
    private readonly Func<string> _profileIdSource;
    private readonly ISettingsService _settings;
    private KeymapEditViewModel? _edit;

    public LayerTabsViewModel(Func<string> profileId, ISettingsService settings)
    {
        _profileIdSource = profileId;
        _settings = settings;
    }

    private string ProfileId => _profileIdSource();

    /// <summary>The tabs. Items are replaced only by <see cref="Rebuild"/>:
    /// everything else updates a tab in place (a rebuild steals focus).</summary>
    public ObservableCollection<LayerViewModel> Layers { get; } = new();

    /// <summary>The open keymap edit, or null. Drives the in-strip edit commands.</summary>
    public KeymapEditViewModel? Edit => _edit;

    public bool IsEditing => _edit is not null;

    /// <summary>The user clicked tab <c>index</c>. The main VM decides what
    /// shows it (the edit while editing, else a plain repaint).</summary>
    public event Action<int>? LayerPicked;

    /// <summary>A tab colour changed: the board repaints (&amp;lt; / &amp;mo keys
    /// resolve other layers' colours) and the active tint is re-read.</summary>
    public event Action? ColorsChanged;

    /// <summary>The main VM follows its edit here; the next <see cref="Rebuild"/> shows it.</summary>
    public void SetEditing(KeymapEditViewModel? edit)
    {
        _edit = edit;
        OnPropertyChanged(nameof(Edit));
        OnPropertyChanged(nameof(IsEditing));
    }

    /// <summary>
    /// Tab colour for the layer at <paramref name="index"/>. While a keymap
    /// edit is open the colour follows the <em>layer</em> rather than the
    /// position: a layer from the baseline keeps the colour its baseline index
    /// owns (surviving a move), and one the edit added gets the palette
    /// default for its position. Outside an edit this is the plain per-profile
    /// palette lookup.
    /// </summary>
    public string ColorFor(int index)
    {
        if (_edit is { } edit)
        {
            return edit.Session.BaselineIndexOf(index) is int baseline
                ? LayerColorPalette.GetColor(ProfileId, baseline)
                : LayerColorPalette.GetDefaultColor(index);
        }
        return LayerColorPalette.GetColor(ProfileId, index);
    }

    /// <summary>
    /// Replaces the tabs. While editing they show the edited keymap, hidden
    /// layers included, and carry the per-layer actions; otherwise they show
    /// <paramref name="loaded"/> with the persisted hidden flags.
    /// </summary>
    public void Rebuild(LoadedKeymap? loaded)
    {
        Layers.Clear();
        var edit = _edit;
        var km = edit?.Keymap ?? loaded;
        if (km is null) return;
        var layerCount = km.Layers.Count;
        var hidden = edit is null ? _settings.Load().HiddenLayers : null;
        foreach (var layer in km.Layers)
        {
            var tab = new LayerViewModel(
                layer.Index,
                $"{layer.Index} : {layer.Name}",
                ColorFor(layer.Index),
                index => LayerPicked?.Invoke(index),
                edit is { } e
                    ? new LayerTabActions(
                        i => _ = e.RenameLayerAsync(i),
                        i => e.DuplicateLayer(i),
                        (i, delta) => e.MoveLayer(i, i + delta),
                        i => _ = e.DeleteLayerAsync(i),
                        layerCount)
                    : null);
            if (hidden is not null)
                tab.IsShown = !HiddenLayers.IsHidden(hidden, ProfileId, layer.Index);
            Layers.Add(tab);
        }
    }

    /// <summary>Drops every tab (a board with no keymap loaded).</summary>
    public void Clear() => Layers.Clear();

    /// <summary>Highlights the tab for the layer being shown.</summary>
    public void SetActive(int index)
    {
        foreach (var tab in Layers)
            tab.IsSelected = tab.Index == index;
    }

    /// <summary>True while <see cref="Layers"/> still mirrors the open edit —
    /// same count, and every tab's caption and colour equal what a rebuild from
    /// the edit would produce at its position. False (rebuild) only after a
    /// structural change; a plain key edit never moves either.</summary>
    public bool MatchesEdit()
    {
        if (_edit is not { } edit) return true;
        var layers = edit.Keymap.Layers;
        if (Layers.Count != layers.Count) return false;
        for (var i = 0; i < layers.Count; i++)
        {
            var tab = Layers[i];
            if (tab.DisplayName != $"{i} : {layers[i].Name}") return false;
            if (tab.TabColor != ColorFor(i)) return false;
        }
        return true;
    }

    /// <summary>
    /// Maps a layer index to the closest layer whose tab is shown, so leaving
    /// edit mode never displays a layer the user hid: the index itself while
    /// its tab is in <see cref="Layers"/> and shown, otherwise the nearest
    /// shown layer (a tie goes to the lower index). With no layers, or none
    /// shown, there is nothing to map to and the index is returned unchanged.
    /// </summary>
    public int NearestShownLayer(int index)
    {
        if (Layers.Any(l => l.Index == index && l.IsShown)) return index;
        var shown = Layers.Where(l => l.IsShown).ToList();
        if (shown.Count == 0) return index;
        return shown
            .OrderBy(l => Math.Abs(l.Index - index))
            .ThenBy(l => l.Index)
            .First().Index;
    }

    /// <summary>
    /// Sets or clears a per-layer colour override for the active keyboard
    /// profile. Persists to <see cref="UserSettings.LayerColors"/>, updates the
    /// static palette and repaints the tab swatches in place, then raises
    /// <see cref="ColorsChanged"/> for the board.
    /// </summary>
    public void SetColorOverride(int layerIndex, string? hex)
    {
        var profileId = ProfileId;
        LayerColorPalette.SetOverride(profileId, layerIndex, hex);

        _settings.Update(s => string.IsNullOrWhiteSpace(hex)
            ? s with { LayerColors = PerProfile.RemoveInner(s.LayerColors, profileId, layerIndex) }
            : s with { LayerColors = PerProfile.SetInner(s.LayerColors, profileId, layerIndex, hex!) }, "LayerTabs");

        foreach (var tab in Layers)
            tab.TabColor = ColorFor(tab.Index);
        ColorsChanged?.Invoke();
    }

    /// <summary>
    /// Shows or hides one layer's tab for the active keyboard profile. Persists
    /// to <see cref="UserSettings.HiddenLayers"/> and updates the matching tab
    /// in place (a rebuild would steal selection focus). Layer 0 is always
    /// shown, so requests for it are ignored. The layer itself stays fully
    /// live: auto-follow, Settings pickers and keyboard pushes are untouched.
    /// </summary>
    public void SetShown(int layerIndex, bool isShown)
    {
        if (layerIndex <= 0) return;

        var profileId = ProfileId;
        _settings.Update(s => s with
        {
            HiddenLayers = HiddenLayers.With(s.HiddenLayers, profileId, layerIndex, hidden: !isShown),
        }, "LayerTabs");

        foreach (var tab in Layers)
            if (tab.Index == layerIndex)
                tab.IsShown = isShown;
    }
}
