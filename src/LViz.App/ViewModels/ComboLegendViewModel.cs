using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Services;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Editing;
using LViz.Core.Models;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// The combo legend: the numbered tile strip under the keyboard, the overlay
/// toggle that also numbers the combo badges on the keys, the per-combo label
/// overrides, and the hover highlight between tiles and keys. A board surface
/// exposes it as <see cref="IBoardSurface.ComboLegend"/>; the main VM feeds it
/// through <see cref="SetEditing"/> and <see cref="Rebuild"/>. The picker and
/// the combo dialog build an inert one (no settings, no shell) that only
/// carries the overlay flag.
/// </summary>
public partial class ComboLegendViewModel : ObservableObject
{
    private readonly IList<KeyViewModel> _keys;
    private readonly ISettingsService? _settings;
    private readonly IMainWindowShell? _shell;

    private KeymapEditViewModel? _edit;
    private LoadedKeymap? _loaded;
    private readonly Func<string> _profileIdSource;

    // Combo label overrides are scoped to the board shown right now.
    private string ProfileId => _profileIdSource();

    public ComboLegendViewModel(
        IList<KeyViewModel> keys,
        BoardStyleViewModel boardStyle,
        bool overlayVisible,
        Func<string> profileId,
        ISettingsService? settings = null,
        IMainWindowShell? shell = null)
    {
        _keys = keys;
        BoardStyle = boardStyle;
        _profileIdSource = profileId;
        _settings = settings;
        _shell = shell;
        _isOverlayVisible = overlayVisible;
    }

    /// <summary>The tile hover/active border takes the board's press colour.</summary>
    public BoardStyleViewModel BoardStyle { get; }

    /// <summary>
    /// When true, combo keys show numbered indicators (e.g. <c>"1"</c>,
    /// <c>"1,3"</c>) instead of the static earmark, and the combo legend is
    /// visible beneath the keyboard. Persisted across launches.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLegendVisible))]
    private bool _isOverlayVisible;

    partial void OnIsOverlayVisibleChanged(bool value) =>
        _settings?.Update(s => s with { ComboOverlayVisible = value }, "ComboLegend");

    [RelayCommand]
    private void ToggleOverlay() => IsOverlayVisible = !IsOverlayVisible;

    /// <summary>The open keymap edit, or null. Drives the in-legend edit commands.</summary>
    public KeymapEditViewModel? Edit => _edit;

    public bool IsEditing => _edit is not null;

    /// <summary>
    /// Whether the legend renders: the persisted overlay toggle, or always
    /// while a keymap edit is open — in edit mode the legend is the combo
    /// editor's tile strip.
    /// </summary>
    public bool IsLegendVisible => IsOverlayVisible || IsEditing;

    /// <summary>
    /// Every combo in the shown keymap, numbered sequentially in source-file
    /// order starting at 1. Not layer-scoped. While a keymap edit is open these
    /// are the <em>edited</em> combos, every tile editable. Repopulated by
    /// <see cref="Rebuild"/>.
    /// </summary>
    public ObservableCollection<ComboViewModel> Combos { get; } = new();

    /// <summary>The main VM follows its edit here; the next <see cref="Rebuild"/> paints it.</summary>
    public void SetEditing(KeymapEditViewModel? edit)
    {
        _edit = edit;
        OnPropertyChanged(nameof(Edit));
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(IsLegendVisible));
    }

    /// <summary>
    /// Walks the shown keymap's combos once and populates <see cref="Combos"/>
    /// plus each key's <see cref="KeyViewModel.SetCombos"/>. While a keymap
    /// edit is open the <em>edited</em> config paints (and wins over
    /// <paramref name="loaded"/>), and every tile is editable and carries its
    /// pending-change mark. Combos aren't layer-scoped, so nothing here repeats
    /// on a plain layer switch. The participating-key labels in a tile's
    /// <c>ParticipatingKeysText</c> reflect whatever layer was active at the
    /// rebuild.
    /// </summary>
    public void Rebuild(LoadedKeymap? loaded)
    {
        _loaded = loaded;
        Rebuild();
    }

    private void Rebuild()
    {
        SetHighlighted(Array.Empty<int>());
        Combos.Clear();

        var edit = _edit;
        var km = edit?.Keymap ?? _loaded;
        if (km is null)
        {
            for (var i = 0; i < _keys.Count; i++)
                _keys[i].SetCombos(Array.Empty<ZmkCombo>(), Array.Empty<int>(), _ => "");
            return;
        }

        var allCombos = km.Combos;
        var combosByKey = new Dictionary<int, List<ZmkCombo>>();
        var numbersByKey = new Dictionary<int, List<int>>();
        for (var i = 0; i < allCombos.Count; i++)
        {
            var combo = allCombos[i];
            var number = i + 1;
            foreach (var keyIdx in combo.KeyPositions)
            {
                if (!combosByKey.TryGetValue(keyIdx, out var list))
                    combosByKey[keyIdx] = list = new List<ZmkCombo>();
                list.Add(combo);
                if (!numbersByKey.TryGetValue(keyIdx, out var nums))
                    numbersByKey[keyIdx] = nums = new List<int>();
                nums.Add(number);
            }
        }

        string LabelLookup(int idx) => idx >= 0 && idx < _keys.Count ? _keys[idx].Label : "";

        for (var i = 0; i < _keys.Count; i++)
        {
            if (combosByKey.TryGetValue(i, out var keyCombos))
                _keys[i].SetCombos(keyCombos, numbersByKey[i], LabelLookup);
            else
                _keys[i].SetCombos(Array.Empty<ZmkCombo>(), Array.Empty<int>(), LabelLookup);
        }

        for (var i = 0; i < allCombos.Count; i++)
        {
            var combo = allCombos[i];
            var key = ComboLabelOverrides.KeyPositionsToKey(combo.KeyPositions);
            var ov = ComboLabelOverrides.Get(ProfileId, key);
            var tile = new ComboViewModel(i + 1, combo, LabelLookup, km.MacrosByName, ov);
            if (edit is not null)
            {
                tile.IsEditable = true;
                tile.IsChanged = edit.Session.IsComboChanged(i);
            }
            Combos.Add(tile);
        }
    }

    /// <summary>
    /// Sets the currently-highlighted combo numbers. Pass empty to clear.
    /// Idempotent and cheap to call from PointerEntered / PointerExited.
    ///
    /// Drives three pieces of state in lockstep: the legend tile's
    /// <c>IsHighlighted</c>, each on-key pill's <c>IsHighlighted</c>, and —
    /// for every participating key (including ones whose pill was capped
    /// out by <c>MaxComboBadges</c>) — the per-key
    /// <c>IsComboKeyHighlighted</c> flag that re-uses the existing
    /// press-dot visual via <c>IsAnyHighlight</c>. That way hovering a
    /// combo lights the same dot a live HID press would, no extra
    /// indicator.
    /// </summary>
    public void SetHighlighted(IReadOnlyCollection<int> numbers)
    {
        numbers ??= Array.Empty<int>();
        var numberSet = numbers as ISet<int> ?? new HashSet<int>(numbers);

        for (var i = 0; i < Combos.Count; i++)
            Combos[i].IsHighlighted = numberSet.Contains(Combos[i].Number);

        foreach (var key in _keys)
        {
            key.IsComboKeyHighlighted = false;
            foreach (var badge in key.ComboBadges)
                badge.IsHighlighted = numberSet.Contains(badge.Number);
        }

        foreach (var n in numberSet)
        {
            if (n < 1 || n > Combos.Count) continue;
            foreach (var idx in Combos[n - 1].KeyIndices)
                if (idx >= 0 && idx < _keys.Count)
                    _keys[idx].IsComboKeyHighlighted = true;
        }
    }

    /// <summary>
    /// The user right-clicked a legend tile: edit that combo's label override
    /// through the shell's dialog and apply the answer.
    /// </summary>
    public async void RequestEdit(string keyPositionsKey)
    {
        if (_shell is null) return;
        var ctx = GetLabelEditContext(keyPositionsKey);
        if (ctx is null) return;
        var answer = await _shell.EditComboLabelAsync(
            ctx.Value.Number, keyPositionsKey, ctx.Value.DefaultLabel, ctx.Value.ParticipatingKeys, ctx.Value.Existing);
        if (answer is not null)
            SetLabelOverride(keyPositionsKey, answer.Value);
    }

    /// <summary>
    /// Sets or clears a combo-pill label override for the active profile, then
    /// rebuilds so the affected tile repaints.
    /// </summary>
    public void SetLabelOverride(string keyPositionsKey, ComboLabelOverride? value)
    {
        ComboLabelOverrides.Set(ProfileId, keyPositionsKey, value);
        PersistOverrides();
        Rebuild();
    }

    /// <summary>
    /// Snapshot of the default-computed label + current override (if any) for
    /// the combo identified by <paramref name="keyPositionsKey"/>. Null when no
    /// combo matches (defensive — the view only asks for an existing tile).
    /// </summary>
    public (int Number, string DefaultLabel, string ParticipatingKeys, ComboLabelOverride? Existing)?
        GetLabelEditContext(string keyPositionsKey)
    {
        var combo = Combos.FirstOrDefault(c => c.KeyPositionsKey == keyPositionsKey);
        if (combo is null) return null;
        var existing = ComboLabelOverrides.Get(ProfileId, keyPositionsKey);
        return (combo.Number, combo.DefaultLabel, combo.ParticipatingKeysText, existing);
    }

    /// <summary>
    /// Carries this keyboard's combo label overrides across a save that moved
    /// or removed combos: a rewritten combo takes its label to its new
    /// key-position key (when nothing already lives there), and a deleted
    /// combo's label goes with it. Overrides of combos the save only renamed
    /// or retargeted (same positions) stay put, and adds have no label yet.
    /// Persists once, and only when something actually moved.
    /// </summary>
    public void MoveLabelOverrides(IReadOnlyList<KeymapComboChange> changes)
    {
        var moved = false;
        foreach (var change in changes)
        {
            switch (change.Edit)
            {
                case ComboEdit.Change:
                {
                    if (change.Old is not { } old || change.New is not { } combo) continue;
                    var oldKey = ComboLabelOverrides.KeyPositionsToKey(old.KeyPositions);
                    var newKey = ComboLabelOverrides.KeyPositionsToKey(combo.KeyPositions);
                    if (oldKey == newKey) continue;

                    var existing = ComboLabelOverrides.Get(ProfileId, oldKey);
                    if (existing is null) continue; // nothing to carry, and clearing an absent key is a no-op
                    if (ComboLabelOverrides.Get(ProfileId, newKey) is null)
                        ComboLabelOverrides.Set(ProfileId, newKey, existing);
                    ComboLabelOverrides.Set(ProfileId, oldKey, null);
                    moved = true;
                    break;
                }
                case ComboEdit.Delete:
                {
                    if (change.Old is not { } old) continue;
                    var oldKey = ComboLabelOverrides.KeyPositionsToKey(old.KeyPositions);
                    if (ComboLabelOverrides.Get(ProfileId, oldKey) is null) continue;

                    ComboLabelOverrides.Set(ProfileId, oldKey, null);
                    moved = true;
                    break;
                }
            }
        }

        if (moved) PersistOverrides();
    }

    private void PersistOverrides() =>
        _settings?.Update(s => s with { ComboLabelOverrides = ComboLabelOverrides.Snapshot() }, "ComboLegend");
}
