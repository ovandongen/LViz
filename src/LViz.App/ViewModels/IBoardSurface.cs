using System.Collections.ObjectModel;

namespace LViz.App.ViewModels;

/// <summary>
/// Minimal property surface that <c>BoardView</c> binds against. Implemented
/// by <see cref="MainWindowViewModel"/> (the main keyboard render) and
/// <see cref="ExitKeyPickerViewModel"/> (the auto-switch exit-key picker
/// popup) so the same XAML renders both without forking the control.
///
/// <para>BoardView's compiled bindings target this interface; the picker VM
/// supplies a no-binding-context <see cref="KeyViewModel"/> list (empty
/// labels, no combo earmarks) and a click handler via
/// <see cref="OnKeyTapped"/>, while the main VM leaves the click handler
/// null so taps in the main window are inert.</para>
/// </summary>
public interface IBoardSurface
{
    ObservableCollection<KeyViewModel> LeftKeys { get; }
    ObservableCollection<KeyViewModel> RightKeys { get; }

    /// <summary>Canvas geometry + stacked-mode offsets the BoardView binds against.</summary>
    BoardLayoutViewModel BoardLayout { get; }

    /// <summary>Press-highlight fill/stroke colors for the per-key press dot.</summary>
    BoardStyleViewModel BoardStyle { get; }

    /// <summary>
    /// When true, combo keys render a numbered badge instead of the static
    /// earmark, and the indicator becomes hit-testable so hover handlers
    /// can drive bidirectional highlight against the legend. Main VM toggles
    /// this from the toolbar; the picker VM always returns false.
    /// </summary>
    bool IsComboOverlayVisible { get; }

    /// <summary>
    /// Optional per-cap click handler. When non-null, <c>BoardView</c>'s
    /// code-behind invokes this with the tapped <see cref="KeyViewModel"/>'s
    /// <c>Position.Index</c>. The main VM leaves it null so the main window's
    /// rendering stays read-only; the picker VM provides the handler.
    /// </summary>
    Action<int>? OnKeyTapped { get; }

    /// <summary>
    /// Optional per-cap right-click handler. Symmetric with
    /// <see cref="OnKeyTapped"/>: the main VM provides a handler that opens
    /// the per-key label-override editor; the exit-key picker leaves it
    /// null so right-clicks in that surface are inert.
    /// </summary>
    Action<int>? OnKeyRightTapped { get; }
}
