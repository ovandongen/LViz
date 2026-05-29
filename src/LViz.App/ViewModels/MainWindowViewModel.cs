using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.MouseIdle;
using LViz.Core.Diagnostics;
using LViz.Core.Input;
using LViz.Core.Keymap;
using LViz.Core.Layout;
using LViz.Core.Models;
using LViz.Core.Settings;
using ZmkHidProtocol.ActiveWindow;
using ZmkHidProtocol.Protocol;

namespace LViz.App.ViewModels;

/// <summary>
/// Top-level view model for <c>MainWindow</c>. Owns the loaded keymap, the
/// active layer, the live-key tracker, and persists the user's choice of
/// keyboard + last-loaded JSON path.
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IBoardSurface
{
    /// <summary>
    /// Always null on the main VM — the main window's BoardView is read-only.
    /// The picker VM (<see cref="ExitKeyPickerViewModel"/>) provides a real
    /// handler so taps register selections.
    /// </summary>
    public Action<int>? OnKeyTapped => null;

    /// <summary>
    /// Right-click on a key opens the per-key label-override editor. Wired
    /// up so the View can stay binding-only; the actual dialog launch is a
    /// callback into <c>App.axaml.cs</c> (see <see cref="EditKeyLabelRequested"/>)
    /// because the View can't construct a window directly without leaking
    /// Avalonia.Controls into the VM.
    /// </summary>
    public Action<int>? OnKeyRightTapped => OnKeyRightTappedInternal;

    private void OnKeyRightTappedInternal(int keyIndex) =>
        EditKeyLabelRequested?.Invoke(keyIndex, ActiveLayerIndex);

    /// <summary>
    /// Invoked when the user right-clicks a key. The callback (set by
    /// <c>App.axaml.cs</c>) owns the modal dialog lifecycle and calls
    /// <see cref="SetKeyLabelOverride"/> on completion. Parameters:
    /// (keyIndex, activeLayerIndex).
    /// </summary>
    public Action<int, int>? EditKeyLabelRequested { get; set; }

    /// <summary>
    /// Invoked when the user right-clicks a combo legend tile. Callback (set
    /// by <c>App.axaml.cs</c>) owns the modal-dialog lifecycle and calls
    /// <see cref="SetComboLabelOverride"/> on save.
    /// </summary>
    public Action<string>? EditComboLabelRequested { get; set; }

    public void RequestEditCombo(string keyPositionsKey) =>
        EditComboLabelRequested?.Invoke(keyPositionsKey);

    private readonly ISettingsService _settingsService;

    // Owns the loaded keymap + resolution. Null Current means no layout loaded
    // (callers treat that as "every binding is Transparent").
    private readonly IKeymapStateService _keymap;

    private IKeyboardProfile _profile;
    // Last successful load's status text. Null when the current StatusMessage
    // is something else (error, transient, etc).
    private string? _loadStatusBase;

    private readonly IHidPipeline _hid;
    private readonly ILayerPushCoordinator _push;

    // Press-highlight pipeline: per-layer (mod-set + keycode) → KeyViewModel
    // lookup, held-modifier set, modifier-grace deferral, per-key pulse.
    // Built lazily once the Keys collection is populated.
    private IKeyHighlightTracker? _highlightTracker;

    // --- UI-bindable state ---
    /// <summary>
    /// Transient/changing status text shown in the center of the top bar
    /// ("Switched to GO60", "Load error: …"). The persistent loaded-layout
    /// info lives in <see cref="LoadInfoTooltip"/>, surfaced via the
    /// InfoButton's hover tooltip.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>
    /// Hover tooltip for the InfoButton: persistent "Loaded foo.json (N layers) · keyboard hint",
    /// or "No layout loaded · keyboard hint" when nothing is loaded.
    /// </summary>
    public string LoadInfoTooltip =>
        $"{_loadStatusBase ?? Loc.Instance["Status_NoLayoutLoaded"]}  ·  {KeyboardStatusHint}";

    // Single write site for _loadStatusBase so the InfoButton tooltip refreshes
    // whenever the persistent loaded-info changes.
    private void SetLoadStatusBase(string? value)
    {
        _loadStatusBase = value;
        OnPropertyChanged(nameof(LoadInfoTooltip));
    }

    /// <summary>
    /// Suffix appended to the keyboard status hint describing the HID source
    /// state ("via Raw HID (Go60 Left)"). Empty until the coordinator has a
    /// connected source.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyboardStatusHint))]
    [NotifyPropertyChangedFor(nameof(LoadInfoTooltip))]
    private string _layerSourceHint = "";

    /// <summary>True while the HID source is connected.</summary>
    [ObservableProperty] private bool _isHidSourceActive;

    /// <summary>App-rules settings tab: hidden on Linux (no active-window API) and when HID is disconnected.</summary>
    public bool IsAppRulesTabVisible => !OperatingSystem.IsLinux() && IsHidSourceActive;

    partial void OnIsHidSourceActiveChanged(bool value) => OnPropertyChanged(nameof(IsAppRulesTabVisible));

    [ObservableProperty] private bool _isAlwaysOnTop;
    [ObservableProperty] private bool _colorTrayIconByActiveLayer;
    [ObservableProperty] private bool _isLiveHighlightingEnabled;
    [ObservableProperty] private bool _isAutoLayerSwitchEnabled;
    [ObservableProperty] private bool _hasLayoutLoaded;

    /// <summary>Transient auto-dismissing toast banner (load errors, etc.).</summary>
    public ToastController Toast { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveLayerTintColor))]
    private int _activeLayerIndex;

    /// <summary>Palette color for the active layer — used as the press-highlight pulse fill.</summary>
    public string ActiveLayerTintColor => LayerColorPalette.GetColor(_profile.Id, ActiveLayerIndex);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoardBackground))]
    [NotifyPropertyChangedFor(nameof(TabBackground))]
    private double _backgroundOpacity;

    // Emitted in CSS-style #RRGGBBAA so HexColorToBrushConverter swaps the
    // alpha to the front for Avalonia's #AARRGGBB. The base color matches
    // svalboard's port — kept identical so a fully-solid slider lands on the
    // same dark plum the original UI used.
    public string BoardBackground => $"{AppTheme.BgCrustHex}{(int)(BackgroundOpacity * 255):X2}";

    // Tabs always retain at least 40% alpha so their text stays readable
    // even at slider 0; svalboard's formula, ported verbatim.
    public string TabBackground
    {
        get
        {
            const int baseAlpha = 0x66;
            var alpha = Math.Min(255, baseAlpha + (int)(BackgroundOpacity * (255 - baseAlpha)));
            return $"{AppTheme.BgCrustHex}{alpha:X2}";
        }
    }

    partial void OnBackgroundOpacityChanged(double value) =>
        PersistSetting(s => s with { BackgroundOpacity = value });

    partial void OnColorTrayIconByActiveLayerChanged(bool value) =>
        PersistSetting(s => s with { ColorTrayIconByActiveLayer = value });

    /// <summary>Canvas geometry + stacked-mode offsets for the board render
    /// surface, extracted into its own VM. Bound by BoardView and the settings
    /// window; owns the persisted stacked-layout toggles.</summary>
    public BoardLayoutViewModel BoardLayout { get; }

    /// <summary>Press-highlight fill/stroke colors for the per-key press dot,
    /// extracted into its own VM. Owns the persisted highlight color.</summary>
    public BoardStyleViewModel BoardStyle { get; }

    /// <summary>
    /// Global show/hide hotkey key name (e.g. "F12"). Modifier handling
    /// lives in <see cref="UserSettings.HotkeyModifiers"/> and isn't
    /// user-editable today. Changing this raises <see cref="HotkeyKeyChanged"/>
    /// so the live <see cref="Services.IGlobalHotkeyService"/> rewires
    /// without restart.
    /// </summary>
    [ObservableProperty]
    private string _hotkeyKey = "F12";

    partial void OnHotkeyKeyChanged(string value)
    {
        PersistSetting(s => s with { HotkeyKey = value });
        HotkeyKeyChanged?.Invoke(value);
    }

    public event Action<string>? HotkeyKeyChanged;

    /// <summary>Read-through to the persisted modifier name. Not user-editable today; keeps the hotkey-rewire call site in App self-contained.</summary>
    public string HotkeyModifiers => _settingsService.Load().HotkeyModifiers;

    /// <summary>
    /// Sets or clears a per-layer color override for the currently active
    /// keyboard profile. Persists to <see cref="UserSettings.LayerColors"/>,
    /// updates the static palette, and refreshes both the layer-tab swatches
    /// (in place) and every key fill on the board.
    /// </summary>
    public void SetLayerColorOverride(int layerIndex, string? hex)
    {
        var profileId = _profile.Id;
        LayerColorPalette.SetOverride(profileId, layerIndex, hex);

        PersistSetting(s => string.IsNullOrWhiteSpace(hex)
            ? s with { LayerColors = PerProfile.RemoveInner(s.LayerColors, profileId, layerIndex) }
            : s with { LayerColors = PerProfile.SetInner(s.LayerColors, profileId, layerIndex, hex!) });

        // Repaint tab swatches in place (re-creating Layers would steal selection focus).
        foreach (var layer in Layers)
            layer.TabColor = LayerColorPalette.GetColor(profileId, layer.Index);
        // Re-resolve every key's fill — &lt / &mo keys reference arbitrary
        // layer colors, so changing layer 2's tint repaints layer 0's view too.
        if (_keymap.HasLayout)
            ApplyActiveLayer(ActiveLayerIndex);
        OnPropertyChanged(nameof(ActiveLayerTintColor));
    }

    /// <summary>
    /// Sets or clears a per-(layer, key) label override for the currently
    /// active keyboard profile. Persists to
    /// <see cref="UserSettings.KeyLabelOverrides"/> through the same
    /// PersistSetting → SettingsService.Save → AtomicFile chain
    /// <see cref="SetLayerColorOverride"/> uses, then re-applies the active
    /// layer so the affected key repaints. <c>null</c> or
    /// <see cref="KeyLabelOverride.IsEmpty"/> removes the entry.
    /// </summary>
    public void SetKeyLabelOverride(int layerIndex, int keyIndex, KeyLabelOverride? value)
    {
        KeyLabelOverrides.Set(_profile.Id, layerIndex, keyIndex, value);
        PersistSetting(s => s with { KeyLabelOverrides = KeyLabelOverrides.Snapshot() });

        if (_keymap.HasLayout && layerIndex == ActiveLayerIndex)
            ApplyActiveLayer(ActiveLayerIndex);
    }

    /// <summary>
    /// Sets or clears a combo-pill label override for the active profile, then
    /// rebuilds the combo VM list so the affected pill repaints.
    /// </summary>
    public void SetComboLabelOverride(string keyPositionsKey, ComboLabelOverride? value)
    {
        ComboLabelOverrides.Set(_profile.Id, keyPositionsKey, value);
        PersistSetting(s => s with { ComboLabelOverrides = ComboLabelOverrides.Snapshot() });
        BuildCombosForConfig();
    }

    /// <summary>
    /// Snapshot of the default-computed label + current override (if any) for
    /// the combo identified by <paramref name="keyPositionsKey"/>. Returns null
    /// when no combo matches the key (defensive — the View only invokes this
    /// for an existing ComboViewModel).
    /// </summary>
    public (int Number, string DefaultLabel, string ParticipatingKeys, ComboLabelOverride? Existing)?
        GetComboLabelEditContext(string keyPositionsKey)
    {
        var combo = Combos.FirstOrDefault(c => c.KeyPositionsKey == keyPositionsKey);
        if (combo is null) return null;
        var existing = ComboLabelOverrides.Get(_profile.Id, keyPositionsKey);
        return (combo.Number, combo.DefaultLabel, combo.ParticipatingKeysText, existing);
    }

    /// <summary>
    /// Snapshot of the formatter-computed default text + the current override
    /// (if any) for the given (layer, key) slot — used by
    /// <see cref="Views.EditKeyLabelDialog"/> to seed the editor and show a
    /// "current default looks like…" hint. Returns null when the slot is out
    /// of range.
    /// </summary>
    public (string DefaultHint, KeyLabelOverride? Existing)? GetKeyLabelEditContext(int layerIndex, int keyIndex)
    {
        var km = _keymap.Current;
        if (km is null) return null;
        if (layerIndex < 0 || layerIndex >= km.LayerCount) return null;
        if (keyIndex < 0 || keyIndex >= Keys.Count) return null;

        var r = km.Resolve(layerIndex, keyIndex);
        var (label, sub, top) = KeyLabelFormatter.FormatBinding(r.Binding, r.TargetLayerName, r.HoldTap, km.MacrosByName);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(top)) parts.Add($"[{top}]");
        if (!string.IsNullOrEmpty(label)) parts.Add(label);
        if (!string.IsNullOrEmpty(sub)) parts.Add($"({sub})");
        var hint = parts.Count > 0 ? string.Join(" ", parts) : r.Binding.Display;

        var existing = KeyLabelOverrides.Get(_profile.Id, layerIndex, keyIndex);
        return (hint, existing);
    }

    public ObservableCollection<KeyViewModel> Keys { get; } = new();
    /// <summary>Left-hand subset of <see cref="Keys"/>. Bound separately so the stacked-layout renderer can translate the half independently.</summary>
    public ObservableCollection<KeyViewModel> LeftKeys { get; } = new();
    /// <summary>Right-hand subset of <see cref="Keys"/>. Bound separately so the stacked-layout renderer can translate the half independently.</summary>
    public ObservableCollection<KeyViewModel> RightKeys { get; } = new();
    public ObservableCollection<LayerViewModel> Layers { get; } = new();

    /// <summary>
    /// True when the board renders Windows-style modifier glyphs (⊞ Alt Ctrl ⇧)
    /// instead of the default Mac set (⌘ ⌥ ⌃ ⇪). User-controlled via the
    /// toolbar; persisted across launches as <see cref="UserSettings.ModifierStyle"/>.
    /// Backed by the static <see cref="ZmkKeycodeLabel.CurrentModifierStyle"/>
    /// which every label-builder reads at render time.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModifierStyleIconData))]
    private bool _isWindowsModifierStyle;

    /// <summary>
    /// SVG path data for the toolbar button's icon: Apple silhouette when the
    /// Mac style is active, four-square Windows logo when the Windows style is
    /// active. Both paths are designed against a 24×24 viewBox so PathIcon
    /// scales them like the rest of the toolbar icons.
    /// </summary>
    public string ModifierStyleIconData => IsWindowsModifierStyle
        ? "M3 5.479L10.768 4.5v8.385H3V5.479zM11.232 4.5L21 3v9.885h-9.768V4.5zM3 13.115h7.768V21.5L3 20.521V13.115zM11.232 13.115H21V22.5l-9.768-1.385V13.115z"
        : "M17.05 20.28c-.98.95-2.05.8-3.08.35-1.09-.46-2.09-.48-3.24 0-1.44.62-2.2.44-3.06-.35C2.79 15.25 3.51 7.59 9.05 7.31c1.35.07 2.29.74 3.08.8 1.18-.24 2.31-.93 3.57-.84 1.51.12 2.65.72 3.4 1.8-3.12 1.87-2.38 5.98.48 7.13-.57 1.5-1.31 2.99-2.54 4.09zM12.03 7.25c-.15-2.23 1.66-4.07 3.74-4.25.29 2.58-2.34 4.5-3.74 4.25z";

    partial void OnIsWindowsModifierStyleChanged(bool value)
    {
        ZmkKeycodeLabel.CurrentModifierStyle = value ? ModifierStyle.Windows : ModifierStyle.Mac;
        PersistSetting(s => s with { ModifierStyle = value ? "Windows" : "Mac" });
        // Same redraw hook SetLayerColorOverride uses: rebuilds every key's
        // labels against the freshly-set static.
        if (_keymap.HasLayout) ApplyActiveLayer(ActiveLayerIndex);
    }

    [RelayCommand]
    private void ToggleWindowsModifierStyle() => IsWindowsModifierStyle = !IsWindowsModifierStyle;

    /// <summary>
    /// When true, combo keys show numbered indicators (e.g. <c>"1"</c>,
    /// <c>"1,3"</c>) instead of the static earmark, and the combo legend is
    /// visible beneath the keyboard. Persisted across launches.
    /// </summary>
    [ObservableProperty]
    private bool _isComboOverlayVisible;

    partial void OnIsComboOverlayVisibleChanged(bool value) =>
        PersistSetting(s => s with { ComboOverlayVisible = value });

    [RelayCommand]
    private void ToggleComboOverlay() => IsComboOverlayVisible = !IsComboOverlayVisible;

    /// <summary>
    /// Every combo declared in the loaded keymap, numbered sequentially
    /// in source-file order starting at 1. Not layer-scoped — combos
    /// surface on every layer view so the user can see what chords exist
    /// even when sitting on a layer where the combo doesn't fire.
    /// Repopulated by <see cref="ApplyActiveLayer"/> on layout reload.
    /// </summary>
    public ObservableCollection<ComboViewModel> Combos { get; } = new();

    /// <summary>
    /// Sets the currently-highlighted combo numbers. Pass empty to clear.
    /// Idempotent and cheap to call from PointerEntered / PointerExited.
    ///
    /// Drives three pieces of state in lockstep: the legend tile's
    /// <c>IsHighlighted</c>, each on-key pill's <c>IsHighlighted</c>, and —
    /// for every participating key (including ones whose pill was capped
    /// out by <c>MaxComboBadges</c>) — the per-key
    /// <c>IsComboKeyHighlighted</c> flag that re-uses the existing
    /// press-dot visual via <c>IsAnyHighlight</c>. That way hovering a
    /// combo lights the same dot a live HID press would, no extra
    /// indicator.
    /// </summary>
    public void SetHighlightedCombos(IReadOnlyCollection<int> numbers)
    {
        numbers ??= Array.Empty<int>();
        var numberSet = numbers as ISet<int> ?? new HashSet<int>(numbers);

        for (var i = 0; i < Combos.Count; i++)
            Combos[i].IsHighlighted = numberSet.Contains(Combos[i].Number);

        foreach (var key in Keys)
        {
            key.IsComboKeyHighlighted = false;
            foreach (var badge in key.ComboBadges)
                badge.IsHighlighted = numberSet.Contains(badge.Number);
        }

        foreach (var n in numberSet)
        {
            if (n < 1 || n > Combos.Count) continue;
            foreach (var idx in Combos[n - 1].KeyIndices)
                if (idx >= 0 && idx < Keys.Count)
                    Keys[idx].IsComboKeyHighlighted = true;
        }
    }

    /// <summary>All keyboard profiles the user can switch between, for the picker flyout.</summary>
    public IReadOnlyList<IKeyboardProfile> AvailableKeyboards => KeyboardProfileRegistry.All;

    /// <summary>
    /// Currently selected profile. Bound to the picker button label and drives
    /// checkmark selection in the flyout.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyboardStatusHint))]
    [NotifyPropertyChangedFor(nameof(LoadInfoTooltip))]
    [NotifyPropertyChangedFor(nameof(ActiveLayerTintColor))]
    private IKeyboardProfile _selectedKeyboard = null!;

    /// <summary>Right-aligned status-bar hint: "DisplayName · N keys" with layer-source suffix when known.</summary>
    public string KeyboardStatusHint
    {
        get
        {
            var core = Loc.Instance.Format("Status_KeyboardHintFormat",
                SelectedKeyboard?.DisplayName ?? "", SelectedKeyboard?.KeyCount ?? 0);
            return string.IsNullOrEmpty(LayerSourceHint) ? core : $"{core}  ·  {LayerSourceHint}";
        }
    }

    // --- Callbacks set by App.axaml.cs to bridge to the Window ---
    public Action? QuitRequested { get; set; }
    public Action? ShowWindowRequested { get; set; }
    public Action? ToggleWindowRequested { get; set; }
    public Func<Task>? LoadLayoutRequested { get; set; }
    public Func<Task>? CopyDiagnosticsRequested { get; set; }
    public Action? OpenSettingsRequested { get; set; }

    /// <summary>Path of the layout JSON the user currently has loaded, or null if none.</summary>
    public string? LoadedLayoutPath => _keymap.LoadedPath;

    // --- Commands ---
    public IRelayCommand QuitCommand { get; }
    public IRelayCommand ShowCommand { get; }
    public IRelayCommand LoadLayoutCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand TogglePinCommand { get; }
    public IRelayCommand ToggleLiveHighlightingCommand { get; }
    public IRelayCommand OpenLogFolderCommand { get; }
    public IRelayCommand CopyDiagnosticsCommand { get; }
    public IRelayCommand<IKeyboardProfile> SelectKeyboardCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }

    /// <summary>
    /// The app→layer auto-switch engine, exposed directly. The settings VM and
    /// window bind straight to its observable state — the engine is already an
    /// ObservableObject and everything here is App-layer, so the old
    /// pass-through facade (and the PropertyChanged relay that fed it) bought
    /// nothing but a redundant hop.
    /// </summary>
    public AutoSwitchEngine AutoSwitch => _push.AutoSwitch;

    /// <summary>
    /// The mouse-movement → layer push engine, exposed directly (mirrors
    /// <see cref="AutoSwitch"/>). The settings VM reads its
    /// <see cref="IMouseLayerEngine.CurrentSettings"/> and calls
    /// <see cref="IMouseLayerEngine.ApplySettings"/> on it directly — no MainVM
    /// pass-through facade. Null when no mouse-idle monitor is wired up (tests);
    /// callers fall back to the persisted per-profile settings in that case.
    /// </summary>
    public IMouseLayerEngine? MouseLayer => _push.MouseLayer;

    public MainWindowViewModel(
        ISettingsService settingsService,
        IActiveWindowMonitor? activeWindowMonitor = null,
        IMouseIdleMonitor? mouseIdleMonitor = null,
        IKeymapStateService? keymapState = null)
    {
        _settingsService = settingsService;
        _keymap = keymapState ?? new KeymapStateService();
        var s = settingsService.Load();
        _profile = KeyboardProfileRegistry.TryResolve(s.Keyboard, out var p) ? p : new CorneProfile();
        _selectedKeyboard = _profile;
        _isAlwaysOnTop = s.AlwaysOnTop;
        _colorTrayIconByActiveLayer = s.ColorTrayIconByActiveLayer;
        _isLiveHighlightingEnabled = s.LiveKeyHighlighting;
        _isAutoLayerSwitchEnabled = s.AutoLayerSwitch;
        _backgroundOpacity = Math.Clamp(s.BackgroundOpacity, 0.0, 1.0);
        if (!string.IsNullOrWhiteSpace(s.HotkeyKey))
            _hotkeyKey = s.HotkeyKey;
        _isComboOverlayVisible = s.ComboOverlayVisible;

        // Board geometry + press-style live in their own VMs now; seed them from
        // the same settings load. Each owns its own persistence (StackedLayout /
        // StackedTopHand / PressHighlightColor) via the injected settings service.
        BoardLayout = new BoardLayoutViewModel(
            _profile, settingsService, s.StackedLayout,
            string.IsNullOrWhiteSpace(s.StackedTopHand) ? "Left" : s.StackedTopHand);
        BoardStyle = new BoardStyleViewModel(
            string.IsNullOrWhiteSpace(s.PressHighlightColor)
                ? AppTheme.PressHighlightDefaultHex
                : s.PressHighlightColor,
            settingsService);
        _isWindowsModifierStyle = string.Equals(s.ModifierStyle, "Windows", StringComparison.OrdinalIgnoreCase);
        // Seed the static *before* the first ApplyActiveLayer so initial render
        // already uses the persisted glyph set (the partial change handler is
        // not invoked when the backing field is assigned directly).
        ZmkKeycodeLabel.CurrentModifierStyle = _isWindowsModifierStyle ? ModifierStyle.Windows : ModifierStyle.Mac;
        // Seed the static palette with persisted per-keyboard, per-layer overrides
        // so the very first paint already reflects the user's customization.
        LayerColorPalette.SetOverrides(s.LayerColors);
        KeyLabelOverrides.SetOverrides(s.KeyLabelOverrides);
        ComboLabelOverrides.SetOverrides(s.ComboLabelOverrides);

        // HID stack owns the layer source, command sender, and state tracker.
        // Constructed eagerly so push surfaces are available before Start();
        // the underlying RawHidLayerSource doesn't open the device until
        // Start() runs (triggered by ToggleLiveHighlighting).
        _hid = new HidPipeline(_profile);
        _hid.ActiveLayerChanged += OnActiveLayerChanged;
        _hid.ConnectionChanged += OnActiveSourceChanged;

        // Push coordinator owns AutoSwitch + MouseLayer and the handoff between
        // them. Routes both engines' pushes through _hid; redirects AutoSwitch
        // pushes into MouseLayer's revert target while a mouse push is in
        // flight. Forwards HID key-position events into AutoSwitch's exit-tap
        // detector and re-raises them for the highlight tracker below.
        _push = new LayerPushCoordinator(
            _hid,
            getRenderedLayer: () => ActiveLayerIndex,
            _profile,
            settingsService,
            activeWindowMonitor,
            mouseIdleMonitor);
        _push.KeyPositionForUi += OnKeyPositionForHighlight;
        // Transparent-key exit predicate reads the currently-loaded config at
        // call time, so swapping layouts or profiles needs no re-binding.
        _push.SetTransparencyPredicate(_keymap.ClassifyBinding);
        // The settings VM subscribes to _push.AutoSwitch.PropertyChanged
        // directly (via the AutoSwitch property), so no relay is needed here.

        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke());
        ShowCommand = new RelayCommand(() => ShowWindowRequested?.Invoke());
        LoadLayoutCommand = new AsyncRelayCommand(async () =>
        {
            if (LoadLayoutRequested is not null) await LoadLayoutRequested();
        });
        RefreshCommand = new RelayCommand(() =>
        {
            var paths = _settingsService.Load().LayoutJsonPaths;
            if (paths.TryGetValue(_profile.Id, out var path) && !string.IsNullOrEmpty(path))
                LoadLayoutFromPath(path);
        });
        TogglePinCommand = new RelayCommand(() =>
        {
            IsAlwaysOnTop = !IsAlwaysOnTop;
            PersistSetting(s2 => s2 with { AlwaysOnTop = IsAlwaysOnTop });
        });
        ToggleLiveHighlightingCommand = new RelayCommand(ToggleLiveHighlighting);
        OpenLogFolderCommand = new RelayCommand(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = DiagnosticLog.GetLogDirectory(),
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                SetLoadStatusBase(null);
                StatusMessage = $"Could not open log folder: {ex.Message}";
            }
        });
        CopyDiagnosticsCommand = new AsyncRelayCommand(async () =>
        {
            if (CopyDiagnosticsRequested is not null) await CopyDiagnosticsRequested();
        });
        SelectKeyboardCommand = new RelayCommand<IKeyboardProfile>(SelectKeyboard);
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke());

        BuildKeysFromProfile();
    }

    /// <summary>
    /// Post-startup entry point: load last-used layout, spin up the key-event
    /// hook, and set an opening status message.
    /// </summary>
    public void InitializeAsync()
    {
        var s = _settingsService.Load();
        if (TryGetStoredPathForProfile(s, _profile.Id, out var storedPath))
        {
            LoadLayoutFromPath(storedPath);
        }
        else
        {
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance["Status_NoLayoutLoaded"];
        }

        if (IsLiveHighlightingEnabled)
            StartKeyEventTracking();
    }

    public void LoadLayoutFromPath(string path)
    {
        // Parse + commit happens first and is the only fallible step. On failure
        // nothing else runs, so the profile / settings are never half-mutated.
        var result = _keymap.Load(path, _profile.Id);
        if (result is not KeymapLoadResult.Success loaded)
        {
            var error = ((KeymapLoadResult.Failure)result).Error;
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_LoadErrorFormat", error.Message);
            DiagnosticLog.Error("MainVM", $"Load failed: {error}");
            Toast.Show(Loc.Instance.Format("Toast_LoadFailedFormat", Path.GetFileName(path), error.Message));
            return;
        }

        var bindingCount = loaded.FirstLayerBindingCount;
        var keyCountMismatch = loaded.LayerCount > 0 && bindingCount != _profile.KeyCount;
        IKeyboardProfile? autoSwitchedTo = null;
        if (keyCountMismatch)
        {
            var matching = KeyboardProfileRegistry.All
                .FirstOrDefault(p => p.KeyCount == bindingCount);
            if (matching is not null)
            {
                _profile = matching;
                SelectedKeyboard = matching;
                BuildKeysFromProfile();
                PersistSetting(s => s with { Keyboard = matching.Id });
                // Re-scope HID discovery — see SelectKeyboard for context.
                _hid.SetActiveProfile(matching);
                autoSwitchedTo = matching;
                DiagnosticLog.Info("MainVM",
                    $"Auto-switched profile to {matching.Id} ({bindingCount} keys) on load");
            }
            else
            {
                DiagnosticLog.Warn("MainVM",
                    $"Loaded layout has {bindingCount} keys but no profile matches; staying on {_profile.Id}");
            }
        }

        RebuildLayers();
        ApplyActiveLayer(0);
        BuildCombosForConfig();
        HasLayoutLoaded = true;

        PersistSetting(s =>
        {
            var paths = new Dictionary<string, string>(s.LayoutJsonPaths) { [_profile.Id] = path };
            return s with { LayoutJsonPaths = paths };
        });

        var baseMsg = Loc.Instance.Format("Status_Loaded",
            Path.GetFileName(path), loaded.LayerCount);
        if (autoSwitchedTo is not null)
        {
            baseMsg += " — " + Loc.Instance.Format("Status_AutoSwitchedKeyboard",
                autoSwitchedTo.DisplayName);
        }
        else if (keyCountMismatch)
        {
            baseMsg += " — " + Loc.Instance.Format("Status_LoadKeyCountMismatch",
                bindingCount, _profile.DisplayName, _profile.KeyCount);
        }
        SetLoadStatusBase(baseMsg);
        StatusMessage = baseMsg;
        DiagnosticLog.Info("MainVM", $"Loaded '{path}'");
    }

    /// <summary>
    /// Builds a snapshot of runtime state (active settings, loaded layout,
    /// last load error) for inclusion in
    /// <see cref="DiagnosticLog.CollectDiagnosticReport"/>.
    /// </summary>
    public string BuildDiagnosticsSnapshot()
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Active Settings ---");
        try
        {
            var settings = _settingsService.Load();
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            sb.AppendLine(json);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(could not serialize settings: {ex.Message})");
        }
        sb.AppendLine();

        sb.AppendLine("--- Active Keyboard ---");
        sb.AppendLine($"Profile: {_profile.DisplayName} ({_profile.Id}), {_profile.KeyCount} keys");
        sb.AppendLine($"Loaded layout: {_keymap.LoadedPath ?? "(none)"}");
        sb.AppendLine($"Last load error: {_keymap.LastLoadError ?? "(none)"}");

        return sb.ToString();
    }

    /// <summary>Gracefully stops live tracking; idempotent. Called on window close / quit.</summary>
    public void Shutdown()
    {
        StopKeyEventTracking();
        _push.Shutdown();
        _push.Dispose();
        _hid.Dispose();
    }

    // --- Internals ---

    private void BuildKeysFromProfile()
    {
        Keys.Clear();
        LeftKeys.Clear();
        RightKeys.Clear();

        foreach (var pos in _profile.Keys)
        {
            var vm = new KeyViewModel(pos);
            Keys.Add(vm);
            (pos.Hand == Hand.Left ? LeftKeys : RightKeys).Add(vm);
        }

        // The board-layout VM owns the per-hand bounds + geometry; hand it the
        // active profile so it recomputes and raises its own geometry changes.
        BoardLayout.SetProfile(_profile);
    }

    private void SelectKeyboard(IKeyboardProfile? profile)
    {
        if (profile is null) return;
        if (string.Equals(profile.Id, _profile.Id, StringComparison.OrdinalIgnoreCase)) return;

        _profile = profile;
        SelectedKeyboard = profile;
        BuildKeysFromProfile();
        _push.SetActiveProfile(profile);

        var km = _keymap.Current;
        var layoutFits = km is not null
            && km.LayerCount > 0
            && km.Layers[0].Bindings.Count == profile.KeyCount;

        if (km is not null && !layoutFits)
        {
            _keymap.Clear();
            Layers.Clear();
            ActiveLayerIndex = 0;
            HasLayoutLoaded = false;
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitchedUnloaded", profile.DisplayName);
        }
        else if (km is not null)
        {
            ApplyActiveLayer(ActiveLayerIndex);
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitched", profile.DisplayName);
        }
        else
        {
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitched", profile.DisplayName);
        }

        PersistSetting(s => s with { Keyboard = profile.Id });
        DiagnosticLog.Info("MainVM", $"Keyboard profile switched to {profile.Id}");

        // Re-scope HID discovery to the new profile so a Go60 stops feeding
        // reports into a Glove80 layout (or vice versa). No-op when HID is
        // disabled or the source isn't running.
        _hid.SetActiveProfile(profile);

        // Auto-load whichever JSON the user last associated with this keyboard.
        // If the previously-loaded layout already fits, leave it alone.
        if (!HasLayoutLoaded
            && TryGetStoredPathForProfile(_settingsService.Load(), profile.Id, out var storedPath))
        {
            LoadLayoutFromPath(storedPath);
        }
    }

    /// <summary>
    /// Returns the persisted JSON path for the given profile if one exists and
    /// the file is still on disk. If the entry points at a file that has gone
    /// missing, logs a warning and removes the stale entry from settings so
    /// startup doesn't keep complaining about it.
    /// </summary>
    private bool TryGetStoredPathForProfile(UserSettings s, string profileId, out string path)
    {
        path = "";
        if (!s.LayoutJsonPaths.TryGetValue(profileId, out var stored) || string.IsNullOrWhiteSpace(stored))
            return false;
        if (File.Exists(stored))
        {
            path = stored;
            return true;
        }

        DiagnosticLog.Warn("MainVM", $"Stored layout for {profileId} is missing on disk: {stored}");
        PersistSetting(curr =>
        {
            var paths = new Dictionary<string, string>(curr.LayoutJsonPaths);
            paths.Remove(profileId);
            return curr with { LayoutJsonPaths = paths };
        });
        return false;
    }

    private void RebuildLayers()
    {
        Layers.Clear();
        var km = _keymap.Current;
        if (km is null) return;
        foreach (var layer in km.Layers)
        {
            Layers.Add(new LayerViewModel(
                layer.Index,
                $"{layer.Index} : {layer.Name}",
                LayerColorPalette.GetColor(_profile.Id, layer.Index),
                SelectLayer));
        }
    }

    private void SelectLayer(int index)
    {
        ApplyActiveLayer(index);
    }

    /// <summary>Routes a layer push through the HID pipeline. Test-tab plumbing for SettingsViewModel.</summary>
    public void PushLayerToKeyboard(int index) => _hid.PushLayer(index);

    /// <summary>
    /// Sends a 0xFD GetDeviceInfo request and awaits the 0xFE reply.
    /// Returns null when no HID is connected, on timeout, or on transport
    /// failure. Test-tab plumbing only.
    /// </summary>
    public Task<DeviceInfo?> QueryDeviceInfoAsync(TimeSpan timeout, CancellationToken ct) =>
        _hid.QueryDeviceInfoAsync(timeout, ct);

    /// <summary>
    /// Sends a 0xFB GetConfigId request and awaits the 0xFA reply.
    /// Returns null when no HID is connected, on timeout, or on transport
    /// failure. Empty string means the firmware has no
    /// CONFIG_HID_VIZ_CONFIG_ID set. Test-tab plumbing only.
    /// </summary>
    public Task<string?> QueryConfigIdAsync(TimeSpan timeout, CancellationToken ct) =>
        _hid.QueryConfigIdAsync(timeout, ct);

    private void ApplyActiveLayer(int index)
    {
        var km = _keymap.Current;
        if (km is null) return;
        if (index < 0 || index >= km.LayerCount) return;

        ActiveLayerIndex = index;

        for (int i = 0; i < Keys.Count; i++)
        {
            // Resolution (including &trans fall-through) lives on LoadedKeymap;
            // here we just paint the resolved binding onto the key VM.
            var r = km.Resolve(index, i);
            Keys[i].ApplyBinding(
                r.Binding,
                activeLayerIndex: index,
                targetLayer: r.TargetLayer,
                targetLayerName: r.TargetLayerName,
                profileId: _profile.Id,
                holdTap: r.HoldTap,
                macros: km.MacrosByName);
        }

        for (int i = 0; i < Layers.Count; i++)
            Layers[i].IsSelected = Layers[i].Index == index;
    }

    /// <summary>
    /// Walks the loaded keymap's combos once and populates the legend
    /// collection plus each key's <see cref="KeyViewModel.SetCombos"/>.
    /// Called once per keymap load from <see cref="LoadLayoutFromPath"/>
    /// after the initial <see cref="ApplyActiveLayer"/> — combos aren't
    /// layer-scoped, so this work doesn't repeat on layer switch. The
    /// participating-key labels in the legend's
    /// <c>ParticipatingKeysText</c> reflect whatever layer was active at
    /// load time (layer 0 in practice) and stay stable from there.
    /// </summary>
    private void BuildCombosForConfig()
    {
        SetHighlightedCombos(Array.Empty<int>());
        Combos.Clear();

        var km = _keymap.Current;
        if (km is null)
        {
            for (var i = 0; i < Keys.Count; i++)
                Keys[i].SetCombos(Array.Empty<ZmkCombo>(), Array.Empty<int>(), _ => "");
            return;
        }

        var allCombos = km.Combos;
        var combosByKey = new Dictionary<int, List<ZmkCombo>>();
        var numbersByKey = new Dictionary<int, List<int>>();
        for (var i = 0; i < allCombos.Count; i++)
        {
            var combo = allCombos[i];
            var number = i + 1;
            foreach (var keyIdx in combo.KeyPositions)
            {
                if (!combosByKey.TryGetValue(keyIdx, out var list))
                    combosByKey[keyIdx] = list = new List<ZmkCombo>();
                list.Add(combo);
                if (!numbersByKey.TryGetValue(keyIdx, out var nums))
                    numbersByKey[keyIdx] = nums = new List<int>();
                nums.Add(number);
            }
        }

        string LabelLookup(int idx) => idx >= 0 && idx < Keys.Count ? Keys[idx].Label : "";

        for (var i = 0; i < Keys.Count; i++)
        {
            if (combosByKey.TryGetValue(i, out var keyCombos))
                Keys[i].SetCombos(keyCombos, numbersByKey[i], LabelLookup);
            else
                Keys[i].SetCombos(Array.Empty<ZmkCombo>(), Array.Empty<int>(), LabelLookup);
        }

        for (var i = 0; i < allCombos.Count; i++)
        {
            var combo = allCombos[i];
            var key = ComboLabelOverrides.KeyPositionsToKey(combo.KeyPositions);
            var ov = ComboLabelOverrides.Get(_profile.Id, key);
            Combos.Add(new ComboViewModel(i + 1, combo, LabelLookup, km.MacrosByName, ov));
        }
    }

    /// <summary>
    /// Lazily-instantiated press-highlight tracker. Constructed on first use
    /// so it captures the <see cref="Keys"/> collection after
    /// <see cref="BuildKeysFromProfile"/> has populated it.
    /// </summary>
    private IKeyHighlightTracker HighlightTracker =>
        _highlightTracker ??= new KeyHighlightTracker(Keys);


    private void ToggleLiveHighlighting()
    {
        var enable = !IsLiveHighlightingEnabled;
        IsLiveHighlightingEnabled = enable;
        // Merged toolbar toggle: AutoLayerSwitch rides along with
        // LiveHighlighting so users get a single "live" master switch.
        // The underlying booleans stay distinct in UserSettings so any
        // future independent control point can still flip them apart.
        IsAutoLayerSwitchEnabled = enable;
        PersistSetting(s2 => s2 with
        {
            LiveKeyHighlighting = enable,
            AutoLayerSwitch = enable,
        });

        if (enable)
        {
            StartKeyEventTracking();
        }
        else
        {
            StopKeyEventTracking();
            ResetLayerState();
        }
    }

    private void StartKeyEventTracking()
    {
        _hid.Start();
        // Initial label sync — the source may already have settled the
        // active state before our subscription was attached above.
        OnActiveSourceChanged();
    }

    private void StopKeyEventTracking()
    {
        // Revert any in-flight mouse-layer push *before* tearing down HID.
        // Otherwise the engine's _preMoveLayer stays set across the stop,
        // and the next enable captures the (still-held) mouse layer as
        // the new pre-move layer — getting stuck pushing/reverting onto
        // the mouse layer.
        _push.RevertMouseLayerForShutdown(TimeSpan.FromMilliseconds(500));
        _hid.Stop();
        IsHidSourceActive = false;
        LayerSourceHint = "";
    }

    private void OnActiveLayerChanged(int layer)
    {
        if (!IsAutoLayerSwitchEnabled) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyActiveLayer(layer));
    }

    private void OnActiveSourceChanged()
    {
        var hidActive = _hid.IsConnected;
        var label = _hid.SourceLabel;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsHidSourceActive = hidActive;
            LayerSourceHint = string.IsNullOrEmpty(label)
                ? ""
                : Loc.Instance.Format("Status_LayerSourceHintFormat", label);
            // Mouse-layer engine only fires when HID is connected; toggling
            // it stops the OS-level mouse tap while disconnected.
            _push.OnHidConnectionChanged();
        });
    }

    /// <summary>
    /// Press-highlight path for the HID source: the firmware reports the
    /// physical matrix position so we go straight to <see cref="Keys"/>[position].
    /// </summary>
    private void OnKeyPositionForHighlight(int position, bool pressed)
    {
        if (!pressed) return;
        HighlightTracker.PulseAt(position);
    }

    private void ResetLayerState()
    {
        ApplyActiveLayer(0);
    }

    // Thin wrapper over the shared SettingsServiceExtensions.Update so every
    // MainVM persist names the "MainVM" diagnostic subsystem in one place; the
    // load→mutate→save + error-swallow lives in the extension.
    private void PersistSetting(Func<UserSettings, UserSettings> update) =>
        _settingsService.Update(update, "MainVM");
}
