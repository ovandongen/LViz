using CommunityToolkit.Mvvm.ComponentModel;
using LViz.Core.Keymap;
using LViz.Core.Models;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// One entry in the combo legend: a sequentially-numbered combo for the
/// active layer, with its rendered label and the participating-key labels
/// (e.g. "Q + W"). Highlight state is bidirectional — hovering either the
/// key indicator on the board or this tile in the legend flips
/// <see cref="IsHighlighted"/> on every matching combo.
/// </summary>
public partial class ComboViewModel : ObservableObject
{
    public int Number { get; }
    public string Label { get; }
    public string Subscript { get; }
    public string ParticipatingKeysText { get; }
    public IReadOnlyList<int> KeyIndices { get; }

    /// <summary>
    /// Sorted, comma-joined key-positions string — the stable combo identity
    /// used for <see cref="LViz.Core.Settings.ComboLabelOverride"/> lookup.
    /// e.g. <c>"12,13"</c> for a two-key combo on positions 12 and 13.
    /// </summary>
    public string KeyPositionsKey { get; }

    /// <summary>
    /// Computed default label (what the formatter would produce from the
    /// binding alone, ignoring user overrides). Surfaced to the edit dialog
    /// as the "default" hint so the user can see what they're overriding.
    /// </summary>
    public string DefaultLabel { get; }

    [ObservableProperty] private bool _isHighlighted;

    public ComboViewModel(
        int number,
        ZmkCombo combo,
        Func<int, string> labelLookup,
        IReadOnlyDictionary<string, ZmkMacro>? macros = null,
        ComboLabelOverride? overrideEntry = null)
    {
        Number = number;
        KeyIndices = combo.KeyPositions;
        KeyPositionsKey = ComboLabelOverrides.KeyPositionsToKey(combo.KeyPositions);

        var (computedLabel, subscript, _) = KeyLabelFormatter.FormatBinding(combo.Binding, null, holdTap: null, macros: macros);
        DefaultLabel = !string.IsNullOrEmpty(computedLabel) ? computedLabel : combo.Binding.Display;
        Label = !string.IsNullOrEmpty(overrideEntry?.MainLabel) ? overrideEntry!.MainLabel : DefaultLabel;
        Subscript = subscript;

        ParticipatingKeysText = string.Join(" + ", combo.KeyPositions.Select(idx =>
        {
            var lookup = labelLookup(idx);
            return string.IsNullOrWhiteSpace(lookup) ? idx.ToString() : lookup;
        }));
    }
}
