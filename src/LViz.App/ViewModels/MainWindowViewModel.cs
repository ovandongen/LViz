using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.GitHub;
using LViz.App.Services.MouseIdle;
using LViz.Core.Diagnostics;
using LViz.Core.GitHub;
using LViz.Core.Input;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Layout;
using LViz.Core.Models;
using LViz.Core.Persistence;
using LViz.Core.Settings;
using ZmkHidProtocol.ActiveWindow;
using ZmkHidProtocol.Protocol;
using ZmkHidProtocol.Transport;
// The RecentKeymaps property below shadows the Core helper type of the same
// name inside this class; the bookkeeping stays reachable through this alias.
using RecentKeymapHistory = LViz.Core.Settings.RecentKeymaps;

namespace LViz.App.ViewModels;

/// <summary>
/// Top-level view model for <c>MainWindow</c>. Owns the loaded keymap, the
/// active layer, the live-key tracker, and persists the user's choice of
/// keyboard + last-loaded JSON path.
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IBoardSurface
{
    /// <summary>
    /// Null on the main VM outside keymap edit mode — the main window's
    /// BoardView is read-only there. While editing it selects the tapped key
    /// (<see cref="OnEditKeyTapped"/>). The picker VM
    /// (<see cref="ExitKeyPickerViewModel"/>) provides a real handler so taps
    /// register selections.
    /// </summary>
    public Action<int>? OnKeyTapped => IsEditing ? OnEditKeyTapped : null;

    /// <summary>
    /// Double-click on a cap while editing selects it and opens the key editor
    /// on it (see <see cref="EditSelectedKeyAsync"/>). Null outside edit mode,
    /// where the board has no editor to open.
    /// </summary>
    public Action<int>? OnKeyDoubleTapped => IsEditing ? OnEditKeyDoubleTapped : null;

    /// <summary>
    /// Right-click on a key opens the per-key label-override editor — inert
    /// while editing a keymap, where the label dialog doesn't apply. Wired
    /// up so the View can stay binding-only; the actual dialog launch is a
    /// callback into <c>App.axaml.cs</c> (see <see cref="EditKeyLabelRequested"/>)
    /// because the View can't construct a window directly without leaking
    /// Avalonia.Controls into the VM.
    /// </summary>
    public Action<int>? OnKeyRightTapped => IsEditing ? null : OnKeyRightTappedInternal;

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

    // GitHub keymap opener (sign-in, repo refresh, open). Null in tests that
    // don't wire it — GitHub entries then only ever load from nothing and a
    // click on one toasts "GitHub is not available".
    private readonly IGitHubKeymapService? _gitHub;

    // Backup store the keymap editor's local save snapshots into. App injects
    // the shared one; tests point it at a temp folder. Only ever written by a
    // part-6 save, never by entering or leaving edit mode.
    private readonly KeymapBackupStore _keymapBackups;

    private IKeyboardProfile _profile;
    // Last successful load's status text. Null when the current StatusMessage
    // is something else (error, transient, etc).
    private string? _loadStatusBase;

    // Settings entry (a local path, or a GitHubKeymapRef string) of the keymap
    // currently loaded. Not LoadedLayoutPath: a GitHub keymap loads through
    // its local cache file, so the loaded path and the stored entry differ.
    // Drives RecentKeymapItem.IsCurrent; card 11 reads this field by name.
    private string? _loadedEntry;

    private readonly IHidPipeline _hid;
    private readonly ILayerPushCoordinator _push;

    // Cross-device capability bus + router. The bus owns the *other* raw-HID
    // devices (the keyboard is never opened twice — the router gets it as an
    // adapter over _hid); the router brokers capability traffic between them.
    // Started/stopped/disposed alongside HID, re-scanned on profile change.
    private readonly RawHidDeviceBus _deviceBus;
    private readonly ICapabilityRouter _router;
    private readonly IHostActionExecutor _hostActionExecutor;

    // Signal.fire → action-pipeline plumbing. The dispatcher listens for inbound
    // signal reports across every router device and runs the bound pipeline; the
    // runner executes a pipeline's steps (layer push, RGB/pointing to a bus
    // device, launch/shell, delay). Both ride the same live-tracking lifecycle as
    // the router/bus (Start/Stop in StartKeyEventTracking).
    private readonly IPipelineRunner _pipelineRunner;
    private readonly ISignalDispatcher _signalDispatcher;
    private readonly AppLayerPipelineDispatcher _appLayerDispatcher;

    // Local IPC surface for the CLI / host scripts (lviz-host-agent-cli-spec.md).
    // The command service resolves an inbound string event id to a pipeline (the
    // string-keyed sibling of the signal dispatcher) and reports device status; the
    // server runs the named-pipe accept loop. Unlike the signal dispatcher it rides
    // the *app* lifetime, not the live-tracking toggle — the CLI must reach a
    // running LViz whether or not highlighting is on (Start in the ctor, Stop in
    // Shutdown). Device-step pipelines still need the router started, same as signals.
    private readonly LvizIpcServer _ipcServer;

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

    /// <summary>
    /// Cross-device capability router — the device inventory + routing table the
    /// Settings UI binds to directly (no MainVM pass-through). Lives as long as
    /// the VM; active only while HID tracking is on.
    /// </summary>
    public ICapabilityRouter CapabilityRouter => _router;

    /// <summary>True when ≥2 protocol-capable devices expose a routable trigger→handler pair.</summary>
    public bool IsDeviceRoutingAvailable => _router.IsRoutingAvailable;

    [ObservableProperty] private bool _isAlwaysOnTop;
    [ObservableProperty] private bool _colorTrayIconByActiveLayer;
    [ObservableProperty] private bool _useRingKeyEditor;
    [ObservableProperty] private bool _isLiveHighlightingEnabled;
    [ObservableProperty] private bool _isAutoLayerSwitchEnabled;
    [ObservableProperty] private bool _hasLayoutLoaded;

    // The edit button's enabled state and tooltip follow the loaded file, so
    // every flip of "a layout is loaded" re-raises them (a successful load
    // raises again explicitly — the file can change while this stays true).
    partial void OnHasLayoutLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartEdit));
        OnPropertyChanged(nameof(EditButtonTooltip));
        StartEditCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// True when a keymap loaded successfully but its binding count matches
    /// no board profile in the registry. Drives the status-bar layout-wizard
    /// link; false whenever the keymap fits the active board or none is loaded.
    /// </summary>
    [ObservableProperty] private bool _hasUnmatchedKeymap;

    /// <summary>Transient auto-dismissing toast banner (load errors, etc.).</summary>
    public ToastController Toast { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveLayerTintColor))]
    private int _activeLayerIndex;

    /// <summary>Palette color for the active layer — used as the press-highlight pulse fill.</summary>
    public string ActiveLayerTintColor => LayerColorFor(ActiveLayerIndex);

    /// <summary>
    /// Tab color for the layer at <paramref name="index"/>. While a keymap edit
    /// is open the color follows the <em>layer</em> rather than the position: a
    /// layer that came from the baseline keeps the color its baseline index
    /// owns (surviving a move), and a layer the edit added gets the palette
    /// default for its position. Outside an edit this is the plain
    /// per-profile palette lookup.
    /// </summary>
    private string LayerColorFor(int index)
    {
        if (Edit is { } edit)
        {
            return edit.Session.BaselineIndexOf(index) is int baseline
                ? LayerColorPalette.GetColor(_profile.Id, baseline)
                : LayerColorPalette.GetDefaultColor(index);
        }
        return LayerColorPalette.GetColor(_profile.Id, index);
    }

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

    partial void OnUseRingKeyEditorChanged(bool value) =>
        PersistSetting(s => s with { RingKeyEditor = value });

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
            layer.TabColor = LayerColorFor(layer.Index);
        // Re-resolve every key's fill — &lt / &mo keys reference arbitrary
        // layer colors, so changing layer 2's tint repaints layer 0's view too.
        if (_keymap.HasLayout)
            ApplyActiveLayer(ActiveLayerIndex);
        OnPropertyChanged(nameof(ActiveLayerTintColor));
    }

    /// <summary>
    /// Shows or hides one layer's tab button on the main form for the
    /// currently active keyboard profile. Persists the choice to
    /// <see cref="UserSettings.HiddenLayers"/> and updates the matching
    /// <see cref="Layers"/> item in place (no rebuild — that would steal
    /// selection focus). Layer 0 is always shown, so requests for it are
    /// ignored. The layer itself stays fully live: auto-follow, Settings
    /// pickers and keyboard pushes are untouched.
    /// </summary>
    public void SetLayerShown(int layerIndex, bool isShown)
    {
        if (layerIndex <= 0) return;

        PersistSetting(s => s with
        {
            HiddenLayers = HiddenLayers.With(s.HiddenLayers, _profile.Id, layerIndex, hidden: !isShown),
        });

        foreach (var layer in Layers)
            if (layer.Index == layerIndex)
                layer.IsShown = isShown;
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
    [NotifyPropertyChangedFor(nameof(IsComboLegendVisible))]
    private bool _isComboOverlayVisible;

    partial void OnIsComboOverlayVisibleChanged(bool value) =>
        PersistSetting(s => s with { ComboOverlayVisible = value });

    /// <summary>
    /// Whether the combo legend renders: the persisted overlay toggle, or always
    /// while a keymap edit is open — in edit mode the legend is the combo
    /// editor's tile strip. Raised whenever either source changes.
    /// </summary>
    public bool IsComboLegendVisible => IsComboOverlayVisible || IsEditing;

    [RelayCommand]
    private void ToggleComboOverlay() => IsComboOverlayVisible = !IsComboOverlayVisible;

    /// <summary>
    /// Every combo in the shown keymap, numbered sequentially
    /// in source-file order starting at 1. Not layer-scoped — combos
    /// surface on every layer view so the user can see what chords exist
    /// even when sitting on a layer where the combo doesn't fire.
    /// While a keymap edit is open these are the <em>edited</em> combos,
    /// every tile editable. Repopulated by <see cref="BuildCombosForConfig"/>
    /// on layout reload and on every edit-mode repaint.
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
    [NotifyPropertyChangedFor(nameof(CanEditLayout))]
    private IKeyboardProfile _selectedKeyboard = null!;

    /// <summary>True when the selected profile came from a user layout —
    /// gates the picker flyout's "Edit layout…" item.</summary>
    public bool CanEditLayout => SelectedKeyboard is UserLayoutProfile;

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
    /// <summary>Set by <c>App.axaml.cs</c>: opens the read-only "Keyboard
    /// shortcuts" window from the Help menu.</summary>
    public Func<Task>? ShortcutsRequested { get; set; }
    /// <summary>Set by <c>App.axaml.cs</c>: opens the layout-wizard window.</summary>
    public Action? AddOwnBoardRequested { get; set; }
    /// <summary>Set by <c>App.axaml.cs</c>: opens the "Edit layout…" window.</summary>
    public Action? EditLayoutRequested { get; set; }
    /// <summary>Set by <c>App.axaml.cs</c> (card 12+): opens the GitHub keymap picker window.</summary>
    public Action? OpenFromGitHubRequested { get; set; }

    /// <summary>Path of the layout JSON the user currently has loaded, or null if none.</summary>
    public string? LoadedLayoutPath => _keymap.LoadedPath;

    /// <summary>
    /// Recent keymap paths for the active profile, most-recent-first (empty
    /// when none). Backs the future recents menu — no UI is wired to it yet.
    /// Raises <c>PropertyChanged</c> after every load (success or failure)
    /// and after a keyboard-profile switch. Each read re-loads settings, so
    /// the list is a snapshot, not a live view; treat it as read-only.
    /// </summary>
    public IReadOnlyList<string> RecentKeymaps =>
        _settingsService.Load().RecentKeymapPaths.TryGetValue(_profile.Id, out var paths)
            ? paths
            : Array.Empty<string>();

    /// <summary>
    /// File-existence seam for the recents surface — tests fake the disk
    /// here; production is <see cref="File.Exists"/>. The startup stored-path
    /// prune (<see cref="TryGetStoredPathForProfile"/>) deliberately keeps
    /// checking the disk directly.
    /// </summary>
    internal Func<string, bool> FileExists { get; set; } = File.Exists;

    /// <summary>
    /// <see cref="RecentKeymaps"/> in flyout-ready shape: one item per entry
    /// in the same order. A <see cref="GitHubKeymapRef"/> entry renders as a
    /// GitHub row — repo · branch as the folder line, never greyed out.
    /// Local entries are stamped at read time with whether the file is still
    /// on disk. Each row's IsCurrent compares the entry with the loaded
    /// keymap's settings entry (a GitHub keymap's <em>file</em> lives in the
    /// cache, so <see cref="LoadedLayoutPath"/> can't answer that).
    /// Snapshot, like the raw list — rebuilt on every read.
    /// </summary>
    public IReadOnlyList<RecentKeymapItem> RecentKeymapItems
    {
        get
        {
            var current = _loadedEntry;
            var entries = RecentKeymaps;
            var items = new List<RecentKeymapItem>(entries.Count);
            foreach (var entry in entries)
            {
                if (GitHubKeymapRef.TryParse(entry, out var keymap))
                {
                    // A GitHub row is never greyed out: "missing" has no
                    // meaning for a repo path, and the cache may be cold.
                    items.Add(new RecentKeymapItem(
                        entry,
                        keymap.FileName,
                        $"{keymap.RepoFullName} · {keymap.Branch}",
                        Exists: true,
                        IsCurrent: entry == current,
                        IsGitHub: true));
                    continue;
                }

                items.Add(new RecentKeymapItem(
                    entry,
                    Path.GetFileName(entry),
                    Path.GetDirectoryName(entry) ?? "",
                    FileExists(entry),
                    string.Equals(entry, current, StringComparison.Ordinal)));
            }
            return items;
        }
    }

    /// <summary>True when the active profile has at least one recent keymap.</summary>
    public bool HasRecentKeymaps => RecentKeymaps.Count > 0;

    // --- Commands ---
    public IRelayCommand QuitCommand { get; }
    public IRelayCommand ShowCommand { get; }
    public IRelayCommand LoadLayoutCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand TogglePinCommand { get; }
    public IRelayCommand ToggleLiveHighlightingCommand { get; }
    public IRelayCommand OpenLogFolderCommand { get; }
    public IRelayCommand CopyDiagnosticsCommand { get; }
    public IRelayCommand OpenHelpCommand { get; }
    public IAsyncRelayCommand OpenShortcutsCommand { get; }
    public IRelayCommand<IKeyboardProfile> SelectKeyboardCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IRelayCommand RefreshRecentKeymapsCommand { get; }
    public IRelayCommand<RecentKeymapItem> OpenRecentKeymapCommand { get; }
    public IRelayCommand ClearRecentKeymapsCommand { get; }

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
        IKeymapStateService? keymapState = null,
        ICapabilityControl? capabilityControl = null,
        IHostActionExecutor? hostActionExecutor = null,
        IGitHubKeymapService? gitHub = null,
        KeymapBackupStore? keymapBackups = null)
    {
        _settingsService = settingsService;
        _keymap = keymapState ?? new KeymapStateService();
        _gitHub = gitHub;
        _keymapBackups = keymapBackups ?? new KeymapBackupStore(KeymapBackupStore.DefaultRoot);
        var s = settingsService.Load();
        _profile = KeyboardProfileRegistry.TryResolve(s.Keyboard, out var p) ? p : new CorneProfile();
        _selectedKeyboard = _profile;
        _isAlwaysOnTop = s.AlwaysOnTop;
        _colorTrayIconByActiveLayer = s.ColorTrayIconByActiveLayer;
        _useRingKeyEditor = s.RingKeyEditor;
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

        // Capability bus excludes the keyboard via its live profile matcher (the
        // open-path is a defensive secondary exclusion); the router consumes the
        // keyboard as an adapter over _hid's already-open handle plus the bus's
        // other devices. Constructed eagerly; opens nothing until Start().
        _deviceBus = new RawHidDeviceBus(_hid.KeyboardMatcher, () => _hid.OpenDevicePath);
        _router = new CapabilityRouter(
            new KeyboardCapabilityDevice(_hid),
            _deviceBus,
            settingsService);
        _router.RoutesChanged += OnCapabilityRoutesChanged;

        // signal.fire → action-pipeline. The runner drives bus devices through
        // capability control + the keyboard through the HID layer-push path; the
        // dispatcher resolves inbound signal ids to pipelines and runs them off
        // the read thread. CapabilityControl is stateless (own ConfirmTracker per
        // instance), so a private one here doesn't conflict with the DI singleton.
        _hostActionExecutor = hostActionExecutor ?? new HostActionExecutor();
        _pipelineRunner = new PipelineRunner(
            _router,
            capabilityControl ?? new CapabilityControl(),
            _hostActionExecutor,
            _hid.PushLayer,
            () => settingsService.Load().ActionPipelines);
        _signalDispatcher = new SignalDispatcher(_router, settingsService, _pipelineRunner);

        // Host IPC: the CLI ('lviz event …', 'status', 'devices') forwards a line to
        // this app over a named pipe. Started here so it's live for the app's whole
        // lifetime, independent of the live-tracking toggle.
        var ipcCommands = new IpcCommandService(settingsService, _router, _pipelineRunner);
        _ipcServer = new LvizIpcServer(ipcCommands.Handle);
        _ipcServer.Start();

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
        // App-focus auto-switch lifecycle → action-pipeline. Sibling of
        // _signalDispatcher / the IPC event path: the engine raises a moment as it
        // pushes/reverts layers, this runs the bound pipeline alongside it. No
        // Start/Stop — the engine only raises while auto-switch is enabled.
        _appLayerDispatcher = new AppLayerPipelineDispatcher(_push.AutoSwitch, settingsService, _pipelineRunner);
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
            if (IsEditing)
            {
                DiagnosticLog.Info("MainVM", "RefreshCommand ignored while editing");
                return;
            }
            // Re-read the stored entry and resolve it (GitHub entries load
            // from the cache, then check GitHub in the background, which also
            // fetches a cold cache). A local entry that resolves to nothing
            // does nothing, as an empty path did before.
            var entries = _settingsService.Load().LayoutJsonPaths;
            if (entries.TryGetValue(_profile.Id, out var entry) && !string.IsNullOrEmpty(entry))
            {
                if (TryResolveEntry(entry, out var path))
                    LoadLayoutFromPath(path, entry);
                PendingGitHubCheck = CheckGitHubForUpdateAsync(entry);
            }
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
        OpenHelpCommand = new RelayCommand(() => _hostActionExecutor.Launch(UpdateService.WikiUrl));
        OpenShortcutsCommand = new AsyncRelayCommand(async () =>
        {
            if (ShortcutsRequested is not null) await ShortcutsRequested();
        });
        RefreshRecentKeymapsCommand = new RelayCommand(RaiseRecentKeymapsChanged);
        OpenRecentKeymapCommand = new RelayCommand<RecentKeymapItem>(OpenRecentKeymap);
        ClearRecentKeymapsCommand = new RelayCommand(ClearRecentKeymaps);

        BuildKeysFromProfile();
    }

    /// <summary>
    /// Post-startup entry point: load last-used layout, spin up the key-event
    /// hook, and set an opening status message.
    /// </summary>
    public void InitializeAsync()
    {
        var s = _settingsService.Load();
        if (TryGetStoredPathForProfile(s, _profile.Id, out var storedPath, out var storedEntry))
        {
            LoadLayoutFromPath(storedPath, storedEntry);
        }
        else
        {
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance["Status_NoLayoutLoaded"];
        }
        PendingGitHubCheck = CheckGitHubForUpdateAsync(storedEntry);

        if (IsLiveHighlightingEnabled)
            StartKeyEventTracking();
    }

    /// <summary>
    /// Loads the keymap file at <paramref name="path"/> and books
    /// <paramref name="entry"/> (defaulting to the path) as its settings
    /// identity — for a GitHub keymap the path is the local cache file while
    /// the entry stays the <see cref="GitHubKeymapRef"/> string, so recents
    /// and the per-profile last-used slot keep storing the reference, not a
    /// cache path the cache layer may move or wipe.
    /// </summary>
    public void LoadLayoutFromPath(string path, string? entry = null)
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "LoadLayoutFromPath ignored while editing");
            return;
        }

        entry ??= path;

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
            // A keymap that no longer loads shouldn't linger in "recent" —
            // the *entry* is what recents store, so that's what gets pruned.
            // Skip the write entirely when the entry isn't listed.
            if (RecentKeymaps.Contains(entry, StringComparer.Ordinal))
            {
                PersistSetting(s =>
                {
                    if (!s.RecentKeymapPaths.TryGetValue(_profile.Id, out var recents))
                        return s;
                    var recentPaths = new Dictionary<string, List<string>>(s.RecentKeymapPaths)
                    {
                        [_profile.Id] = RecentKeymapHistory.Remove(recents, entry),
                    };
                    return s with { RecentKeymapPaths = recentPaths };
                });
            }
            RaiseRecentKeymapsChanged();
            return;
        }

        var bindingCount = loaded.FirstLayerBindingCount;
        var keyCountMismatch = loaded.LayerCount > 0 && bindingCount != _profile.KeyCount;
        IKeyboardProfile? autoSwitchedTo = null;
        if (keyCountMismatch)
        {
            var matching = KeyboardProfileRegistry.FindByKeyCount(bindingCount);
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
        // An orphaned keymap (mismatch, nothing to switch to) arms the
        // status-bar wizard link; every other successful load disarms it.
        HasUnmatchedKeymap = keyCountMismatch && autoSwitchedTo is null;

        RebuildLayers();
        ApplyActiveLayer(0);
        BuildCombosForConfig();
        HasLayoutLoaded = true;

        // Booked for the profile the keymap ended up on (_profile is already
        // the auto-switch target when one ran). The *entry*, not the loaded
        // path — see the method doc.
        _loadedEntry = entry;
        BookEntryForProfile(entry);

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

        // The edit button tracks the loaded file: enabled for a fresh .keymap,
        // tooltip wording per extension. HasLayoutLoaded may not have flipped,
        // so raise explicitly on every successful load.
        OnPropertyChanged(nameof(CanStartEdit));
        OnPropertyChanged(nameof(EditButtonTooltip));
        StartEditCommand.NotifyCanExecuteChanged();
    }

    // The recents flyout binds RecentKeymaps + the two derived members;
    // every mutation site raises all three so an open flyout refreshes in
    // one step.
    private void RaiseRecentKeymapsChanged()
    {
        OnPropertyChanged(nameof(RecentKeymaps));
        OnPropertyChanged(nameof(RecentKeymapItems));
        OnPropertyChanged(nameof(HasRecentKeymaps));
    }

    /// <summary>
    /// Recents flyout row click: a GitHub row re-opens through the network
    /// (cache fallback offline); a local row loads a still-present keymap
    /// through the normal path, or — when its file has gone missing — prunes
    /// it from the active profile's list instead. The disk is re-checked at
    /// click time because the row's <see cref="RecentKeymapItem.Exists"/> is
    /// only a snapshot from when the flyout built its list.
    /// </summary>
    private void OpenRecentKeymap(RecentKeymapItem? item)
    {
        if (item is null) return;

        if (GitHubKeymapRef.TryParse(item.Path, out var keymap))
        {
            // Fire-and-forget: the row's click completes; status/toasts report
            // the outcome. Unlike a stale local row, a failing GitHub entry is
            // NOT pruned — the repo may be temporarily unreachable.
            _ = OpenGitHubKeymapAsync(keymap);
            return;
        }

        if (FileExists(item.Path))
        {
            // Success bookkeeping (recents move-to-front, status, recents
            // notifications) lives in the load path itself.
            LoadLayoutFromPath(item.Path);
            return;
        }

        // Stale entry — drop it from the list; skip the write entirely when
        // settings no longer list the path (mirrors the load-failure prune).
        if (RecentKeymaps.Contains(item.Path, StringComparer.Ordinal))
        {
            PersistSetting(s =>
            {
                if (!s.RecentKeymapPaths.TryGetValue(_profile.Id, out var recents))
                    return s;
                var recentPaths = new Dictionary<string, List<string>>(s.RecentKeymapPaths)
                {
                    [_profile.Id] = RecentKeymapHistory.Remove(recents, item.Path),
                };
                return s with { RecentKeymapPaths = recentPaths };
            });
        }
        RaiseRecentKeymapsChanged();
        SetLoadStatusBase(null);
        StatusMessage = Loc.Instance.Format("Status_RecentKeymapRemovedFormat", item.FileName);
    }

    /// <summary>
    /// Opens a GitHub keymap: refreshes its repo through
    /// <see cref="IGitHubKeymapService"/> and loads the resulting local file
    /// under the reference as its settings entry (offline cache copies load
    /// too, with a toast). Failures only toast — the entry stays in recents
    /// because the repo may just be temporarily unreachable. An
    /// <see cref="GitHubErrorKind.Unauthorized"/> additionally raises
    /// <see cref="OpenFromGitHubRequested"/> so the sign-in window can open.
    /// </summary>
    public async Task OpenGitHubKeymapAsync(GitHubKeymapRef keymap)
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "OpenGitHubKeymapAsync ignored while editing");
            return;
        }

        if (_gitHub is null)
        {
            Toast.Show(Loc.Instance.Format("Toast_GitHubFailedFormat", keymap.FileName, "GitHub is not available"));
            return;
        }

        // Keep what the bar said before "Loading…" so a failure can put it
        // back instead of leaving a stale progress message behind.
        var statusBefore = StatusMessage;
        SetLoadStatusBase(null);
        StatusMessage = Loc.Instance.Format("Status_GitHubLoadingFormat", keymap.FileName);
        DiagnosticLog.Info("MainVM", $"Opening '{keymap}' from GitHub");

        var result = await _gitHub.OpenAsync(keymap);
        switch (result)
        {
            case GitHubOpenResult.Loaded loaded:
                DiagnosticLog.Info("MainVM",
                    $"Opened '{keymap}' from GitHub ({(loaded.FromCacheOffline ? "offline cache" : "fresh")})");
                LoadLayoutFromPath(loaded.LocalPath, keymap.ToString());
                if (loaded.FromCacheOffline)
                    Toast.Show(Loc.Instance.Format("Toast_GitHubOfflineFormat", keymap.FileName));
                break;

            case GitHubOpenResult.Failed failed:
                DiagnosticLog.Warn("MainVM", $"Opening '{keymap}' from GitHub failed ({failed.Kind}): {failed.Message}");
                StatusMessage = statusBefore;
                switch (failed.Kind)
                {
                    case GitHubErrorKind.Unauthorized:
                        Toast.Show(Loc.Instance.Format("Toast_GitHubSignInNeededFormat", keymap.FileName));
                        OpenFromGitHubRequested?.Invoke();
                        break;
                    case GitHubErrorKind.NotFound:
                        Toast.Show(Loc.Instance.Format("Toast_GitHubNotFoundFormat", keymap.FileName));
                        break;
                    case GitHubErrorKind.Network:
                        Toast.Show(Loc.Instance.Format("Toast_GitHubNetworkFormat", keymap.FileName));
                        break;
                    default:
                        Toast.Show(Loc.Instance.Format("Toast_GitHubFailedFormat", keymap.FileName, failed.Message));
                        break;
                }
                break;
        }
    }

    /// <summary>
    /// One write for both dicts: <paramref name="entry"/> becomes the active
    /// profile's last-used keymap and moves to the front of its recents.
    /// <paramref name="takeFromProfileId"/>, when given, loses its last-used
    /// slot if that slot held the same entry (the keymap didn't fit there).
    /// </summary>
    private void BookEntryForProfile(string entry, string? takeFromProfileId = null)
    {
        PersistSetting(s =>
        {
            var paths = new Dictionary<string, string>(s.LayoutJsonPaths) { [_profile.Id] = entry };
            if (takeFromProfileId is not null
                && !string.Equals(takeFromProfileId, _profile.Id, StringComparison.OrdinalIgnoreCase)
                && paths.TryGetValue(takeFromProfileId, out var old)
                && string.Equals(old, entry, StringComparison.Ordinal))
                paths.Remove(takeFromProfileId);
            var recentPaths = new Dictionary<string, List<string>>(s.RecentKeymapPaths)
            {
                [_profile.Id] = RecentKeymapHistory.Push(
                    s.RecentKeymapPaths.TryGetValue(_profile.Id, out var recents)
                        ? recents
                        : Array.Empty<string>(),
                    entry),
            };
            return s with { LayoutJsonPaths = paths, RecentKeymapPaths = recentPaths };
        });
        RaiseRecentKeymapsChanged();
    }

    /// <summary>The last background GitHub check started at startup or Refresh; tests await it.</summary>
    internal Task? PendingGitHubCheck { get; private set; }

    /// <summary>
    /// After a profile's stored GitHub entry was restored (startup, board
    /// switch, Refresh): ask GitHub in the background whether the branch
    /// moved, and if a newer copy came down, reload it and say so. When the
    /// cache was cold (nothing loaded yet) the fetched copy simply loads.
    /// Silent when signed out or offline — a startup shouldn't pop up errors
    /// or the sign-in window. A no-op for local entries.
    /// </summary>
    private async Task CheckGitHubForUpdateAsync(string entry)
    {
        if (_gitHub is null || !GitHubKeymapRef.TryParse(entry, out var keymap))
            return;

        var profileId = _profile.Id;
        try
        {
            await _gitHub.EnsureRestoredAsync();
            if (!_gitHub.IsSignedIn)
                return;

            var result = await _gitHub.FetchRepoAsync(keymap.Owner, keymap.Repo, keymap.Branch);

            // The user switched board or opened something else meanwhile.
            var wasLoaded = _loadedEntry == entry;
            if (_profile.Id != profileId || !(wasLoaded || _loadedEntry is null))
                return;
            if (wasLoaded && !result.Downloaded)
                return;

            if (_gitHub.LocalPathFor(keymap) is not string path)
            {
                DiagnosticLog.Warn("MainVM", $"'{keymap}' is gone from the newest GitHub copy; keeping the loaded one");
                return;
            }

            // An open edit holds the bytes the cache copy was parsed from;
            // reloading underneath it is what the explicit load lock refuses —
            // and no "changed on GitHub" toast may claim otherwise.
            if (IsEditing)
            {
                DiagnosticLog.Info("MainVM", "CheckGitHubForUpdateAsync ignored while editing");
                return;
            }

            DiagnosticLog.Info("MainVM", wasLoaded
                ? $"'{keymap}' changed on GitHub, reloading"
                : $"'{keymap}' fetched from GitHub (no cached copy)");
            LoadLayoutFromPath(path, entry);
            if (wasLoaded)
                Toast.Show(Loc.Instance.Format("Toast_GitHubUpdatedFormat", keymap.FileName));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Info("MainVM", $"Background GitHub check for '{keymap}' skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// "Clear list" in the recents flyout: drops the active profile's entry
    /// from the per-profile dict wholesale — sibling profiles keep their own
    /// history. No write when the list is already empty.
    /// </summary>
    private void ClearRecentKeymaps()
    {
        if (RecentKeymaps.Count == 0) return;

        PersistSetting(s =>
        {
            if (!s.RecentKeymapPaths.ContainsKey(_profile.Id))
                return s;
            var recentPaths = new Dictionary<string, List<string>>(s.RecentKeymapPaths);
            recentPaths.Remove(_profile.Id);
            return s with { RecentKeymapPaths = recentPaths };
        });
        RaiseRecentKeymapsChanged();
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
        StopKeyEventTracking(isAppExit: true);
        // Stop accepting CLI connections before tearing down the router it queries.
        _ipcServer.Dispose();
        // Dispatcher before the push coordinator: it holds a subscription on the
        // coordinator's AutoSwitch engine.
        _appLayerDispatcher.Dispose();
        _push.Shutdown();
        _push.Dispose();
        // Dispatcher before the router: it holds per-device report subscriptions
        // sourced from the router's inventory.
        _signalDispatcher.Dispose();
        // Router before the bus and _hid: it unsubscribes from the keyboard
        // adapter (which rides _hid's report stream) and the bus devices.
        _router.Dispose();
        _deviceBus.Dispose();
        _hid.Dispose();
    }

    // --- Keymap edit mode (start/discard/save here; the UI is part 7) ---

    /// <summary>
    /// The open keymap edit, or null. Setting it (from <see cref="StartEdit"/>
    /// and <see cref="LeaveEdit"/>) drives everything the view changes while
    /// editing — <see cref="IsEditing"/>, <see cref="UnsavedEditCount"/>,
    /// <see cref="CanStartEdit"/> and the Start command's enable state.
    /// </summary>
    [ObservableProperty]
    private KeymapEditViewModel? _edit;

    /// <summary>True while a keymap edit is open — locks keymap/board switching
    /// and routes key taps into the editor.</summary>
    public bool IsEditing => Edit is not null;

    /// <summary>Changes still differing from the baseline; 0 when not editing.</summary>
    public int UnsavedEditCount => Edit?.ChangeCount ?? 0;

    /// <summary>Only a loaded <c>.keymap</c> file can be edited (the Moergo
    /// <c>.json</c> export stays import-only), and only one edit at a time.</summary>
    public bool CanStartEdit =>
        !IsEditing
        && HasLayoutLoaded
        && LoadedLayoutPath is string path
        && path.EndsWith(".keymap", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tooltip for the edit button; over a <c>.json</c> keymap it says
    /// that format cannot be edited.</summary>
    public string EditButtonTooltip =>
        LoadedLayoutPath is string path && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? Loc.Instance["Tooltip_EditKeymapJson"]
            : Loc.Instance["Tooltip_EditKeymap"];

    partial void OnEditChanged(KeymapEditViewModel? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(UnsavedEditCount));
        OnPropertyChanged(nameof(CanStartEdit));
        OnPropertyChanged(nameof(IsComboLegendVisible));
        StartEditCommand.NotifyCanExecuteChanged();
        NotifyEditCommandState();
    }

    /// <summary>
    /// Re-query the commands whose enable state hangs off <see cref="Edit"/> and
    /// <see cref="IsSavingEdit"/>. Every site that can move that state calls this
    /// so one of them can't be forgotten.
    /// </summary>
    private void NotifyEditCommandState()
    {
        SaveEditCommand.NotifyCanExecuteChanged();
        DiscardEditCommand.NotifyCanExecuteChanged();
        NewComboCommand.NotifyCanExecuteChanged();
        NotifyLayerCommandState();
    }

    /// <summary>
    /// Open an edit on the loaded keymap: re-read the file, parse it, and show
    /// the edit view — every layer tab visible (hidden ones included, in memory
    /// only). A read or parse failure leaves the view unedited and toasts why.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartEdit))]
    private void StartEdit()
    {
        KeymapEditViewModel edit;
        try
        {
            edit = KeymapEditViewModel.Start(
                LoadedLayoutPath!, _loadedEntry ?? LoadedLayoutPath!, _gitHub, _keymapBackups);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ZmkKeymapParseException)
        {
            Toast.Show(Loc.Instance.Format(
                "Edit_StartFailedFormat", Path.GetFileName(LoadedLayoutPath!), ex.Message));
            DiagnosticLog.Warn("MainVM", $"Starting the keymap edit failed: {ex}");
            return;
        }

        Edit = edit;
        edit.Changed += OnEditChanged;

        // The tabs rebuild off the edit: every layer shows (hidden ones
        // included, purely in memory) and each tab carries the per-layer
        // actions; LeaveEdit rebuilds them from the loaded keymap and the
        // persisted setting.
        RebuildLayers();

        ApplyActiveLayer(ActiveLayerIndex);

        // The legend follows the edit too: it shows the edited combos, and
        // every tile becomes editable with its pending-change mark.
        BuildCombosForConfig();
    }

    /// <summary>One edit change: repaint the board off the edited keymap and
    /// refresh the banner count and the save/discard enable state. The board
    /// follows the shown <em>layer</em>, not its position, so an undo or redo of
    /// a move shows that layer where it went. When the shown layer is gone (an
    /// undone add) the position is kept, or the last layer when that position
    /// no longer exists. Structural changes (add/delete/move/rename) also
    /// rebuild the tab strip — a plain key edit leaves the tabs alone, since
    /// rebuilding replaces the tab buttons.</summary>
    private void OnEditChanged(object? sender, EventArgs e)
    {
        var index = ActiveLayerIndex;
        if (Edit is { } edit)
        {
            if (_shownLayerId is int id && edit.Session.IndexOfLayerId(id) is int followed)
                index = followed;
            var count = edit.Session.Current.Layers.Count;
            if (count > 0 && (index < 0 || index >= count)) index = count - 1;
        }
        if (!TabsMatchEdit()) RebuildLayers();
        ApplyActiveLayer(index);
        OnPropertyChanged(nameof(UnsavedEditCount));
        // A tab color can move to another position while the shown index (and
        // with it the NotifyPropertyChangedFor above) stays put.
        OnPropertyChanged(nameof(ActiveLayerTintColor));
        NotifyEditCommandState();

        // The legend re-reads the edited combos — a key edit can retake a
        // tile's labels, an undo can add or drop tiles entirely.
        BuildCombosForConfig();
    }

    /// <summary>True while <see cref="Layers"/> still mirrors the open edit —
    /// same count, and every tab's caption and color equal what a rebuild from
    /// the edit would produce at its position. False (rebuild) only after a
    /// structural change; a plain key edit never moves either.</summary>
    private bool TabsMatchEdit()
    {
        if (Edit is not { } edit) return true;
        var layers = edit.Keymap.Layers;
        if (Layers.Count != layers.Count) return false;
        for (var i = 0; i < layers.Count; i++)
        {
            var tab = Layers[i];
            if (tab.DisplayName != $"{i} : {layers[i].Name}") return false;
            if (tab.TabColor != LayerColorFor(i)) return false;
        }
        return true;
    }

    /// <summary>
    /// Close the open edit without saving: unsubscribe, rebuild the tabs from
    /// settings (hidden layers hide again), and repaint from the loaded keymap.
    /// The save path reuses this once the bytes are on disk, and always before
    /// reloading anything — the edit-mode locks refuse every load path.
    /// </summary>
    private void LeaveEdit()
    {
        if (Edit is { } edit)
            edit.Changed -= OnEditChanged;
        Edit = null;
        RebuildLayers();
        ApplyActiveLayer(NearestShownLayer(ActiveLayerIndex));

        // Back to the loaded keymap's combos, and the tiles lose their
        // edit-mode flags with it.
        BuildCombosForConfig();
    }

    /// <summary>
    /// Maps a layer index to the closest layer whose tab is shown, so leaving
    /// edit mode never displays a layer the user hid: the index itself while
    /// its tab is in <see cref="Layers"/> and shown, otherwise the nearest
    /// shown layer (a tie goes to the lower index). With no layers, or none
    /// shown, there is nothing to map to and the index is returned unchanged.
    /// </summary>
    private int NearestShownLayer(int index)
    {
        if (Layers.Any(l => l.Index == index && l.IsShown)) return index;
        var shown = Layers.Where(l => l.IsShown).ToList();
        if (shown.Count == 0) return index;
        return shown
            .OrderBy(l => Math.Abs(l.Index - index))
            .ThenBy(l => l.Index)
            .First().Index;
    }

    /// <summary>Drop the edit and its changes — the editor's Discard/Esc in part 7.</summary>
    [RelayCommand(CanExecute = nameof(CanDiscardEdit))]
    private void DiscardEdit()
    {
        if (IsEditing) LeaveEdit();
    }

    /// <summary>Left-click on a key while editing selects it for the BoardView.</summary>
    private void OnEditKeyTapped(int keyIndex)
    {
        Edit!.SelectKey(keyIndex);
        RefreshEditSelectionFlags();
    }

    /// <summary>Double-click on a key while editing selects it and opens the
    /// key editor on it — fire and forget, the editor owns its own errors.</summary>
    private void OnEditKeyDoubleTapped(int keyIndex)
    {
        OnEditKeyTapped(keyIndex);
        _ = EditSelectedKeyAsync();
    }

    /// <summary>Mark the editor's current selection on every key.</summary>
    private void RefreshEditSelectionFlags()
    {
        if (Edit is not { } edit) return;
        for (var i = 0; i < Keys.Count; i++)
            Keys[i].IsEditSelected = edit.SelectedKeyIndex == i;
    }

    /// <summary>
    /// Clear the selected key on the visible layer to <c>&amp;none</c>
    /// (<paramref name="toNone"/>) or <c>&amp;trans</c> — Delete / Shift+Delete
    /// in part 7. Does nothing while not editing or when nothing is selected;
    /// the session's Changed event repaints the board and the flags.
    /// </summary>
    public void EditClearSelectedKey(bool toNone)
    {
        if (Edit is not { } edit) return;
        if (edit.SelectedKeyIndex is not int selected) return;
        edit.ClearKey(ActiveLayerIndex, selected, toNone);
    }

    /// <summary>Drop the key selection — Esc in part 7. Does nothing while not editing.</summary>
    public void EditClearSelection()
    {
        if (Edit is not { } edit) return;
        edit.ClearSelection();
        RefreshEditSelectionFlags();
    }

    // ---- the key dialog ------------------------------------------------------------

    /// <summary>
    /// Invoked when the user asks to rebind a key (double-click or Enter while
    /// editing): the callback (set by <c>App.axaml.cs</c>, part 9) owns the key
    /// dialog's lifecycle and answers the new binding, or
    /// <see langword="null"/> when the dialog was cancelled. Null means the
    /// dialog isn't wired up — opening it does nothing.
    /// </summary>
    public Func<KeyDialogRequest, Task<KeyBinding?>>? KeyDialogRequested { get; set; }

    /// <summary>
    /// Invoked when the user asks to rebind a key and the ring editor is the
    /// one to open (see <see cref="UseRingKeyEditor"/>): the callback (set by
    /// <c>App.axaml.cs</c>, part 10) owns the ring's lifecycle and answers the
    /// new binding, or <see langword="null"/> when the ring was cancelled.
    /// Null means the ring isn't wired up — the key dialog is used instead.
    /// </summary>
    public Func<KeyDialogRequest, Task<KeyBinding?>>? KeyRingRequested { get; set; }

    /// <summary>True while a callback from an earlier <see cref="EditSelectedKeyAsync"/>
    /// is still open; a second Enter/double-click is ignored until it closes.</summary>
    private bool _keyDialogOpen;

    /// <summary>
    /// Open the key editor on the selected key of the shown layer and apply
    /// what it answers as one undo step: the ring when <see cref="UseRingKeyEditor"/>
    /// is on and a ring host is wired up, the key dialog otherwise.
    /// Does nothing when not editing, when nothing is selected, when the
    /// selection sits outside the active layer, when no callback is wired up,
    /// or while an earlier editor is still open. A cancelled editor (null)
    /// changes nothing; so does a result once the edit it was opened on has
    /// been replaced underneath the await.
    /// </summary>
    public async Task EditSelectedKeyAsync()
    {
        if (Edit is not { } edit) return;
        if (edit.SelectedKeyIndex is not int key) return;

        var layer = ActiveLayerIndex;
        var layers = edit.Session.Current.Layers;
        if (layer < 0 || layer >= layers.Count) return;
        if (key < 0 || key >= layers[layer].Bindings.Count) return;

        // The ring when the setting picks it and a host is wired up; the key
        // dialog in every other case.
        var callback = UseRingKeyEditor && KeyRingRequested is not null
            ? KeyRingRequested
            : KeyDialogRequested;
        if (callback is not { } open) return;
        if (_keyDialogOpen) return;

        _keyDialogOpen = true;
        try
        {
            var result = await open(
                new KeyDialogRequest(edit.Session.Current, edit.Defines, layer, key)
                {
                    LayerDefines = edit.LayerDefines,
                });

            if (result is not null && ReferenceEquals(Edit, edit))
                edit.SetKey(layer, key, result);
        }
        finally
        {
            _keyDialogOpen = false;
        }
    }

    // ---- the combo dialog ----------------------------------------------------------

    /// <summary>
    /// Invoked when the user asks to add or edit a combo (double-click on a
    /// tile, or the tile's ⋮ menu — never a single click): the callback (set
    /// by <c>App.axaml.cs</c>, part 15) owns the combo dialog's lifecycle and
    /// answers the new combo, or <see langword="null"/> when the dialog was
    /// cancelled. Null means the dialog isn't wired up — opening it does
    /// nothing.
    /// </summary>
    public Func<ComboDialogRequest, Task<ZmkCombo?>>? ComboDialogRequested { get; set; }

    /// <summary>True while a combo-dialog callback is still open; a second
    /// new/edit request is ignored until it closes.</summary>
    private bool _comboDialogOpen;

    /// <summary>
    /// Open the combo dialog for a new combo and append what it answers as one
    /// undo step. Does nothing when not editing, while a save is in flight,
    /// when no callback is wired up, or while a combo dialog is already open.
    /// A cancelled dialog (null) changes nothing; so does a result once the
    /// edit it was opened on has been replaced underneath the await.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditLayers))]
    private async Task NewComboAsync()
    {
        if (Edit is not { } edit) return;
        if (IsSavingEdit) return;
        if (ComboDialogRequested is not { } open) return;
        if (_comboDialogOpen) return;

        _comboDialogOpen = true;
        try
        {
            var result = await open(new ComboDialogRequest(
                _profile,
                edit.Session.Current,
                edit.Defines,
                edit.LayerDefines,
                null,
                ActiveLayerIndex));

            if (result is not null && ReferenceEquals(Edit, edit))
                edit.AddCombo(result);
        }
        finally
        {
            _comboDialogOpen = false;
        }
    }

    /// <summary>
    /// Open the combo dialog on tile <paramref name="number"/> (the 1-based
    /// tile number) and replace that combo with what it answers, as one undo
    /// step. An out-of-range number, no edit, no callback, a save in flight,
    /// or an already-open dialog do nothing; so does a cancelled dialog (null)
    /// or a result once the edit has been replaced underneath the await — or
    /// once the tile it was opened on no longer exists.
    /// </summary>
    [RelayCommand]
    private async Task EditComboAsync(int number)
    {
        var index = number - 1;
        if (!CanEditLayers || Edit is not { } edit) return;
        if (ComboDialogRequested is not { } open) return;
        if (_comboDialogOpen) return;
        if (index < 0 || index >= edit.Session.Current.Combos.Count) return;

        _comboDialogOpen = true;
        try
        {
            var result = await open(new ComboDialogRequest(
                _profile,
                edit.Session.Current,
                edit.Defines,
                edit.LayerDefines,
                index,
                ActiveLayerIndex));

            if (result is not null && ReferenceEquals(Edit, edit)
                && index < edit.Session.Current.Combos.Count)
                edit.SetCombo(index, result);
        }
        finally
        {
            _comboDialogOpen = false;
        }
    }

    /// <summary>
    /// Remove the combo on tile <paramref name="number"/> (the 1-based tile
    /// number) as one undo step — no confirmation, like deleting a layer. An
    /// out-of-range number, a closed edit or a save in flight does nothing.
    /// </summary>
    [RelayCommand]
    private void DeleteCombo(int number)
    {
        var index = number - 1;
        // Not while a save is in flight: the plan is already built, so the
        // delete would be lost when the save closes the edit.
        if (!CanEditLayers || Edit is not { } edit) return;
        if (index < 0 || index >= edit.Session.Current.Combos.Count) return;

        edit.DeleteCombo(index);
    }

    // ---- layer operations --------------------------------------------------------

    /// <summary>
    /// Invoked when the user asks to rename a layer: the callback (set by
    /// <c>App.axaml.cs</c>, part 7) owns the rename dialog's lifecycle and
    /// answers the new name, or <see langword="null"/> when it was cancelled.
    /// Null means the dialog isn't wired up — renaming does nothing.
    /// </summary>
    public Func<RenameLayerRequest, Task<string?>>? RenameLayerRequested { get; set; }

    /// <summary>
    /// Invoked when a delete is refused: the callback (set by
    /// <c>App.axaml.cs</c>, part 7) owns the dialog listing the blockers. Null
    /// means the dialog isn't wired up — the refused delete passes silently.
    /// </summary>
    public Func<LayerDeleteBlockedRequest, Task>? LayerDeleteBlockedRequested { get; set; }

    /// <summary>True while a callback from <see cref="RenameLayerAsync"/> or
    /// <see cref="DeleteLayerAsync"/> is still open; a second request is
    /// ignored until it closes.</summary>
    private bool _layerDialogOpen;

    /// <summary>Layer actions need an open edit and no save in flight.</summary>
    private bool CanEditLayers => IsEditing && !IsSavingEdit;

    /// <summary>
    /// Append a new layer to the edit and show it. Does nothing when not
    /// editing or while saving; a keymap with no layers yields no new layer
    /// (and so no shown-layer change).
    /// </summary>
    public void AddLayer()
    {
        if (!CanEditLayers) return;
        if (Edit!.AddLayer() is int added) ApplyActiveLayer(added);
    }

    /// <summary>
    /// Copy the layer at <paramref name="layerIndex"/> right after itself and
    /// show the copy. Does nothing when not editing, while saving, or when the
    /// index falls outside the edited layers.
    /// </summary>
    public void DuplicateLayer(int layerIndex)
    {
        if (!CanEditLayers) return;
        if (Edit!.DuplicateLayer(layerIndex) is int copy) ApplyActiveLayer(copy);
    }

    /// <summary>
    /// Move the layer at <paramref name="layerIndex"/> by
    /// <paramref name="delta"/> places and keep showing it (the board follows
    /// the layer, not the position). Does nothing when not editing, while
    /// saving, or when the move is refused.
    /// </summary>
    public void MoveLayer(int layerIndex, int delta)
    {
        if (!CanEditLayers) return;
        var to = layerIndex + delta;
        if (Edit!.MoveLayer(layerIndex, to)) ApplyActiveLayer(to);
    }

    /// <summary>
    /// Rename the layer at <paramref name="layerIndex"/> through the rename
    /// dialog. Does nothing when not editing, while saving, when the index
    /// falls outside the edited layers, when no callback is wired up, or while
    /// an earlier layer dialog is still open. A cancelled dialog (null) changes
    /// nothing; so does an answer once the edit it was opened on has been
    /// replaced underneath the await.
    /// </summary>
    public async Task RenameLayerAsync(int layerIndex)
    {
        if (!CanEditLayers) return;
        var edit = Edit!;
        var layers = edit.Session.Current.Layers;
        if (layerIndex < 0 || layerIndex >= layers.Count) return;

        if (RenameLayerRequested is not { } callback) return;
        if (_layerDialogOpen) return;

        _layerDialogOpen = true;
        try
        {
            var answer = await callback(new RenameLayerRequest(layerIndex, layers[layerIndex].Name));

            if (answer is not null && ReferenceEquals(Edit, edit))
                edit.RenameLayer(layerIndex, answer);
        }
        finally
        {
            _layerDialogOpen = false;
        }
    }

    /// <summary>
    /// Delete the layer at <paramref name="layerIndex"/>, no confirmation:
    /// when the check allows it, delete it, show the layer before it (layer 0
    /// when layer 0 went), and report it in the status bar — with the cleared
    /// key count when keys pointed at the layer. When it is refused, show the
    /// blockers through <see cref="LayerDeleteBlockedRequested"/> and change
    /// nothing. Does nothing when not editing, while saving, when the index
    /// falls outside the edited layers, or while an earlier layer dialog is
    /// still open. A writer refusal toasts why and changes nothing.
    /// </summary>
    public async Task DeleteLayerAsync(int layerIndex)
    {
        if (!CanEditLayers) return;
        var edit = Edit!;
        var layers = edit.Session.Current.Layers;
        if (layerIndex < 0 || layerIndex >= layers.Count) return;
        var name = layers[layerIndex].Name;

        try
        {
            if (edit.CheckDeleteLayer(layerIndex) is not { } check) return;

            if (!check.CanDelete)
            {
                if (LayerDeleteBlockedRequested is { } blocked && !_layerDialogOpen)
                {
                    _layerDialogOpen = true;
                    try
                    {
                        await blocked(new LayerDeleteBlockedRequest(name, check.Blockers)
                        {
                            ComboEditsPending = edit.HasPendingComboEdits,
                        });
                    }
                    finally
                    {
                        _layerDialogOpen = false;
                    }
                }
                return;
            }

            if (edit.DeleteLayer(layerIndex))
            {
                ApplyActiveLayer(Math.Max(layerIndex - 1, 0));
                StatusMessage = check.ClearedBindings > 0
                    ? Loc.Instance.Format("Edit_LayerDeletedFormat", name, check.ClearedBindings)
                    : Loc.Instance.Format("Edit_LayerDeletedNoRefsFormat", name);
            }
        }
        catch (ZmkKeymapParseException ex)
        {
            // The text refused the change; the edit and its undo history are
            // untouched, so the user stays put.
            Toast.Show(Loc.Instance.Format("Edit_LayerActionFailedFormat", ex.Message));
            DiagnosticLog.Warn("MainVM", $"The layer action on '{name}' failed: {ex}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditLayers))]
    private void NewLayer() => AddLayer();

    [RelayCommand(CanExecute = nameof(CanEditLayers))]
    private void DuplicateShownLayer() => DuplicateLayer(ActiveLayerIndex);

    private bool CanMoveShownLayerLeft => CanEditLayers && ActiveLayerIndex > 0;

    [RelayCommand(CanExecute = nameof(CanMoveShownLayerLeft))]
    private void MoveShownLayerLeft() => MoveLayer(ActiveLayerIndex, -1);

    private bool CanMoveShownLayerRight =>
        CanEditLayers && Edit is { } edit && ActiveLayerIndex < edit.Session.Current.Layers.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveShownLayerRight))]
    private void MoveShownLayerRight() => MoveLayer(ActiveLayerIndex, +1);

    [RelayCommand(CanExecute = nameof(CanEditLayers))]
    private Task RenameShownLayerAsync() => RenameLayerAsync(ActiveLayerIndex);

    [RelayCommand(CanExecute = nameof(CanEditLayers))]
    private Task DeleteShownLayerAsync() => DeleteLayerAsync(ActiveLayerIndex);

    /// <summary>Re-query the six layer commands — the "⋮" menu items and the
    /// shortcuts ride their enable state.</summary>
    private void NotifyLayerCommandState()
    {
        NewLayerCommand.NotifyCanExecuteChanged();
        DuplicateShownLayerCommand.NotifyCanExecuteChanged();
        MoveShownLayerLeftCommand.NotifyCanExecuteChanged();
        MoveShownLayerRightCommand.NotifyCanExecuteChanged();
        RenameShownLayerCommand.NotifyCanExecuteChanged();
        DeleteShownLayerCommand.NotifyCanExecuteChanged();
    }

    // ---- saving the edit ----

    /// <summary>
    /// Invoked between building the save plan and writing it: the callback (set
    /// by <c>App.axaml.cs</c>) owns the review dialog's lifecycle and answers
    /// <see langword="null"/> to abandon the save, else the commit message the
    /// user approved. No callback means approved with the generated message —
    /// in tests, saving goes straight through.
    /// </summary>
    public Func<KeymapReviewRequest, Task<KeymapReviewAnswer?>>? ReviewChangesRequested { get; set; }

    /// <summary>
    /// Invoked when the target changed underneath the edit: the callback (set by
    /// <c>App.axaml.cs</c>, part 9) owns the conflict dialog and answers reload /
    /// overwrite / cancel. Null means <see cref="KeymapSaveConflictChoice.Cancel"/>
    /// — nothing is ever written over a changed target silently.
    /// </summary>
    public Func<KeymapConflictRequest, Task<KeymapSaveConflictChoice>>? SaveConflictRequested { get; set; }

    /// <summary>
    /// Invoked after a save that added, deleted or moved layers and with them
    /// the per-layer settings, when something broke on the way: one translated
    /// line per notice, in notice order. The callback (set by
    /// <c>App.axaml.cs</c>, part 7) owns the dialog's lifecycle. Null — as in
    /// tests — means nothing is shown.
    /// </summary>
    public Func<IReadOnlyList<string>, Task>? LayerSettingsNoticeRequested { get; set; }

    /// <summary>True while a save is in flight; gates Save and Discard so
    /// neither can run against an edit that is being written.</summary>
    [ObservableProperty]
    private bool _isSavingEdit;

    partial void OnIsSavingEditChanged(bool value) => NotifyEditCommandState();

    /// <summary>Only an edit with something to write can be saved, and only
    /// while no save is already running.</summary>
    private bool CanSaveEdit => Edit is { HasChanges: true } && !IsSavingEdit;

    private bool CanDiscardEdit => IsEditing && !IsSavingEdit;

    /// <summary>
    /// Write the open edit where it came from: freeze it into a
    /// <see cref="KeymapSavePlan"/> (a writer refusal toasts and keeps the user
    /// in edit mode), pass it by the review callback, then save it through
    /// <see cref="KeymapEditViewModel.SaveAsync"/>.
    /// <list type="bullet">
    /// <item><description>saved — leave edit mode, reload what was written, stay
    /// on the layer the editor was on, and report it in the status bar (toasts
    /// are for errors only);</description></item>
    /// <item><description>conflict — ask: reload loses the changes and shows the
    /// target as it stands, overwrite writes anyway, cancel keeps editing;
    /// </description></item>
    /// <item><description>failed — toast the translated reason and keep editing
    /// with the changes intact.</description></item>
    /// </list>
    /// Every reload happens <em>after</em> <see cref="LeaveEdit"/>: the edit-mode
    /// locks refuse <see cref="LoadLayoutFromPath"/> and
    /// <see cref="OpenGitHubKeymapAsync"/> while an edit is open.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private async Task SaveEditAsync()
    {
        var edit = Edit;
        if (edit is null) return;

        KeymapSavePlan plan;
        try
        {
            plan = edit.BuildSavePlan();
        }
        catch (Exception ex) when (ex is ZmkKeymapParseException or ArgumentException)
        {
            // The text was never written; the edit and its undo history are
            // untouched, so the user stays put and can keep working.
            Toast.Show(Loc.Instance.Format("Edit_SaveRefusedFormat", ex.Message));
            DiagnosticLog.Warn("MainVM", $"Building the keymap save plan failed: {ex}");
            return;
        }

        string? commitMessage = null;
        if (ReviewChangesRequested is { } review)
        {
            var answer = await review(new KeymapReviewRequest(
                plan,
                edit.Source.TargetName,
                edit.Source.IsGitHub ? edit.CommitMessageFor(plan) : null));
            if (answer is null) return;
            commitMessage = answer.CommitMessage;
        }

        IsSavingEdit = true;
        try
        {
            var overwrite = false;

            while (true)
            {
                var outcome = await edit.SaveAsync(plan, overwrite, commitMessage);

                if (outcome is KeymapEditSaveOutcome.Saved saved)
                {
                    // A reload always lands on layer 0; a save must not move the
                    // user off the layer they were editing — unless that layer's
                    // tab is hidden, then the nearest shown one takes over.
                    var layer = ActiveLayerIndex;

                    // The layers just moved on disk for good — renumber this
                    // keyboard's per-layer settings with them and collect what
                    // the move could not carry over.
                    var notices = edit.Session.PendingStructureEdits.Count > 0
                        ? MoveLayerSettings(edit.Session.BaselineLayerMap())
                        : Array.Empty<LayerSettingsNotice>();

                    // Combo labels follow their combo the same way layer
                    // settings follow their layers — before the edit closes.
                    MoveComboLabelOverrides(plan.ComboChanges);

                    LeaveEdit();
                    LoadLayoutFromPath(saved.ReloadPath, edit.Source.Entry);
                    ApplyActiveLayer(NearestShownLayer(layer));
                    StatusMessage = Loc.Instance.Format(
                        "Status_EditSavedFormat", Path.GetFileName(saved.ReloadPath));

                    if (notices.Count > 0 && LayerSettingsNoticeRequested is { } notify)
                        await notify(notices.Select(NoticeLine).ToList());

                    return;
                }

                if (outcome is KeymapEditSaveOutcome.Failed failed)
                {
                    Toast.Show(failed.Message);
                    return;
                }

                // Conflict: the target moved since the edit opened.
                var choice = SaveConflictRequested is { } askConflict
                    ? await askConflict(new KeymapConflictRequest(
                        edit.Source.IsGitHub, edit.Source.TargetName))
                    : KeymapSaveConflictChoice.Cancel;

                if (choice == KeymapSaveConflictChoice.Cancel) return;

                if (choice == KeymapSaveConflictChoice.Reload)
                {
                    LeaveEdit();
                    if (edit.Source.GitHub is { } gitHubRef)
                        await OpenGitHubKeymapAsync(gitHubRef);
                    else
                        LoadLayoutFromPath(edit.Source.LocalPath, edit.Source.Entry);
                    return;
                }

                overwrite = true;
            }
        }
        finally
        {
            IsSavingEdit = false;
        }
    }

    /// <summary>
    /// Carries this keyboard's persisted per-layer settings across a save that
    /// added, deleted or moved layers: <paramref name="map"/> is the just-closed
    /// edit's <see cref="KeymapEditSession.BaselineLayerMap"/>, applied to the
    /// settings as they stand at that moment. Every in-memory copy is refreshed
    /// from the rewritten settings so the UI reads the new numbers — palette and
    /// key labels wholesale, the auto-switch rules and the mouse layer through
    /// their engines (which persist their own slice). Returns what the remap
    /// dropped or cannot fix; see <see cref="LayerSettingsRemap"/>.
    /// </summary>
    private IReadOnlyList<LayerSettingsNotice> MoveLayerSettings(IReadOnlyList<int?> map)
    {
        LayerSettingsRemapResult? remapped = null;
        PersistSetting(s =>
        {
            remapped = LayerSettingsRemap.Apply(s, _profile.Id, map);
            return remapped.Settings;
        });
        if (remapped is null) return Array.Empty<LayerSettingsNotice>();

        var settings = remapped.Settings;
        LayerColorPalette.SetOverrides(settings.LayerColors);
        KeyLabelOverrides.SetOverrides(settings.KeyLabelOverrides);
        AutoSwitch.ApplyAppLayerRules(settings.AppLayerRules.TryGetValue(_profile.Id, out var rules)
            ? rules
            : Array.Empty<AppLayerRule>());
        if (settings.MouseLayer.TryGetValue(_profile.Id, out var mouse))
            MouseLayer?.ApplySettings(mouse);

        return remapped.Notices;
    }

    /// <summary>
    /// Carries this keyboard's combo label overrides across a save that moved
    /// or removed combos: a rewritten combo takes its label to its new
    /// key-position key (when nothing already lives there), and a deleted
    /// combo's label goes with it. Overrides of combos the save only renamed
    /// or retargeted (same positions) stay put, and adds have no label yet.
    /// Persists once, and only when something actually moved.
    /// </summary>
    private void MoveComboLabelOverrides(IReadOnlyList<KeymapComboChange> changes)
    {
        var moved = false;
        foreach (var change in changes)
        {
            switch (change.Edit)
            {
                case ComboEdit.Change:
                {
                    if (change.Old is not { } old || change.New is not { } combo) continue;
                    var oldKey = ComboLabelOverrides.KeyPositionsToKey(old.KeyPositions);
                    var newKey = ComboLabelOverrides.KeyPositionsToKey(combo.KeyPositions);
                    if (oldKey == newKey) continue;

                    var existing = ComboLabelOverrides.Get(_profile.Id, oldKey);
                    if (existing is null) continue; // nothing to carry, and clearing an absent key is a no-op
                    if (ComboLabelOverrides.Get(_profile.Id, newKey) is null)
                        ComboLabelOverrides.Set(_profile.Id, newKey, existing);
                    ComboLabelOverrides.Set(_profile.Id, oldKey, null);
                    moved = true;
                    break;
                }
                case ComboEdit.Delete:
                {
                    if (change.Old is not { } old) continue;
                    var oldKey = ComboLabelOverrides.KeyPositionsToKey(old.KeyPositions);
                    if (ComboLabelOverrides.Get(_profile.Id, oldKey) is null) continue;

                    ComboLabelOverrides.Set(_profile.Id, oldKey, null);
                    moved = true;
                    break;
                }
            }
        }

        if (moved)
            PersistSetting(s => s with { ComboLabelOverrides = ComboLabelOverrides.Snapshot() });
    }

    /// <summary>One remap notice as its translated report line.</summary>
    private static string NoticeLine(LayerSettingsNotice notice) => notice.Kind switch
    {
        LayerSettingsNoticeKind.AppRuleRemoved => Loc.Instance.Format(
            "Edit_NoticeAppRuleRemovedFormat", notice.Name, notice.OldLayerIndex),
        LayerSettingsNoticeKind.MouseLayerCleared => Loc.Instance.Format(
            "Edit_NoticeMouseLayerClearedFormat", notice.OldLayerIndex),
        // PipelineStepCheck — pipelines are global, nothing was rewritten.
        _ => Loc.Instance.Format(
            "Edit_NoticePipelineCheckFormat", notice.Name, notice.OldLayerIndex),
    };

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
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "SelectKeyboard ignored while editing");
            return;
        }

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
            _loadedEntry = null;
            Layers.Clear();
            ActiveLayerIndex = 0;
            HasLayoutLoaded = false;
            HasUnmatchedKeymap = false;
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitchedUnloaded", profile.DisplayName);
        }
        else if (km is not null)
        {
            // Same keymap, different board: tab colors and hidden buttons are
            // per profile, so the tabs must be rebuilt from the new profile.
            RebuildLayers();
            ApplyActiveLayer(ActiveLayerIndex);
            HasUnmatchedKeymap = false;
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitched", profile.DisplayName);
        }
        else
        {
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitched", profile.DisplayName);
        }

        PersistSetting(s => s with { Keyboard = profile.Id });
        // Recents are per-profile; the surfaced list changed with the profile.
        RaiseRecentKeymapsChanged();
        DiagnosticLog.Info("MainVM", $"Keyboard profile switched to {profile.Id}");

        // Re-scope HID discovery to the new profile so a Go60 stops feeding
        // reports into a Glove80 layout (or vice versa). No-op when HID is
        // disabled or the source isn't running.
        _hid.SetActiveProfile(profile);

        // The keyboard's manifest/handlers change with the profile, and the new
        // profile excludes a different keyboard from the bus — update the matcher
        // and rebuild the routing table. Fire-and-forget; the router serializes
        // and self-logs scan failures.
        _deviceBus.SetKeyboardMatcher(_hid.KeyboardMatcher);
        _ = _router.RescanAsync();

        // Auto-load whichever entry the user last associated with this
        // keyboard (local path or GitHub reference — resolved via cache).
        // If the previously-loaded layout already fits, leave it alone.
        if (!HasLayoutLoaded)
        {
            if (TryGetStoredPathForProfile(_settingsService.Load(), profile.Id, out var storedPath, out var storedEntry))
                LoadLayoutFromPath(storedPath, storedEntry);
            PendingGitHubCheck = CheckGitHubForUpdateAsync(storedEntry);
        }
    }

    // --- Layout wizard ("My own board…", window lives in part 6) ---

    /// <summary>
    /// Everything the wizard window can't discover itself, read from live VM
    /// state: the open keymap, the user-layout ids already taken (without the
    /// <c>user:</c> prefix), the connected devices no profile claims, the
    /// folder user layouts live in, and the cache folder of the open GitHub
    /// repo + branch (null when the open keymap is local).
    /// </summary>
    public LayoutWizardContext GetLayoutWizardContext()
    {
        // One snapshot: All allocates a fresh array per read.
        var profiles = KeyboardProfileRegistry.All;
        var existingIds = profiles
            .OfType<UserLayoutProfile>()
            .Select(p => p.Source.Id)
            .ToList();
        var deviceNames = _deviceBus.Devices
            .Select(d => d.DisplayName)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !profiles.Any(p => p.NameMatches(name)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        // A GitHub entry has no local folder the user could browse to; the
        // wizard starts on the repo's cache instead.
        string? gitHubFolder =
            _gitHub is { } gitHub && GitHubKeymapRef.TryParse(_loadedEntry, out var keymap)
                ? gitHub.CacheFolderFor(keymap.Owner, keymap.Repo, keymap.Branch)
                : null;
        return new LayoutWizardContext(
            _keymap.Current, existingIds, deviceNames, UserLayoutStore.DefaultDirectory, gitHubFolder);
    }

    /// <summary>
    /// Everything the "Edit layout…" window can't discover itself: the saved
    /// layout behind the active user profile, the key captions from the open
    /// keymap, and the folder user layouts live in. Null unless a user
    /// layout is the active profile (bundled boards can't be edited).
    /// </summary>
    public LayoutEditContext? GetLayoutEditContext() =>
        _profile is UserLayoutProfile p
            ? new(p.Source, EditorLabels.For(_keymap.Current, p.KeyCount, p.Id), UserLayoutStore.DefaultDirectory)
            : null;

    /// <summary>
    /// Replaces the registry entry with the same id with a fresh profile
    /// built from <paramref name="layout"/> (appending it when the id is
    /// new) and refreshes the picker list.
    /// </summary>
    private UserLayoutProfile ReplaceUserProfile(UserLayoutFile layout)
    {
        var profile = new UserLayoutProfile(layout);
        KeyboardProfileRegistry.SetUserProfiles(
            KeyboardProfileRegistry.All
                .OfType<UserLayoutProfile>()
                .Where(p => !string.Equals(p.Source.Id, layout.Id, StringComparison.Ordinal))
                .Append((IKeyboardProfile)profile));
        OnPropertyChanged(nameof(AvailableKeyboards));
        return profile;
    }

    /// <summary>
    /// Shows a profile the picker could switch to — or one that just replaced
    /// the active profile under the same id. That second case needs an
    /// in-place refresh: <see cref="SelectKeyboard"/> returns early on an
    /// identical id, which would leave the old geometry on screen. No status
    /// message, persistence, or HID/bus re-scope — the device the picker
    /// points at doesn't change.
    /// </summary>
    private void ShowProfile(IKeyboardProfile profile)
    {
        if (!string.Equals(profile.Id, _profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            SelectKeyboard(profile);
            return;
        }

        _profile = profile;
        SelectedKeyboard = profile;
        BuildKeysFromProfile();
        _push.SetActiveProfile(profile);

        var km = _keymap.Current;
        if (km is not null && km.LayerCount > 0 && km.Layers[0].Bindings.Count == profile.KeyCount)
        {
            // Same keymap, edited board: repaint so the rebuilt keys carry
            // their labels again, exactly like the switch path does.
            RebuildLayers();
            ApplyActiveLayer(ActiveLayerIndex);
        }
    }

    /// <summary>
    /// Registers a saved wizard layout as a user profile (replacing any
    /// existing layout with the same id), selects it through the normal
    /// picker path, and loads the keymap the wizard picked when it found one.
    /// </summary>
    public void ApplyLayoutWizardResult(LayoutWizardResult result)
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "ApplyLayoutWizardResult ignored while editing");
            return;
        }

        var previousProfileId = _profile.Id;
        var profile = ReplaceUserProfile(result.Layout);
        ShowProfile(profile);

        if (result.KeymapPathToLoad is not null)
            LoadLayoutFromPath(result.KeymapPathToLoad);
        else if (_loadedEntry is string openEntry)
            // The wizard built this board around the keymap already open (it
            // fits, so the switch kept it loaded): remember it for the new
            // board, and drop it from the board it was opened on unmatched.
            BookEntryForProfile(openEntry, takeFromProfileId: previousProfileId);
        HasUnmatchedKeymap = false;

        DiagnosticLog.Info("MainVM", $"Added user layout {profile.Id} ({profile.KeyCount} keys)");
    }

    /// <summary>
    /// Applies the file the "Edit layout…" window saved: swaps the registry
    /// entry for the same id and refreshes the board in place, repainting the
    /// open keymap when it still fits the edited geometry.
    /// </summary>
    public void ApplyEditedLayout(UserLayoutFile layout)
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "ApplyEditedLayout ignored while editing");
            return;
        }

        ShowProfile(ReplaceUserProfile(layout));

        DiagnosticLog.Info("MainVM", $"Edited user layout user:{layout.Id} ({layout.Keys.Count} keys)");
    }

    /// <summary>Starts the layout wizard via the window callback.</summary>
    [RelayCommand]
    private void AddOwnBoard()
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "AddOwnBoard ignored while editing");
            return;
        }
        AddOwnBoardRequested?.Invoke();
    }

    /// <summary>Opens the "Edit layout…" window via the window callback.</summary>
    [RelayCommand]
    private void EditLayout()
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "EditLayout ignored while editing");
            return;
        }
        EditLayoutRequested?.Invoke();
    }

    /// <summary>Opens the GitHub keymap picker window via the window callback.</summary>
    [RelayCommand]
    private void OpenFromGitHub() => OpenFromGitHubRequested?.Invoke();

    /// <summary>
    /// Turns a stored settings entry into a file to load. A GitHub reference
    /// resolves through the service's local cache only — never the network
    /// (a cold cache simply doesn't resolve); a local path resolves when the
    /// file is on disk.
    /// </summary>
    private bool TryResolveEntry(string entry, out string path)
    {
        if (GitHubKeymapRef.TryParse(entry, out var keymap))
        {
            var cached = _gitHub?.LocalPathFor(keymap);
            path = cached ?? "";
            return cached is not null;
        }

        path = entry;
        return File.Exists(entry);
    }

    /// <summary>
    /// Returns the persisted entry for the given profile plus the file it
    /// resolves to (see <see cref="TryResolveEntry"/>). If the entry points
    /// at a file that has gone missing — or a GitHub keymap whose cache copy
    /// is gone — logs a warning and removes the stale entry from settings so
    /// startup doesn't keep complaining about it.
    /// </summary>
    private bool TryGetStoredPathForProfile(UserSettings s, string profileId, out string path, out string entry)
    {
        path = "";
        entry = "";
        if (!s.LayoutJsonPaths.TryGetValue(profileId, out var stored) || string.IsNullOrWhiteSpace(stored))
            return false;
        entry = stored;
        if (TryResolveEntry(stored, out path))
            return true;

        // A GitHub reference without a cache copy (signed out, cache wiped)
        // is still valid: keep it, the background check fetches it.
        if (GitHubKeymapRef.IsGitHubEntry(stored))
        {
            DiagnosticLog.Info("MainVM", $"Stored layout for {profileId} has no cached copy yet: {stored}");
            return false;
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
        // While editing, the tabs show the edited keymap, hidden layers
        // included, and carry the per-layer actions; outside an edit they
        // show the loaded keymap with the persisted hidden flags.
        var edit = Edit;
        var km = edit?.Keymap ?? _keymap.Current;
        if (km is null) return;
        var editing = edit is not null;
        var layerCount = km.Layers.Count;
        var hiddenLayers = editing ? null : _settingsService.Load().HiddenLayers;
        foreach (var layer in km.Layers)
        {
            var layerVm = new LayerViewModel(
                layer.Index,
                $"{layer.Index} : {layer.Name}",
                LayerColorFor(layer.Index),
                SelectLayer,
                editing
                    ? new LayerTabActions(
                        i => _ = RenameLayerAsync(i), DuplicateLayer, MoveLayer,
                        i => _ = DeleteLayerAsync(i), layerCount)
                    : null);
            if (hiddenLayers is { } hidden)
                layerVm.IsShown = !HiddenLayers.IsHidden(hidden, _profile.Id, layer.Index);
            Layers.Add(layerVm);
        }
    }

    private void SelectLayer(int index)
    {
        ApplyActiveLayer(index);
    }

    /// <summary>Routes a layer push through the HID pipeline. Test-tab plumbing for SettingsViewModel.</summary>
    public void PushLayerToKeyboard(int index) => _hid.PushLayer(index);

    /// <summary>
    /// Runs an action pipeline off the UI thread (steps await I/O + delays).
    /// The Test button in the pipeline editor calls this with the in-progress
    /// (possibly unsaved) pipeline, so testing exercises the real runner without
    /// persisting first. Same path an inbound signal.fire takes.
    /// </summary>
    public void RunPipeline(ActionPipeline pipeline)
    {
        _ = Task.Run(async () =>
        {
            try { await _pipelineRunner.RunAsync(pipeline).ConfigureAwait(false); }
            catch (Exception ex) { DiagnosticLog.Warn("Pipeline", $"test run '{pipeline.Name}' failed: {ex.Message}"); }
        });
    }

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

    /// <summary>The edit session's id of the shown layer (see
    /// <see cref="KeymapEditSession.LayerIdAt"/>), so <see cref="OnEditChanged"/>
    /// can follow the layer through undo and redo; null outside an edit.</summary>
    private int? _shownLayerId;

    private void ApplyActiveLayer(int index)
    {
        // While editing, the edited config paints instead of the loaded one.
        var edit = Edit;
        var km = edit?.Keymap ?? _keymap.Current;
        if (km is null) return;
        if (index < 0 || index >= km.LayerCount) return;

        var previousLayerId = _shownLayerId;
        ActiveLayerIndex = index;
        _shownLayerId = edit?.Session.LayerIdAt(index);

        // A selection is a key on the layer it was made on: showing another
        // layer drops it. Undo/redo and the repaint after a save keep the
        // same layer id, so they keep it.
        if (edit is not null && previousLayerId is not null && previousLayerId != _shownLayerId)
            edit.ClearSelection();

        for (int i = 0; i < Keys.Count; i++)
        {
            if (edit is null)
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
                Keys[i].IsEditChanged = false;
                Keys[i].IsEditSelected = false;
                continue;
            }

            // Edit mode shows the layer's OWN binding — &trans paints ▽ and
            // &none empty, no fall-through — and ignores per-key label
            // overrides, so the user sees exactly what a save will write.
            var bindings = km.Layers[index].Bindings;
            var literal = i < bindings.Count ? bindings[i] : KeyBinding.Transparent;
            if (literal.Behavior is "&trans" or "&none")
            {
                Keys[i].ApplyBinding(
                    literal,
                    activeLayerIndex: index,
                    targetLayer: null,
                    targetLayerName: null,
                    profileId: _profile.Id,
                    ignoreLabelOverride: true);
            }
            else
            {
                var r = km.Resolve(index, i);
                Keys[i].ApplyBinding(
                    r.Binding,
                    activeLayerIndex: index,
                    targetLayer: r.TargetLayer,
                    targetLayerName: r.TargetLayerName,
                    profileId: _profile.Id,
                    holdTap: r.HoldTap,
                    macros: km.MacrosByName,
                    ignoreLabelOverride: true);
            }
            Keys[i].IsEditChanged = edit.IsKeyChanged(index, i);
            Keys[i].IsEditSelected = edit.SelectedKeyIndex == i;
        }

        for (int i = 0; i < Layers.Count; i++)
            Layers[i].IsSelected = Layers[i].Index == index;

        // The shown layer decides whether the layer moves are possible.
        NotifyLayerCommandState();
    }

    /// <summary>
    /// Walks the shown keymap's combos once and populates the legend
    /// collection plus each key's <see cref="KeyViewModel.SetCombos"/>. While
    /// a keymap edit is open the <em>edited</em> config paints, and every tile
    /// is editable and carries its pending-change mark. Called once per keymap
    /// load from <see cref="LoadLayoutFromPath"/> and from every edit-mode
    /// repaint (<see cref="StartEdit"/>, <see cref="OnEditChanged(object?,
    /// EventArgs)"/>, <see cref="LeaveEdit"/>) — combos aren't layer-scoped, so
    /// nothing here repeats on a plain layer switch. The participating-key
    /// labels in the legend's <c>ParticipatingKeysText</c> reflect whatever
    /// layer was active when the legend was rebuilt.
    /// </summary>
    private void BuildCombosForConfig()
    {
        SetHighlightedCombos(Array.Empty<int>());
        Combos.Clear();

        // While editing, the legend shows the edit — combo badges follow too.
        var edit = Edit;
        var km = edit?.Keymap ?? _keymap.Current;
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
            var tile = new ComboViewModel(i + 1, combo, LabelLookup, km.MacrosByName, ov);
            if (edit is not null)
            {
                tile.IsEditable = true;
                tile.IsChanged = edit.IsComboChanged(i);
            }
            Combos.Add(tile);
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
        // Cross-device discovery rides the same live-tracking lifecycle as HID.
        // The bus discovers (and the router builds the inventory) regardless of
        // the master routing toggle — the toggle only gates forwarding — so the
        // Settings UI can surface availability with routing still off.
        _deviceBus.Start();
        _router.Start();
        // Signal listening rides the same lifecycle: it attaches per-device
        // handlers off the router's inventory (reconciled on RoutesChanged) and
        // runs the bound pipeline on an inbound signal.fire.
        _signalDispatcher.Start();
        // Initial label sync — the source may already have settled the
        // active state before our subscription was attached above.
        OnActiveSourceChanged();
    }

    private void StopKeyEventTracking(bool isAppExit = false)
    {
        // On app exit, always return the keyboard to the base layer — once LViz
        // is gone the board has no way to leave an app-pushed layer, so it would
        // otherwise stay stuck on a non-base layer. This is an absolute rule and
        // supersedes the mouse-layer revert. On a plain live-tracking toggle-off
        // (re-enable possible) we instead revert any in-flight mouse-layer push
        // *before* tearing down HID — otherwise the engine's _preMoveLayer stays
        // set across the stop and the next enable captures the (still-held) mouse
        // layer as the new pre-move layer, getting stuck pushing/reverting onto it.
        if (isAppExit)
            _push.ForceBaseLayerOnExit(TimeSpan.FromMilliseconds(500));
        else
            _push.RevertMouseLayerForShutdown(TimeSpan.FromMilliseconds(500));
        // Stop signal listening first (it holds per-device subscriptions), then
        // the router before the bus so forwarding/subscriptions are torn down
        // before the underlying handles close.
        _signalDispatcher.Stop();
        _router.Stop();
        _deviceBus.Stop();
        _hid.Stop();
        IsHidSourceActive = false;
        LayerSourceHint = "";
    }

    private void OnActiveLayerChanged(int layer)
    {
        // The view keeps showing the layer the editor is on while editing;
        // the board's own switches must not repaint underneath it.
        if (IsEditing) return;
        if (!IsAutoLayerSwitchEnabled) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyActiveLayer(layer));
    }

    // RoutesChanged fires on a background scan thread; marshal the availability
    // notification onto the UI thread so bindings update safely. The inventory
    // itself is read on demand from the router (immutable snapshot).
    private void OnCapabilityRoutesChanged()
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(IsDeviceRoutingAvailable)));

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
