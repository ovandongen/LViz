using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// View model for the combo-label editor. Mirrors
/// <see cref="EditKeyLabelDialogViewModel"/> but exposes only a single
/// <see cref="MainLabel"/> field — combo pills are small and the user asked
/// not to surface subscript / icon / font controls there.
/// </summary>
public sealed partial class EditComboLabelDialogViewModel : ObservableObject
{
    public int ComboNumber { get; }
    public string KeyPositionsKey { get; }
    public string DefaultLabelHint { get; }
    public string ParticipatingKeysText { get; }
    public bool HasExistingOverride { get; }

    public string HeaderText => Loc.Instance.Format("EditCombo_HeaderFormat", ComboNumber);
    public string DefaultHintLine => Loc.Instance.Format("EditCombo_DefaultFormat", DefaultLabelHint);
    public string ParticipatingHintLine => Loc.Instance.Format("EditCombo_KeysFormat", ParticipatingKeysText);

    [ObservableProperty] private string _mainLabel = "";

    public EditComboLabelDialogViewModel(
        int comboNumber,
        string keyPositionsKey,
        string defaultLabelHint,
        string participatingKeysText,
        ComboLabelOverride? existing)
    {
        ComboNumber = comboNumber;
        KeyPositionsKey = keyPositionsKey;
        DefaultLabelHint = defaultLabelHint;
        ParticipatingKeysText = participatingKeysText;
        HasExistingOverride = existing is not null;
        if (existing is not null)
            _mainLabel = existing.MainLabel;
    }

    public ComboLabelOverride ToOverride() => new() { MainLabel = MainLabel };
}
