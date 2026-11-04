using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LViz.App.Localization;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// Modal key-rebind dialog on top of <see cref="KeyDialogViewModel"/>: pick a
/// behavior, then fill each parameter tab. Its window result is the new
/// binding, or <c>null</c> when the user cancelled — Esc, Cancel, or the close
/// button all answer <c>null</c>. While the current tab hosts a keycode picker
/// in record mode, every key goes to a <see cref="KeyRecorder"/> instead of the
/// focused control; otherwise Esc closes and the platform command modifier
/// with Left/Right steps through the tabs.
/// </summary>
public partial class KeyDialog : Window
{
    /// <summary>Alive exactly while a recording is in progress: created when
    /// one starts, dropped when it produces a result or stops.</summary>
    private KeyRecorder? _recorder;

    /// <summary>The keycode picker of the current tab; the recorder watch
    /// follows it so a recording that stops (mouse-toggled off, unknown key)
    /// drops its recorder without needing a key event.</summary>
    private KeycodePickerViewModel? _watchedPicker;

    public KeyDialog()
    {
        InitializeComponent();
        // Tunnel routing: while recording, the window must see every press
        // and release before the focused text box or list does.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is KeyDialogViewModel vm)
            vm.PropertyChanged += OnViewModelPropertyChanged;
        SyncPickerWatch();
    }

    // ─── Result ───────────────────────────────────────────────────────────

    private void OnOkClick(object? sender, RoutedEventArgs e) =>
        Close(DataContext is KeyDialogViewModel vm ? vm.Result : null);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    // ─── Keys ─────────────────────────────────────────────────────────────

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (DataContext is not KeyDialogViewModel vm) return;

        if (vm.CurrentParam is KeycodeParamViewModel { Picker.IsRecording: true } keycode)
        {
            // While recording, every key belongs to the recorder — the
            // text boxes and lists see nothing at all.
            e.Handled = true;
            _recorder ??= new KeyRecorder();
            var recorded = _recorder.KeyDown(e.PhysicalKey, e.KeyModifiers);
            if (recorded is not null)
                keycode.Picker.ApplyRecorded(recorded); // ends the recording either way
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
            return;
        }

        // Command-modifier arrows step through the tabs (handled here so the
        // lists keep their plain arrows and the text boxes their word jumps).
        if (e.KeyModifiers != (PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control))
            return;

        switch (e.Key)
        {
            case Key.Left:
                vm.PreviousTab();
                e.Handled = true;
                break;
            case Key.Right:
                vm.NextTab();
                e.Handled = true;
                break;
        }
    }

    private void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        if (DataContext is not KeyDialogViewModel vm) return;
        if (vm.CurrentParam is not KeycodeParamViewModel { Picker.IsRecording: true } keycode) return;

        e.Handled = true;
        var recorded = _recorder?.KeyUp(e.PhysicalKey);
        if (recorded is not null)
            keycode.Picker.ApplyRecorded(recorded);
    }

    // ─── Recording watch ──────────────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KeyDialogViewModel.CurrentParam))
            SyncPickerWatch();
    }

    private void SyncPickerWatch()
    {
        var picker = (DataContext as KeyDialogViewModel)?.CurrentParam is KeycodeParamViewModel keycode
            ? keycode.Picker
            : null;
        if (ReferenceEquals(picker, _watchedPicker)) return;

        if (_watchedPicker is not null)
            _watchedPicker.PropertyChanged -= OnPickerPropertyChanged;
        _watchedPicker = picker;
        if (picker is not null)
            picker.PropertyChanged += OnPickerPropertyChanged;

        // Leaving a param that is not recording drops any recorder it left behind.
        if (picker is { IsRecording: false })
            _recorder = null;
    }

    private void OnPickerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(KeycodePickerViewModel.IsRecording)) return;

        // One recorder per recording: fresh when one starts, gone when it stops.
        _recorder = (KeycodePickerViewModel)sender! is { IsRecording: true } ? new KeyRecorder() : null;
    }

    // ─── Small wiring handlers ────────────────────────────────────────────

    private void OnQuickBehaviorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: KeyBehaviorChoice choice }
            && DataContext is KeyDialogViewModel vm)
        {
            vm.SelectedBehavior = choice;
        }
    }

    /// <summary>A tab bar with no selection (a deselect, or the new tab list
    /// of a behavior switch) snaps back to the VM's tab on the next turn, once
    /// the switch has set it; the VM ignores the -1 meanwhile.</summary>
    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox tabs || tabs.SelectedIndex >= 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (tabs.SelectedIndex < 0 && DataContext is KeyDialogViewModel vm && vm.SelectedTabIndex < tabs.ItemCount)
                tabs.SelectedIndex = vm.SelectedTabIndex;
        });
    }

    // ─── List selection restore ───────────────────────────────────────────

    // A ListBox whose ItemsSource is replaced (search, category switch,
    // behavior filter) drops its selection, and the TwoWay binding does not
    // put it back even when the view-model still holds an equal item. The
    // view-models ignore the null that comes in, so the pick survives; these
    // handlers re-select its row once the list has settled. Tag says which
    // view-model property a list shows.

    private void OnSelectionListAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not ListBox list) return;
        list.PropertyChanged -= OnSelectionListPropertyChanged;
        list.PropertyChanged += OnSelectionListPropertyChanged;
        list.SelectionChanged -= OnSelectionListSelectionChanged;
        list.SelectionChanged += OnSelectionListSelectionChanged;
        RestoreSelectionLater(list);
    }

    private void OnSelectionListPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ItemsControl.ItemsSourceProperty && sender is ListBox list)
            RestoreSelectionLater(list);
    }

    private void OnSelectionListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: null } list)
            RestoreSelectionLater(list);
    }

    private static void RestoreSelectionLater(ListBox list) =>
        Dispatcher.UIThread.Post(() =>
        {
            var wanted = WantedSelection(list);
            if (wanted is null || Equals(list.SelectedItem, wanted) || !list.Items.Contains(wanted)) return;
            list.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, wanted);
        });

    private static object? WantedSelection(ListBox list) => list.Tag switch
    {
        "keycodes" => (list.DataContext as KeycodeParamViewModel)?.Picker.SelectedItem,
        "categories" => (list.DataContext as KeycodeParamViewModel)?.Picker.SelectedCategory,
        "behaviors" => (list.DataContext as KeyDialogViewModel)?.SelectedOtherBehavior,
        _ => null,
    };

    private void OnKeyCodeSearchAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Posted so focus lands after the tab content has finished attaching.
        if (sender is TextBox box)
            Dispatcher.UIThread.Post(() => box.Focus());
    }
}

/// <summary>Renders a keycode row's "used N times" badge through
/// <c>KeyDialog_UseCountFormat</c>; zero renders as empty text, which hides
/// the badge without a second binding.</summary>
public sealed class KeycodeUseCountConverter : IValueConverter
{
    public static readonly KeycodeUseCountConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Loc.Instance.Format("KeyDialog_UseCountFormat", count) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
