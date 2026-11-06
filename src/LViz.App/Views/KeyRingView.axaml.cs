using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LViz.App.ViewModels;
// Avalonia.Input also declares a KeyBinding, so the model type is aliased.
using ZmkKeyBinding = LViz.Core.Models.KeyBinding;

namespace LViz.App.Views;

/// <summary>
/// How a <see cref="KeyRingView"/> closed: the binding to write (null on
/// cancel or when the result equals what the key opened with) and whether
/// the host should open the same key in the key dialog instead.
/// </summary>
public sealed class KeyRingClosedEventArgs(ZmkKeyBinding? result, bool openInDialog) : EventArgs
{
    /// <summary>The binding to write, or null.</summary>
    public ZmkKeyBinding? Result { get; } = result;

    /// <summary>True when the key should switch to the key dialog.</summary>
    public bool OpenInDialog { get; } = openInDialog;
}

/// <summary>
/// The key ring as the host shows it: the ring itself
/// (<see cref="KeyRingControl"/>, part 8), the list panel with its search
/// box, the centre text box for the free-text step, and an
/// "open in the key dialog" escape hatch. Everything is driven by the
/// <see cref="KeyRingViewModel"/> on the DataContext; the only thing the
/// view adds is where the panel sits (<see cref="SetPanelPosition"/>) and
/// the keyboard, and it reports the outcome once through
/// <see cref="Closed"/>.
/// </summary>
public partial class KeyRingView : UserControl
{
    private KeyRingViewModel? _ring;
    private bool _closed;

    /// <summary>Whether the previous press on a panel row picked a value, so
    /// the second press of a double-click saves (see
    /// <see cref="OnRowPointerPressed"/>).</summary>
    private bool _lastRowPicked;

    public KeyRingView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (ReferenceEquals(_ring, DataContext)) return;
            if (_ring is not null) _ring.PropertyChanged -= OnRingChanged;
            _ring = DataContext as KeyRingViewModel;
            if (_ring is not null) _ring.PropertyChanged += OnRingChanged;
            UpdateStep();
        };

        // The control draws a frame-wide transparent rectangle, so every
        // press reaches it (part 8): a centre save raises SaveRequested,
        // anything off ring, crumbs and hint raises Dismissed.
        RingControl.SaveRequested += (_, _) => Close(_ring?.CloseResult);
        RingControl.Dismissed += (_, _) => Close(null);

        OpenInDialogButton.Click += (_, _) => RaiseClosed(null, openInDialog: true);

        // Tunnel so the view decides Esc/Enter/arrows before the focused
        // text box gets them; the text boxes keep plain typing and Tab
        // moves focus inside them (the outside-box branch below skips them).
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnTextInput);
    }

    /// <summary>Raised once when the view has served its purpose: the
    /// binding to write (or null) and whether to switch to the key
    /// dialog.</summary>
    public event EventHandler<KeyRingClosedEventArgs>? Closed;

    /// <summary>Places the panel at view coordinates — it may reach outside
    /// the view's own 448 × 514 (the host has the room).</summary>
    public void SetPanelPosition(double left, double top)
    {
        Canvas.SetLeft(PanelBorder, left);
        Canvas.SetTop(PanelBorder, top);
    }

    /// <summary>Focuses what the current step is about — the centre text
    /// box, the panel's search box, else the view itself (for the ring
    /// keys). Posted to the dispatcher so the step's visibility changes
    /// have been applied first.</summary>
    public void FocusInitial()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_ring is not { } vm)
            {
                Focus();
                return;
            }

            if (vm.StepKind == KeyRingStepKind.Text) CenterText.Focus();
            else if (vm.Panel is not null) SearchBox.Focus();
            else Focus();
        });
    }

    /// <summary>The text step shows its box and takes focus when it appears;
    /// a hidden centre box that still held focus hands it back to the view
    /// (a hidden control keeps its focus — AGENTS.md).</summary>
    private void UpdateStep()
    {
        if (_ring is not { } vm)
        {
            CenterText.IsVisible = false;
            PanelBorder.IsVisible = false;
            return;
        }

        bool showText = vm.StepKind == KeyRingStepKind.Text;
        bool wasTextVisible = CenterText.IsVisible;
        CenterText.IsVisible = showText;
        PanelBorder.IsVisible = vm.Panel is not null;

        if (showText && !wasTextVisible) CenterText.Focus();
        else if (!showText && CenterText.IsFocused) Focus();
    }

    private void OnRingChanged(object? sender, PropertyChangedEventArgs e) => UpdateStep();

    private void Close(ZmkKeyBinding? result) => RaiseClosed(result, openInDialog: false);

    private void RaiseClosed(ZmkKeyBinding? result, bool openInDialog)
    {
        if (_closed) return;
        _closed = true;
        Closed?.Invoke(this, new KeyRingClosedEventArgs(result, openInDialog));
    }

    // ─── keyboard ────────────────────────────────────────────────────────

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_ring is not { } vm) return;

        bool inTextBox = e.Source is TextBox;

        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
            return;
        }

        bool enter = e.Key == Key.Enter || e.PhysicalKey == PhysicalKey.NumPadEnter;
        if (enter && e.KeyModifiers == KeyModifiers.Shift)
        {
            RaiseClosed(null, openInDialog: true);
            e.Handled = true;
            return;
        }

        if (enter && e.KeyModifiers == KeyModifiers.None)
        {
            if (RingControl.ActivateKeyboardHighlight())
            {
                e.Handled = true;
                return;
            }

            // A highlighted panel row is picked — unless the caret is in
            // the centre box, where Enter should save (rule 3).
            if (!ReferenceEquals(e.Source, CenterText) && vm.Panel?.HighlightedId is { } id)
            {
                vm.Choose(id);
                e.Handled = true;
                return;
            }

            if (vm.CanClose)
            {
                Close(vm.CloseResult);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Up || e.Key == Key.Down)
        {
            if (vm.Panel is { } panel)
            {
                panel.MoveHighlight(e.Key == Key.Down ? 1 : -1);
                BringHighlightIntoView();
                e.Handled = true;
            }

            return;
        }

        // The rest is for the ring: skipped inside a text box, where ←→
        // move the caret, Backspace deletes, and Tab moves focus.
        if (inTextBox) return;

        switch (e.Key)
        {
            case Key.Left:
                RingControl.MoveKeyboardHighlight(-1);
                e.Handled = true;
                break;
            case Key.Right:
                RingControl.MoveKeyboardHighlight(1);
                e.Handled = true;
                break;
            case Key.Back:
                vm.Back();
                e.Handled = true;
                break;
            case Key.Tab:
                RingControl.MoveIntoBand();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Typing while a panel is shown but no text box has focus
    /// sends the text to the panel's search box, which then keeps it.</summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_ring is not { } vm || e.Text is not { Length: > 0 } text) return;
        if (e.Source is TextBox || vm.Panel is not { } panel) return;

        panel.SearchText += text;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
        e.Handled = true;
    }

    /// <summary>A press on a panel row picks it, like clicking the ring. A
    /// double-click picks and saves: its second press closes with the result
    /// once the binding is complete (the list may have changed under the
    /// pointer, so it is not picked again).</summary>
    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var lastRowPicked = _lastRowPicked;
        _lastRowPicked = false;

        if (_ring is not { } vm || sender is not Control { DataContext: KeyRingPanelRow row }) return;
        e.Handled = true;

        if (e.ClickCount >= 2 && lastRowPicked)
        {
            if (vm.CanClose) Close(vm.CloseResult);
            return;
        }

        _lastRowPicked = vm.Choose(row.Id) && KeyRingViewModel.PicksAValue(row.Id);
    }

    /// <summary>Scrolls the panel so the highlighted row is visible: the
    /// realized container asks for itself, and before realization the
    /// offset is computed from the fixed row height.</summary>
    private void BringHighlightIntoView()
    {
        if (_ring?.Panel is not { } panel || panel.HighlightIndex < 0) return;

        if (PanelItems.ContainerFromIndex(panel.HighlightIndex) is { } container)
        {
            container.BringIntoView();
            return;
        }

        // Not realized yet (the panel was shown the same turn): rows are
        // 26 px plus 2 px of margin, so the row's offset is index × 28.
        const double rowHeight = 28;
        double target = panel.HighlightIndex * rowHeight;
        double viewport = PanelScroll.Viewport.Height;
        double offset = PanelScroll.Offset.Y;

        if (target < offset) PanelScroll.Offset = new Point(0, target);
        else if (target + rowHeight > offset + viewport)
            PanelScroll.Offset = new Point(0, target + rowHeight - viewport);
    }
}

/// <summary>Upper-cases a string for the panel title.</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
        => (value as string)?.ToUpperInvariant() ?? string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
