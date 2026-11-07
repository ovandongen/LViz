using System.Globalization;
using LViz.App.Localization;

namespace LViz.App.ViewModels;

/// <summary>
/// The rows of the ring's list panel, built from the parameter editors the
/// ring is on. Pure functions of the editor state and the search text.
/// </summary>
internal static class KeyRingRows
{
    /// <summary>The picker's own list: the search runs through the picker, so
    /// the panel and the dialog's own keycode editor cannot drift apart.</summary>
    public static IReadOnlyList<KeyRingPanelRow> Keycodes(KeycodePickerViewModel picker, string search)
    {
        picker.SearchText = search;
        var picked = picker.SelectedKeycode;

        return
        [
            .. picker.Items.Select(item => new KeyRingPanelRow(
                new KeyRingTarget.Keycode(item.Keycode),
                item.Name,
                item.Description,
                LabelIsCode: true,
                IsSelected: item.Keycode == picked)),
        ];
    }

    public static IReadOnlyList<KeyRingPanelRow> Layers(
        LayerParamViewModel param, Func<int, string> layerName) =>
        [
            .. param.Layers.Select(choice => new KeyRingPanelRow(
                new KeyRingTarget.Layer(choice.Index),
                layerName(choice.Index),
                choice.Index.ToString(CultureInfo.InvariantCulture),
                LabelIsCode: false,
                IsSelected: choice.Index == param.SelectedLayer?.Index)),
        ];

    /// <summary>A command that takes a profile number is one row per profile
    /// instead of one row of its own — the row says what it writes.</summary>
    public static IReadOnlyList<KeyRingPanelRow> Commands(CommandParamViewModel param)
    {
        var rows = new List<KeyRingPanelRow>();

        foreach (var choice in param.Commands)
        {
            if (!choice.Command.TakesNumber)
            {
                rows.Add(new KeyRingPanelRow(
                    new KeyRingTarget.Command(choice),
                    choice.Name,
                    choice.Command.Name,
                    LabelIsCode: false,
                    IsSelected: ReferenceEquals(choice, param.SelectedCommand)));
                continue;
            }

            rows.AddRange(param.Profiles.Select(profile => new KeyRingPanelRow(
                new KeyRingTarget.Profile(choice, profile),
                Loc.Instance.Format("KeyDialog_CommandProfileFormat", choice.Name, profile.Number),
                null,
                LabelIsCode: false,
                IsSelected: ReferenceEquals(choice, param.SelectedCommand)
                    && ReferenceEquals(profile, param.SelectedProfile))));
        }

        return rows;
    }

    /// <summary>The list panels filter what the ring cannot show: label or sub
    /// containing the search text. The keycode panel does its own searching
    /// through the picker.</summary>
    public static IReadOnlyList<KeyRingPanelRow> Filter(
        IReadOnlyList<KeyRingPanelRow> rows, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return rows;

        return rows
            .Where(row => row.Label.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (row.Sub?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToArray();
    }
}
