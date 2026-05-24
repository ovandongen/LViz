using CommunityToolkit.Mvvm.ComponentModel;
using LViz.Core.Keymap;
using LViz.Core.Models;

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

    [ObservableProperty] private bool _isHighlighted;

    public ComboViewModel(
        int number,
        ZmkCombo combo,
        Func<int, string> labelLookup)
    {
        Number = number;
        KeyIndices = combo.KeyPositions;

        var (label, subscript, _) = KeyLabelFormatter.FormatBinding(combo.Binding, null);
        Label = !string.IsNullOrEmpty(label) ? label : combo.Binding.Display;
        Subscript = subscript;

        ParticipatingKeysText = string.Join(" + ", combo.KeyPositions.Select(idx =>
        {
            var lookup = labelLookup(idx);
            return string.IsNullOrWhiteSpace(lookup) ? idx.ToString() : lookup;
        }));
    }
}
