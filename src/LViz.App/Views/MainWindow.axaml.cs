using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LViz.App.Ring;
using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Diagnostics;
// Avalonia.Input also declares a KeyBinding, so the model type is aliased.
using ZmkKeyBinding = LViz.Core.Models.KeyBinding;

namespace LViz.App.Views;

public partial class MainWindow : Window
{
    private bool? _barsOnBottom;
    private static readonly double ResizeEdge = OperatingSystem.IsWindows() ? 9 : 6;

    public MainWindow()
    {
        InitializeComponent();
        PositionChanged += OnPositionChanged;
        Opened += (_, _) =>
        {
            DiagnosticLog.Info("UI", "MainWindow.Opened event fired");
            ApplyTransparencyFallback();
            UpdateBarPosition();
        };

        if (OperatingSystem.IsMacOS())
            SystemDecorations = SystemDecorations.Full;

        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;

        // Edit-mode shortcuts ride the Tunnel route so they win over whatever
        // control happens to hold focus (see OnEditKeyDown).
        AddHandler(KeyDownEvent, OnEditKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Validates that window transparency is actually working and falls back gracefully.
    /// On some GPU configurations (especially NVIDIA on Windows), Avalonia reports transparency
    /// as available but the compositing doesn't render correctly, producing an invisible window.
    /// </summary>
    private void ApplyTransparencyFallback()
    {
        DiagnosticLog.Info("UI", $"Transparency: ActualTransparencyLevel={ActualTransparencyLevel}");

        if (ActualTransparencyLevel == WindowTransparencyLevel.None)
        {
            // Platform explicitly says no transparency — use solid background with system chrome.
            DiagnosticLog.Warn("UI", "Transparency: fallback to solid background + system decorations");
            Background = Avalonia.Media.SolidColorBrush.Parse(AppTheme.BgBaseHex);
            SystemDecorations = SystemDecorations.Full;
            ExtendClientAreaToDecorationsHint = false;
            return;
        }

        // On Windows, some NVIDIA drivers report Transparent as supported but the compositor
        // doesn't actually render the surface. A near-transparent background (alpha=1/255)
        // is invisible to humans but prevents the GPU from discarding the surface entirely.
        if (ActualTransparencyLevel == WindowTransparencyLevel.Transparent &&
            OperatingSystem.IsWindows())
        {
            DiagnosticLog.Info("UI", "Transparency: applied near-transparent safety background (Windows + Transparent)");
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromArgb(1, 0, 0, 0));

            // Kill the DWM-drawn border line that Windows renders around borderless
            // windows; without this the transparent body has clearly defined edges.
            // DWMWA_BORDER_COLOR=COLOR_NONE is Windows 11+; silently ignored on Win10.
            TryDisableDwmBorder();
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);

    private void TryDisableDwmBorder()
    {
        const int DWMWA_BORDER_COLOR = 34;
        const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            DiagnosticLog.Warn("UI", "DWM border: no HWND available, skipping");
            return;
        }

        try
        {
            uint colorNone = DWMWA_COLOR_NONE;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(uint));
            DiagnosticLog.Info("UI", $"DWM border: BORDER_COLOR=NONE hr=0x{hr:X8}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("UI", $"DWM border: P/Invoke failed: {ex.Message}");
        }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => UpdateBarPosition();

    private void UpdateBarPosition()
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen is null) return;

        var windowMidY = Position.Y + (Height / 2);
        var screenMidY = screen.WorkingArea.Height / 2;
        var shouldBeBottom = windowMidY > screenMidY;

        if (shouldBeBottom == _barsOnBottom) return;
        _barsOnBottom = shouldBeBottom;

        // Move the whole bars panel to top or bottom of the outer grid
        Grid.SetRow(BarsPanel, shouldBeBottom ? 2 : 0);

        // Swap inner order: status bar always at the outer edge; combo
        // legend (when visible) always closest to the keyboard.
        if (shouldBeBottom)
        {
            // Bottom edge: combo legend nearest the keyboard (row 0),
            // tabs (row 1), edit banner (row 2), status bar at the outer
            // edge (row 3).
            Grid.SetRow(ComboLegend, 0);
            Grid.SetRow(LayerTabs, 1);
            Grid.SetRow(EditBanner, 2);
            Grid.SetRow(StatusBar, 3);
            StatusBar.CornerRadius = new CornerRadius(0, 0, 8, 8);
        }
        else
        {
            // Top edge: status bar at the outer edge (row 0), edit banner
            // right below it (row 1), tabs (row 2), combo legend nearest
            // the keyboard (row 3).
            Grid.SetRow(StatusBar, 0);
            Grid.SetRow(EditBanner, 1);
            Grid.SetRow(LayerTabs, 2);
            Grid.SetRow(ComboLegend, 3);
            StatusBar.CornerRadius = new CornerRadius(8, 8, 0, 0);
        }
    }

    /// <summary>
    /// Edit-mode shortcuts. Tunnel routing so the window sees the press before
    /// the focused control does; text boxes keep their keys, and outside edit
    /// mode every press falls through untouched. The gesture table lives in
    /// <see cref="EditShortcuts"/>.
    /// </summary>
    private void OnEditKeyDown(object? sender, KeyEventArgs e)
    {
        // While the ring is open it owns the keyboard — its own tunnel handler
        // decides Esc/Enter/arrows, so the window's shortcuts must not act.
        if (_keyRingOpen) return;

        if (DataContext is not MainWindowViewModel { IsEditing: true } vm) return;
        if (e.Source is TextBox) return;

        var shortcut = EditShortcuts.Resolve(
            e.Key,
            e.KeyModifiers,
            PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control,
            e.PhysicalKey);

        switch (shortcut)
        {
            case EditShortcut.Undo:
                if (vm.Edit!.UndoCommand.CanExecute(null))
                    vm.Edit.UndoCommand.Execute(null);
                e.Handled = true;
                break;
            case EditShortcut.Redo:
                if (vm.Edit!.RedoCommand.CanExecute(null))
                    vm.Edit.RedoCommand.Execute(null);
                e.Handled = true;
                break;
            case EditShortcut.ClearToTransparent:
                vm.EditClearSelectedKey(false);
                e.Handled = true;
                break;
            case EditShortcut.ClearToNone:
                vm.EditClearSelectedKey(true);
                e.Handled = true;
                break;
            case EditShortcut.ClearSelection:
                vm.EditClearSelection();
                e.Handled = true;
                break;
            case EditShortcut.OpenKeyDialog:
                _ = vm.EditSelectedKeyAsync();
                e.Handled = true;
                break;
            case EditShortcut.OpenOtherKeyEditor:
                _ = vm.EditSelectedKeyAsync(otherEditor: true);
                e.Handled = true;
                break;
            case EditShortcut.NewLayer:
                if (vm.NewLayerCommand.CanExecute(null))
                    vm.NewLayerCommand.Execute(null);
                e.Handled = true;
                break;
            case EditShortcut.DuplicateLayer:
                if (vm.DuplicateShownLayerCommand.CanExecute(null))
                    vm.DuplicateShownLayerCommand.Execute(null);
                e.Handled = true;
                break;
            case EditShortcut.MoveLayerLeft:
                if (vm.MoveShownLayerLeftCommand.CanExecute(null))
                    vm.MoveShownLayerLeftCommand.Execute(null);
                e.Handled = true;
                break;
            case EditShortcut.MoveLayerRight:
                if (vm.MoveShownLayerRightCommand.CanExecute(null))
                    vm.MoveShownLayerRightCommand.Execute(null);
                e.Handled = true;
                break;
        }
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

    /// <summary>
    /// Handles window drag (bars area) and resize (edges).
    /// SystemDecorations="None" removes OS title bar and resize handles — we replace both here.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Handled) return;

        var pos = e.GetPosition(this);

        var edge = GetResizeEdge(pos);
        if (edge.HasValue) { BeginResizeDrag(edge.Value, e); return; }

        // Only drag from the bars background, not from interactive controls.
        // A disabled button is left out of hit testing (GetVisualsAt too), so
        // its press arrives with the bar as the source; check the button
        // bounds by geometry instead, or a click on a greyed-out button starts
        // a window drag (macOS then logs "Window move completed without
        // beginning").
        if (BarsPanel.GetVisualDescendants().OfType<Button>().Any(b =>
                b.IsEffectivelyVisible && new Rect(b.Bounds.Size).Contains(e.GetPosition(b))))
            return;

        var barsHeight = BarsPanel.Bounds.Height;
        bool inBars = _barsOnBottom == true
            ? pos.Y > Bounds.Height - barsHeight
            : pos.Y < barsHeight;

        if (inBars)
            BeginMoveDrag(e);
    }

    /// <summary>
    /// Updates the cursor as the pointer enters / leaves a resize zone so the
    /// otherwise-invisible 6–9 px hot zones are discoverable. Suppressed when
    /// the OS draws its own chrome (macOS, Windows transparency fallback).
    /// </summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (SystemDecorations == SystemDecorations.Full) return;

        var edge = GetResizeEdge(e.GetPosition(this));
        Cursor = edge switch
        {
            WindowEdge.North     or WindowEdge.South     => new Cursor(StandardCursorType.SizeNorthSouth),
            WindowEdge.West      or WindowEdge.East      => new Cursor(StandardCursorType.SizeWestEast),
            WindowEdge.NorthWest                         => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.NorthEast                         => new Cursor(StandardCursorType.TopRightCorner),
            WindowEdge.SouthWest                         => new Cursor(StandardCursorType.BottomLeftCorner),
            WindowEdge.SouthEast                         => new Cursor(StandardCursorType.BottomRightCorner),
            _                                            => Cursor.Default,
        };
    }

    /// <summary>
    /// Pointer enters a combo legend tile → ask the main VM to highlight
    /// just that combo's number. Symmetric with the board-side handler in
    /// <see cref="BoardView"/>.
    /// </summary>
    private void OnComboTilePointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not ComboViewModel combo) return;
        if (DataContext is not MainWindowViewModel vm) return;
        vm.SetHighlightedCombos(new[] { combo.Number });
    }

    private void OnComboTilePointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.SetHighlightedCombos(Array.Empty<int>());
    }

    /// <summary>
    /// The recents flyout opens → re-run the Exists check on the stored paths
    /// so a file deleted/renamed since the last open greys out immediately.
    /// (A binding alone can't turn the Flyout.Opened event into a command.)
    /// </summary>
    private void OnRecentKeymapsFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.RefreshRecentKeymapsCommand.Execute(null);
    }

    /// <summary>
    /// Any pick inside the recents flyout (Browse, a recent row, Clear list)
    /// dismisses it. The Hide is posted, not immediate: Button reads its
    /// Command only after Click returns, and hiding detaches the flyout from
    /// the logical tree, which would null the DataContext-bound Command first.
    /// </summary>
    private void OnKeymapMenuItemClick(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => LoadLayoutButton.Flyout?.Hide());
    }

    /// <summary>
    /// "My own board…" opens the layout wizard; like the recents flyout the
    /// Hide is posted so the button's Command still resolves when it runs
    /// after this handler returns.
    /// </summary>
    private void OnAddOwnBoardClick(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => KeyboardPickerButton.Flyout?.Hide());
    }

    /// <summary>
    /// "Edit layout…" opens the edit window for the active user layout; the
    /// Hide is posted for the same reason as above.
    /// </summary>
    private void OnEditLayoutClick(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => KeyboardPickerButton.Flyout?.Hide());
    }

    /// <summary>
    /// Right-click on a combo legend tile opens the combo-label editor. Editing
    /// is intentionally scoped to the legend only — on-key combo pills are
    /// already crowded with the number badge.
    /// </summary>
    private void OnComboTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is not ComboViewModel combo) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (!e.GetCurrentPoint(c).Properties.IsRightButtonPressed) return;
        vm.RequestEditCombo(combo.KeyPositionsKey);
        e.Handled = true;
    }

    private WindowEdge? GetResizeEdge(Point pos)
    {
        // `<` on the near edge and `>=` on the far edge keeps both hot zones
        // exactly ResizeEdge pixels wide. Using `>` symmetrically would leave
        // a 1px dead band at pos == Bounds.Width - ResizeEdge.
        bool left   = pos.X < ResizeEdge;
        bool right  = pos.X >= Bounds.Width - ResizeEdge;
        bool top    = pos.Y < ResizeEdge;
        bool bottom = pos.Y >= Bounds.Height - ResizeEdge;

        if (left  && top)    return WindowEdge.NorthWest;
        if (right && top)    return WindowEdge.NorthEast;
        if (left  && bottom) return WindowEdge.SouthWest;
        if (right && bottom) return WindowEdge.SouthEast;
        if (left)            return WindowEdge.West;
        if (right)           return WindowEdge.East;
        if (top)             return WindowEdge.North;
        if (bottom)          return WindowEdge.South;
        return null;
    }

    // ---- key ring ---------------------------------------------------------------

    /// <summary>True while <see cref="ShowKeyRingAsync"/> hosts the ring: edit
    /// shortcuts pause (the ring owns the keyboard) and a second request is
    /// ignored.</summary>
    private bool _keyRingOpen;

    /// <summary>Completes the currently open ring with how it ended; a scrim
    /// press writes a cancel through it.</summary>
    private TaskCompletionSource<KeyRingClosedEventArgs>? _keyRingCloseTcs;

    /// <summary>A click on the dimmed board cancels the ring. The ring's window
    /// normally covers the board and catches that click itself; this covers a
    /// board outside the screen's working area.</summary>
    private void OnKeyRingScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_keyRingOpen) return;
        _keyRingCloseTcs?.TrySetResult(new KeyRingClosedEventArgs(null, openInDialog: false));
        e.Handled = true;
    }

    /// <summary>
    /// Opens the key ring for <paramref name="request"/>: the board dims, the
    /// edited key gets a gold outline, and the ring opens on the key in a
    /// <see cref="KeyRingWindow"/> over the key's screen, so it is placed
    /// against the monitor, not this window. Answers the binding the ring
    /// saves, or null on a cancel (Esc, a click beside the ring, another app
    /// activated, this window resized). When the edit moves to the key dialog,
    /// or the screen is too small for the ring, answers what the dialog
    /// answers instead. Returns null immediately when a ring is already open.
    /// </summary>
    public async Task<ZmkKeyBinding?> ShowKeyRingAsync(KeyDialogRequest request)
    {
        if (_keyRingOpen) return null;
        _keyRingOpen = true;

        // Whatever happens on the way, the scrim comes down and the edit
        // shortcuts come back; otherwise one exception leaves the board
        // dimmed and the keyboard dead until restart.
        try
        {
            return await OpenKeyRingAsync(request);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("KeyRing", $"Key ring failed: {ex}");
            return null;
        }
        finally
        {
            _keyRingCloseTcs = null;
            KeyRingLayer.IsVisible = false;
            _keyRingOpen = false;
        }
    }

    private async Task<ZmkKeyBinding?> OpenKeyRingAsync(KeyDialogRequest request)
    {
        var dialogVm = new KeyDialogViewModel(request);
        var ringVm = new KeyRingViewModel(dialogVm, request);

        // A collapsed panel has no bounds, so show the layer before finding
        // the key in it.
        KeyRingLayer.IsVisible = true;
        UpdateLayout();
        var key = FindKeyRect(request.KeyIndex, KeyRingLayer.Bounds.Size);

        // The ring's window covers the working area of the key's screen;
        // screen positions are pixels, the window's own coordinates DIPs.
        var keyTopLeft = KeyRingLayer.PointToScreen(key.TopLeft);
        var keyBottomRight = KeyRingLayer.PointToScreen(key.BottomRight);
        var screen = Screens.ScreenFromPoint(KeyRingLayer.PointToScreen(key.Center))
                     ?? Screens.ScreenFromWindow(this)
                     ?? Screens.Primary;
        if (screen is null) return await ShowKeyDialogAsync(dialogVm);

        var work = screen.WorkingArea;
        var scaling = screen.Scaling;
        var area = new Size(work.Width / scaling, work.Height / scaling);
        var keyInWindow = new Rect(
            new Point((keyTopLeft.X - work.X) / scaling, (keyTopLeft.Y - work.Y) / scaling),
            new Point((keyBottomRight.X - work.X) / scaling, (keyBottomRight.Y - work.Y) / scaling));

        var placement = RingGeometry.Place(new Rect(area), keyInWindow.Center);
        if (!placement.Fits) return await ShowKeyDialogAsync(dialogVm);

        // The board stays dimmed behind the ring, its key outlined in gold.
        var outline = key.Inflate(3);
        Canvas.SetLeft(KeyRingKeyOutline, outline.X);
        Canvas.SetTop(KeyRingKeyOutline, outline.Y);
        KeyRingKeyOutline.Width = outline.Width;
        KeyRingKeyOutline.Height = outline.Height;

        var tcs = new TaskCompletionSource<KeyRingClosedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _keyRingCloseTcs = tcs;

        var ringWindow = new KeyRingWindow
        {
            Position = work.Position,
            Width = area.Width,
            Height = area.Height,
        };
        ringWindow.Present(ringVm, area, keyInWindow, placement);
        ringWindow.Finished += (_, closed) => tcs.TrySetResult(closed);

        // A resize moves the board under the outline: cancel.
        EventHandler<SizeChangedEventArgs> sizeCancel =
            (_, _) => tcs.TrySetResult(new KeyRingClosedEventArgs(null, openInDialog: false));
        SizeChanged += sizeCancel;

        KeyRingClosedEventArgs closed;
        try
        {
            ringWindow.Show(this);
            closed = await tcs.Task;
        }
        finally
        {
            SizeChanged -= sizeCancel;
            ringWindow.Close();
            KeyRingLayer.IsVisible = false;
        }

        // The same dialog VM, so a half-made edit carries over.
        if (closed.OpenInDialog) return await ShowKeyDialogAsync(dialogVm);

        Activate();
        return closed.Result;
    }

    /// <summary>The key dialog on the ring's own dialog view-model: the
    /// ring's "open in the key dialog" button, and the fallback when the
    /// screen is too small for the ring.</summary>
    private async Task<ZmkKeyBinding?> ShowKeyDialogAsync(KeyDialogViewModel dialogVm)
    {
        KeyRingLayer.IsVisible = false;
        try
        {
            return await new KeyDialog { DataContext = dialogVm }.ShowDialog<ZmkKeyBinding?>(this);
        }
        finally
        {
            Activate();
        }
    }

    /// <summary>
    /// The edited key's rotated bounding box in <see cref="KeyRingLayer"/>
    /// coordinates: the four corners of its template Grid through
    /// <c>TranslatePoint</c>, which folds in the rotation. A zero-size rect at
    /// the layer centre when the key is not on screen.
    /// </summary>
    private Rect FindKeyRect(int keyIndex, Size area)
    {
        // Pre-order traversal: the outermost matching Grid — the key template's
        // root, not the label boxes nested in it — comes first.
        var target = this.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(g => g.DataContext is KeyViewModel key && key.Position.Index == keyIndex);
        if (target is null)
            return new Rect(new Point(area.Width / 2, area.Height / 2), new Size(0, 0));

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var corner in new[]
                 {
                     new Point(0, 0),
                     new Point(target.Bounds.Width, 0),
                     new Point(target.Bounds.Width, target.Bounds.Height),
                     new Point(0, target.Bounds.Height),
                 })
        {
            if (target.TranslatePoint(corner, KeyRingLayer) is not { } p) continue;
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        return minX <= maxX && minY <= maxY
            ? new Rect(minX, minY, maxX - minX, maxY - minY)
            : new Rect(new Point(area.Width / 2, area.Height / 2), new Size(0, 0));
    }
}
