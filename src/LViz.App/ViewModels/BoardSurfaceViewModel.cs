using System.Collections.ObjectModel;
using LViz.Core.Layout;

namespace LViz.App.ViewModels;

/// <summary>
/// The board a <c>BoardView</c> renders: the keys of one profile (all of them,
/// plus the left and right halves the stacked layout moves independently), the
/// canvas geometry, the press style, the combo legend and the three key
/// callbacks. Every host that shows a board (the main window, the combo dialog,
/// the exit-key picker) owns exactly one of these and is the view's
/// <c>DataContext</c>.
///
/// <para>The key collections are filled once and then rebuilt in place by
/// <see cref="SetProfile"/>, so the legend and the press tracker keep their
/// reference. How a key starts out (default fill, stripped labels) is the
/// <c>keyFactory</c> option; a callback left null makes <c>BoardView</c> leave
/// the pointer event unhandled, which the main window relies on outside edit
/// mode (the press then starts a window drag).</para>
/// </summary>
public sealed class BoardSurfaceViewModel
{
    private readonly Func<KeyPosition, KeyViewModel> _keyFactory;

    /// <param name="profile">The board to build keys for.</param>
    /// <param name="layout">Canvas geometry; the surface hands it each new profile.</param>
    /// <param name="style">Press-highlight colours.</param>
    /// <param name="legendFor">Builds the legend over the surface's key collection.</param>
    /// <param name="keyFactory">Creates the view-model of one key; defaults to a plain
    /// <see cref="KeyViewModel"/> that the host styles afterwards.</param>
    public BoardSurfaceViewModel(
        IKeyboardProfile profile,
        BoardLayoutViewModel layout,
        BoardStyleViewModel style,
        Func<IList<KeyViewModel>, ComboLegendViewModel> legendFor,
        Func<KeyPosition, KeyViewModel>? keyFactory = null)
    {
        _keyFactory = keyFactory ?? (pos => new KeyViewModel(pos));
        BoardLayout = layout;
        BoardStyle = style;
        ComboLegend = legendFor(Keys);
        SetProfile(profile);
    }

    /// <summary>Every key, in keymap binding order.</summary>
    public ObservableCollection<KeyViewModel> Keys { get; } = new();

    /// <summary>Left-hand subset of <see cref="Keys"/>, bound separately so the stacked layout can translate the half.</summary>
    public ObservableCollection<KeyViewModel> LeftKeys { get; } = new();

    /// <summary>Right-hand subset of <see cref="Keys"/>.</summary>
    public ObservableCollection<KeyViewModel> RightKeys { get; } = new();

    /// <summary>Canvas geometry + stacked-mode offsets the view binds against.</summary>
    public BoardLayoutViewModel BoardLayout { get; }

    /// <summary>Press-highlight fill/stroke colours for the per-key press dot.</summary>
    public BoardStyleViewModel BoardStyle { get; }

    /// <summary>
    /// The combo legend. Its <see cref="ComboLegendViewModel.IsOverlayVisible"/>
    /// makes combo keys show numbered badges instead of the static earmark.
    /// Only the main window's is live (toolbar toggle); the picker's is always
    /// off, the combo dialog's always on.
    /// </summary>
    public ComboLegendViewModel ComboLegend { get; }

    /// <summary>Left press on a key (argument: <c>Position.Index</c>); null = the press is not handled.</summary>
    public Action<int>? OnKeyTapped { get; set; }

    /// <summary>Second press of a double-click; null falls back to <see cref="OnKeyTapped"/>.</summary>
    public Action<int>? OnKeyDoubleTapped { get; set; }

    /// <summary>Right press on a key; null = the press is not handled.</summary>
    public Action<int>? OnKeyRightTapped { get; set; }

    /// <summary>Replaces the keys with a fresh set for <paramref name="profile"/> and re-measures the canvas.</summary>
    public void SetProfile(IKeyboardProfile profile)
    {
        Keys.Clear();
        LeftKeys.Clear();
        RightKeys.Clear();

        foreach (var pos in profile.Keys)
        {
            var vm = _keyFactory(pos);
            Keys.Add(vm);
            (pos.Hand == Hand.Left ? LeftKeys : RightKeys).Add(vm);
        }

        BoardLayout.SetProfile(profile);
    }
}
