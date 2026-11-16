using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;

namespace LViz.App.ViewModels;

/// <summary>
/// View model for the rename-layer dialog: shows which layer is being renamed,
/// pre-fills its current name, and answers the typed name only when it is a
/// usable change. The dialog's Rename button stays disabled while
/// <see cref="CanConfirm"/> is false, and the view-model layer of the rename
/// re-checks the same rules, so a name can only reach the edit through
/// <see cref="Answer"/>.
/// </summary>
public sealed partial class RenameLayerDialogViewModel : ObservableObject
{
    /// <summary>The layer being renamed and the name it has right now.</summary>
    public RenameLayerRequest Request { get; }

    /// <summary>Window title.</summary>
    public string Title { get; }

    /// <summary>The prompt naming the layer index and its current name.</summary>
    public string Prompt { get; }

    /// <summary>The typed name, pre-filled with the current one.</summary>
    [ObservableProperty]
    private string _name;

    public RenameLayerDialogViewModel(RenameLayerRequest request)
    {
        Request = request;
        Title = Loc.Instance["Edit_RenameLayerTitle"];
        Prompt = Loc.Instance.Format(
            "Edit_RenameLayerPromptFormat", request.LayerIndex, request.CurrentName);
        _name = request.CurrentName;
    }

    /// <summary>
    /// True when <see cref="Name"/> holds a new usable layer name: non-empty
    /// after trimming, free of control characters, and different from the
    /// current name (comparison on the trimmed text, so padding around the
    /// old name changes nothing). Raised whenever <see cref="Name"/> changes.
    /// </summary>
    public bool CanConfirm
    {
        get
        {
            var trimmed = Name.Trim();
            return trimmed.Length > 0
                && !Name.Any(char.IsControl)
                && trimmed != Request.CurrentName;
        }
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    /// <summary>
    /// The rename to apply: the trimmed <see cref="Name"/> when
    /// <see cref="CanConfirm"/>, else <see langword="null"/> = keep the
    /// current name.
    /// </summary>
    public string? Answer() => CanConfirm ? Name.Trim() : null;
}
