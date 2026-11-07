using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// The layer tab strip. Its DataContext is the <see cref="LayerTabsViewModel"/>;
/// the handlers below wire each tab's context menu and double-click rename.
/// </summary>
public partial class LayerTabsView : UserControl
{
    public LayerTabsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gives a tab's <see cref="ContextMenu"/> its owner's data context. A
    /// closed ContextMenu is not a visual child of its owner, so DataContext
    /// inheritance (which follows the visual tree) never reaches it or its
    /// items; without this the tab menu's command bindings would resolve to
    /// nothing. Tag records the menu that was already wired to this button so
    /// a re-firing Loaded never double-subscribes; the DataContext observer
    /// re-syncs whenever a recycled container gets a new layer.
    /// </summary>
    private void OnLayerTabLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button tab) return;
        if (tab.ContextMenu is not { } menu || ReferenceEquals(tab.Tag, menu)) return;
        tab.Tag = menu;

        menu.DataContext = tab.DataContext;
        tab.GetObservable(DataContextProperty).Subscribe(new MenuDataContextSync(menu));
    }

    /// <summary>Keeps one tab's ContextMenu DataContext in sync with its
    /// button (a closed ContextMenu is no visual child of the button, so it
    /// gets no inheritance; see <see cref="OnLayerTabLoaded"/>).</summary>
    private sealed class MenuDataContextSync(ContextMenu menu) : IObserver<object?>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(object? value) => menu.DataContext = value;
    }

    /// <summary>
    /// Right-click on a layer tab only opens its menu while a keymap edit is
    /// open: Avalonia raises <c>Opening</c> just before the menu appears, and
    /// anything but an editable tab's menu is cancelled, so outside edit mode
    /// a right-click does nothing at all.
    /// </summary>
    private void OnLayerTabMenuOpening(object? sender, CancelEventArgs e)
    {
        e.Cancel = (sender as ContextMenu)?.DataContext
            is not LayerViewModel { IsEditable: true };
    }

    /// <summary>
    /// Double-click on a layer tab opens the rename dialog while a keymap edit
    /// is open. The first click of the double-click has already selected the
    /// layer through the tab's own SelectCommand, which is the intent.
    /// </summary>
    private void OnLayerTabDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control tab) return;
        if (tab.DataContext is not LayerViewModel { IsEditable: true } layer) return;
        if (!layer.RenameCommand.CanExecute(null)) return;

        layer.RenameCommand.Execute(null);
        e.Handled = true;
    }
}
