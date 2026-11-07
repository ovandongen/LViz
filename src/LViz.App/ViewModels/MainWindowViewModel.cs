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
    /// Right-click on a key opens the per-key label-override editor, inert
    /// while editing a keymap, where the label dialog doesn't apply. The dialog
    /// itself is the <see cref="IMainWindowShell"/>'s; the answer is applied here.
    /// </summary>
    public Action<int>? OnKeyRightTapped => IsEditing ? null : OnKeyRightTappedInternal;

    private async void OnKeyRightTappedInternal(int keyIndex)
    {
        var layerIndex = ActiveLayerIndex;
        var ctx = GetKeyLabelEditContext(layerIndex, keyIndex);
        if (ctx is null) return;
        var answer = await _shell.EditKeyLabelAsync(layerIndex, keyIndex, ctx.Value.DefaultHint, ctx.Value.Existing);
        if (answer is not null)
            SetKeyLabelOverride(layerIndex, keyIndex, answer.Value);
    }

    private readonly ISettingsService _settingsService;

    // Owns the loaded keymap + resolution. Null Current means no layout loaded
    // (callers treat that as "every binding is Transparent").
    private readonly IKeymapStateService _keymap;

    // GitHub keymap opener (sign-in, repo refresh, open). Null in tests that
    // don't wire it — GitHub entries then only ever load from nothing and a
    // click on one toasts "GitHub is not available".
    private readonly IGitHubKeymapService? _gitHub;
    private readonly IKeymapEditDialogs _editDialogs;
    private readonly IMainWindowShell _shell;

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

    /// <summary>
    /// The device stack (HID pipeline, capability bus + router, action pipelines,
    /// layer-push coordinator, IPC); <see cref="DeviceStack"/> owns how it is
    /// assembled and torn down. Bind to its parts directly.
    /// </summary>
    internal DeviceStack Devices { get; }
    private readonly IHostActionExecutor _hostActionExecutor;

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
    public ICapabilityRouter CapabilityRouter => Devices.Router;

    /// <summary>True when ≥2 protocol-capable devices expose a routable trigger→handler pair.</summary>
    public bool IsDeviceRoutingAvailable => Devices.Router.IsRoutingAvailable;

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
    public string ActiveLayerTintColor => LayerTabs.ColorFor(ActiveLayerIndex);


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

    /// <summary>The layer tab strip: tabs, colours, hidden layers, edit-mode tab actions.</summary>
    public LayerTabsViewModel LayerTabs { get; }

    /// <summary>The combo legend: tiles, overlay toggle, label overrides, hover highlight.</summary>
    public ComboLegendViewModel ComboLegend { get; }

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
    /// Sets or clears a per-(layer, key) label override for the currently
    /// active keyboard profile. Persists to
    /// <see cref="UserSettings.KeyLabelOverrides"/> through the same
    /// PersistSetting → SettingsService.Save → AtomicFile chain
    /// the layer colour override uses, then re-applies the active
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
        // Rebuilds every key's
        // labels against the freshly-set static.
        if (_keymap.HasLayout) ApplyActiveLayer(ActiveLayerIndex);
    }

    [RelayCommand]
    private void ToggleWindowsModifierStyle() => IsWindowsModifierStyle = !IsWindowsModifierStyle;

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

    /// <summary>Path of the layout JSON the user currently has loaded, or null if none.</summary>
    public string? LoadedLayoutPath => _keymap.LoadedPath;

    /// <summary>
    /// Where keymaps come from (booking, recents, entry resolution, the GitHub
    /// check); this view-model only applies a load to the screen.
    /// </summary>
    internal KeymapLibrary Library { get; }

    /// <summary>The recents flyout of the load menu; bind to its parts directly.</summary>
    public RecentKeymapsViewModel Recents { get; }

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

    /// <summary>
    /// The app→layer auto-switch engine, exposed directly. The settings VM and
    /// window bind straight to its observable state — the engine is already an
    /// ObservableObject and everything here is App-layer, so the old
    /// pass-through facade (and the PropertyChanged relay that fed it) bought
    /// nothing but a redundant hop.
    /// </summary>
    public AutoSwitchEngine AutoSwitch => Devices.Push.AutoSwitch;

    /// <summary>
    /// The mouse-movement → layer push engine, exposed directly (mirrors
    /// <see cref="AutoSwitch"/>). The settings VM reads its
    /// <see cref="IMouseLayerEngine.CurrentSettings"/> and calls
    /// <see cref="IMouseLayerEngine.ApplySettings"/> on it directly — no MainVM
    /// pass-through facade. Null when no mouse-idle monitor is wired up (tests);
    /// callers fall back to the persisted per-profile settings in that case.
    /// </summary>
    public IMouseLayerEngine? MouseLayer => Devices.Push.MouseLayer;

    public MainWindowViewModel(
        ISettingsService settingsService,
        IActiveWindowMonitor? activeWindowMonitor = null,
        IMouseIdleMonitor? mouseIdleMonitor = null,
        IKeymapStateService? keymapState = null,
        ICapabilityControl? capabilityControl = null,
        IHostActionExecutor? hostActionExecutor = null,
        IGitHubKeymapService? gitHub = null,
        KeymapBackupStore? keymapBackups = null,
        IKeymapEditDialogs? editDialogs = null,
        IMainWindowShell? shell = null)
    {
        _settingsService = settingsService;
        Library = new KeymapLibrary(settingsService, gitHub);
        _editDialogs = editDialogs ?? new UnavailableKeymapEditDialogs();
        _shell = shell ?? new UnavailableMainWindowShell();
        _keymap = keymapState ?? new KeymapStateService();
        _gitHub = gitHub;
        _keymapBackups = keymapBackups ?? new KeymapBackupStore(KeymapBackupStore.DefaultRoot);
        var s = settingsService.Load();
        _profile = KeyboardProfileRegistry.TryResolve(s.Keyboard, out var p) ? p : new CorneProfile();
        Recents = new RecentKeymapsViewModel(
            Library,
            () => _profile.Id,
            () => _loadedEntry,
            keymap => _ = OpenGitHubKeymapAsync(keymap),
            path => LoadLayoutFromPath(path),
            fileName =>
            {
                SetLoadStatusBase(null);
                StatusMessage = Loc.Instance.Format("Status_RecentKeymapRemovedFormat", fileName);
            });
        _selectedKeyboard = _profile;
        _isAlwaysOnTop = s.AlwaysOnTop;
        _colorTrayIconByActiveLayer = s.ColorTrayIconByActiveLayer;
        _useRingKeyEditor = s.RingKeyEditor;
        _isLiveHighlightingEnabled = s.LiveKeyHighlighting;
        _isAutoLayerSwitchEnabled = s.AutoLayerSwitch;
        _backgroundOpacity = Math.Clamp(s.BackgroundOpacity, 0.0, 1.0);
        if (!string.IsNullOrWhiteSpace(s.HotkeyKey))
            _hotkeyKey = s.HotkeyKey;

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
        ComboLegend = new ComboLegendViewModel(
            Keys, BoardStyle, s.ComboOverlayVisible, () => _profile.Id, settingsService, _shell);
        LayerTabs = new LayerTabsViewModel(() => _profile.Id, settingsService);
        LayerTabs.LayerPicked += OnLayerPicked;
        LayerTabs.ColorsChanged += OnLayerColorsChanged;
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

        _hostActionExecutor = hostActionExecutor ?? new HostActionExecutor();
        Devices = DeviceStack.Create(
            _profile, settingsService, () => ActiveLayerIndex,
            activeWindowMonitor, mouseIdleMonitor, capabilityControl, _hostActionExecutor);
        Devices.Hid.ActiveLayerChanged += OnActiveLayerChanged;
        Devices.Hid.ConnectionChanged += OnActiveSourceChanged;
        Devices.Router.RoutesChanged += OnCapabilityRoutesChanged;
        Devices.Push.KeyPositionForUi += OnKeyPositionForHighlight;
        // Transparent-key exit predicate reads the currently-loaded config at
        // call time, so swapping layouts or profiles needs no re-binding.
        Devices.Push.SetTransparencyPredicate(_keymap.ClassifyBinding);

        QuitCommand = new RelayCommand(() => _shell.Quit());
        ShowCommand = new RelayCommand(() => _shell.ShowWindow());
        LoadLayoutCommand = new AsyncRelayCommand(async () =>
        {
            var path = await _shell.PickKeymapFileAsync();
            if (path is not null)
                LoadLayoutFromPath(path);
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
            if (Library.BookedEntry(_profile.Id) is { } entry)
            {
                if (Library.TryResolveEntry(entry, out var path))
                    LoadLayoutFromPath(path, entry, keepBoard: true);
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
        CopyDiagnosticsCommand = new AsyncRelayCommand(CopyDiagnosticsAsync);
        SelectKeyboardCommand = new RelayCommand<IKeyboardProfile>(SelectKeyboard);
        OpenSettingsCommand = new RelayCommand(() => _shell.OpenSettings());
        OpenHelpCommand = new RelayCommand(() => _hostActionExecutor.Launch(UpdateService.WikiUrl));
        OpenShortcutsCommand = new AsyncRelayCommand(_shell.ShowShortcutsAsync);

        BuildKeysFromProfile();
    }

    /// <summary>
    /// Post-startup entry point: load last-used layout, spin up the key-event
    /// hook, and set an opening status message.
    /// </summary>
    public void InitializeAsync()
    {
        if (Library.TryGetBooked(_profile.Id, out var storedPath, out var storedEntry))
        {
            // The board's own booked keymap: loading it must not move the board.
            LoadLayoutFromPath(storedPath, storedEntry, keepBoard: true);
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
    /// <c>keepBoard</c> is true for a board's <em>booked</em> keymap — the one
    /// LViz loads by itself for the board that is already showing (startup,
    /// Refresh, the background GitHub check, a board switch's auto-load). Such
    /// a load must never switch the board: a booked keymap that doesn't fit
    /// its board is dropped instead of jumping to the board whose key count
    /// matches. Every explicit load the user picks keeps the auto-switch.
    /// </summary>
    public void LoadLayoutFromPath(string path, string? entry = null, bool keepBoard = false)
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
            Library.RemoveRecent(_profile.Id, entry);
            Recents.Refresh();
            return;
        }

        var bindingCount = loaded.FirstLayerBindingCount;
        var keyCountMismatch = loaded.LayerCount > 0 && bindingCount != _profile.KeyCount;
        // A booked keymap belongs to the board that is already showing — it
        // never gets to move the board elsewhere.
        if (keyCountMismatch && keepBoard)
        {
            DropBookedKeymap(path, entry, bindingCount);
            return;
        }
        IKeyboardProfile? autoSwitchedTo = null;
        if (keyCountMismatch)
        {
            var matching = KeyboardProfileRegistry.FindByKeyCount(bindingCount, Devices.AttachedHidNames());
            if (matching is not null)
            {
                UseProfile(matching);
                PersistSetting(s => s with { Keyboard = matching.Id });
                // Re-scope HID discovery and the bus exclusion, like a picked board.
                Devices.SwitchProfile(matching);
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

        LayerTabs.Rebuild(_keymap.Current);
        ApplyActiveLayer(0);
        ComboLegend.Rebuild(_keymap.Current);
        HasLayoutLoaded = true;

        // Booked for the profile the keymap ended up on (_profile is already
        // the auto-switch target when one ran). The *entry*, not the loaded
        // path — see the method doc.
        _loadedEntry = entry;
        Library.Book(_profile.Id, entry);
        Recents.Refresh();

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

    /// <summary>
    /// Tabs, layer state and load flags back to a bare board. Shared by the
    /// two paths that unload a keymap (<see cref="DropBookedKeymap"/> and
    /// <see cref="SwitchBoard"/>'s unload branch); each sets its own status
    /// message afterwards.
    /// </summary>
    private void ClearLoadedKeymap()
    {
        _keymap.Clear();
        _loadedEntry = null;
        LayerTabs.Clear();
        ActiveLayerIndex = 0;
        HasLayoutLoaded = false;
        HasUnmatchedKeymap = false;
    }

    /// <summary>
    /// A booked keymap that doesn't fit its board (<see cref="LoadLayoutFromPath"/>
    /// with <c>keepBoard</c>): the keymap is unloaded, the board stays put and
    /// the board's booking is dropped, so neither this session nor the next
    /// startup keeps offering a keymap that can't be shown. The board's recents
    /// list keeps the entry — the user can still open it by hand, where the
    /// auto-switch is allowed.
    /// </summary>
    private void DropBookedKeymap(string path, string entry, int bindingCount)
    {
        ClearLoadedKeymap();

        // Only this load's own entry is unbooked; a booking that already points
        // somewhere else belongs to another keymap.
        Library.Unbook(_profile.Id, entry);

        SetLoadStatusBase(null);
        StatusMessage = Loc.Instance.Format(
            "Status_BookedKeymapDoesNotFitFormat",
            Path.GetFileName(path), bindingCount, _profile.DisplayName, _profile.KeyCount);
        DiagnosticLog.Info("MainVM",
            $"Booked keymap '{path}' has {bindingCount} keys but {_profile.Id} has {_profile.KeyCount}: kept the board, dropped the booking");

        // The board is bare now, so the edit button has to notice.
        OnPropertyChanged(nameof(CanStartEdit));
        OnPropertyChanged(nameof(EditButtonTooltip));
        StartEditCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Opens a GitHub keymap: refreshes its repo through
    /// <see cref="IGitHubKeymapService"/> and loads the resulting local file
    /// under the reference as its settings entry (offline cache copies load
    /// too, with a toast). Failures only toast — the entry stays in recents
    /// because the repo may just be temporarily unreachable. An
    /// <see cref="GitHubErrorKind.Unauthorized"/> additionally raises
    /// the shell's GitHub picker so the sign-in window can open.
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
                        _ = OpenFromGitHubAsync();
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
        try
        {
            var update = await Library.CheckGitHubAsync(entry, _profile.Id, () => (_profile.Id, _loadedEntry));
            if (update is null) return;

            // An open edit holds the bytes the cache copy was parsed from;
            // reloading underneath it is what the explicit load lock refuses —
            // and no "changed on GitHub" toast may claim otherwise.
            if (IsEditing)
            {
                DiagnosticLog.Info("MainVM", "CheckGitHubForUpdateAsync ignored while editing");
                return;
            }

            LoadLayoutFromPath(update.LocalPath, entry, keepBoard: true);
            if (update.ReplacesLoaded && GitHubKeymapRef.TryParse(entry, out var keymap))
                Toast.Show(Loc.Instance.Format("Toast_GitHubUpdatedFormat", keymap.FileName));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Info("MainVM", $"Background GitHub check for '{entry}' skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a snapshot of runtime state (active settings, loaded layout,
    /// last load error) for inclusion in
    /// <see cref="DiagnosticLog.CollectDiagnosticReport"/>.
    /// </summary>
    private async Task CopyDiagnosticsAsync()
    {
        try
        {
            var report = DiagnosticLog.CollectDiagnosticReport(BuildDiagnosticsSnapshot());
            if (await _shell.CopyTextAsync(report))
                StatusMessage = Loc.Instance["Status_DiagnosticsCopied"];
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not copy diagnostics: {ex.Message}";
        }
    }

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
        Devices.Dispose();
    }

    // --- Keymap edit mode (starting, leaving and the follow-up to a save; the
    // edit's own actions live on KeymapEditViewModel) ---

    /// <summary>
    /// The open keymap edit, or null. Bind to it directly for everything the
    /// edit offers (<c>Edit.SaveEditCommand</c>, <c>Edit.NewLayerCommand</c>,
    /// ...). Setting it (from <see cref="StartEdit"/> and <see cref="LeaveEdit"/>)
    /// drives <see cref="IsEditing"/>, <see cref="CanStartEdit"/> and the Start
    /// command's enable state.
    /// </summary>
    [ObservableProperty]
    private KeymapEditViewModel? _edit;

    /// <summary>True while a keymap edit is open — locks keymap/board switching
    /// and routes key taps into the editor.</summary>
    public bool IsEditing => Edit is not null;

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
        OnPropertyChanged(nameof(CanStartEdit));
        ComboLegend.SetEditing(value);
        LayerTabs.SetEditing(value);
        StartEditCommand.NotifyCanExecuteChanged();
        DiscardEditCommand.NotifyCanExecuteChanged();
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
                LoadedLayoutPath!, _loadedEntry ?? LoadedLayoutPath!, _gitHub, _keymapBackups,
                _editDialogs, _profile, ActiveLayerIndex);
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
        edit.ShownLayerChanged += OnEditShownLayerChanged;
        edit.PropertyChanged += OnEditPropertyChanged;
        edit.Reported += OnEditReported;
        edit.Completed += OnEditCompletedAsync;

        // The tabs rebuild off the edit: every layer shows (hidden ones
        // included, purely in memory) and each tab carries the per-layer
        // actions; LeaveEdit rebuilds them from the loaded keymap and the
        // persisted setting.
        LayerTabs.Rebuild(_keymap.Current);

        ApplyActiveLayer(edit.ShownLayerIndex);

        // The legend follows the edit too: it shows the edited combos, and
        // every tile becomes editable with its pending-change mark.
        ComboLegend.Rebuild(_keymap.Current);
    }

    /// <summary>One edit change: repaint the board off the edited keymap. The
    /// edit has already moved its shown layer along (a layer is followed by id
    /// through undo and redo). Structural changes (add/delete/move/rename) also
    /// rebuild the tab strip — a plain key edit leaves the tabs alone, since
    /// rebuilding replaces the tab buttons.</summary>
    private void OnEditChanged(object? sender, EventArgs e)
    {
        if (Edit is not { } edit) return;

        if (!LayerTabs.MatchesEdit()) LayerTabs.Rebuild(_keymap.Current);
        ApplyActiveLayer(edit.ShownLayerIndex);
        // A tab color can move to another position while the shown index (and
        // with it the NotifyPropertyChangedFor above) stays put.
        OnPropertyChanged(nameof(ActiveLayerTintColor));

        // The legend re-reads the edited combos — a key edit can retake a
        // tile's labels, an undo can add or drop tiles entirely.
        ComboLegend.Rebuild(_keymap.Current);
    }

    /// <summary>The edit showed another layer on purpose (an added or moved
    /// layer): repaint it. One direction only — painting never writes back to
    /// the edit.</summary>
    private void OnEditShownLayerChanged(object? sender, EventArgs e)
    {
        if (Edit is { } edit) ApplyActiveLayer(edit.ShownLayerIndex);
    }

    private void OnEditPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(KeymapEditViewModel.SelectedKeyIndex):
                RefreshEditSelectionFlags();
                break;
            case nameof(KeymapEditViewModel.IsSaving):
                DiscardEditCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    private void OnEditReported(KeymapEditReport report)
    {
        if (report.Kind == KeymapEditReportKind.Error) Toast.Show(report.Message);
        else StatusMessage = report.Message;
    }

    /// <summary>
    /// What follows a finished edit action. A save that was written: carry the
    /// per-layer settings and combo labels along, close the edit, reload what
    /// was written, stay on the layer the editor was on and report it in the
    /// status bar. A conflict answered with "reload": drop the edit and show
    /// the target as it stands. Every reload happens <em>after</em>
    /// <see cref="LeaveEdit"/>: the edit-mode locks refuse
    /// <see cref="LoadLayoutFromPath"/> and <see cref="OpenGitHubKeymapAsync"/>
    /// while an edit is open.
    /// </summary>
    private async Task OnEditCompletedAsync(KeymapEditCompletion completion)
    {
        if (Edit is not { } edit) return;

        switch (completion)
        {
            case KeymapEditCompletion.Saved saved:
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
                ComboLegend.MoveLabelOverrides(saved.Plan.ComboChanges);

                LeaveEdit();
                LoadLayoutFromPath(saved.ReloadPath, edit.Source.Entry);
                ApplyActiveLayer(LayerTabs.NearestShownLayer(layer));
                StatusMessage = Loc.Instance.Format(
                    "Status_EditSavedFormat", Path.GetFileName(saved.ReloadPath));

                if (notices.Count > 0)
                    await _editDialogs.ShowLayerSettingsNoticesAsync(notices.Select(NoticeLine).ToList());
                break;
            }

            case KeymapEditCompletion.ReloadRequested:
                LeaveEdit();
                if (edit.Source.GitHub is { } gitHubRef)
                    await OpenGitHubKeymapAsync(gitHubRef);
                else
                    LoadLayoutFromPath(edit.Source.LocalPath, edit.Source.Entry);
                break;
        }
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
        {
            edit.Changed -= OnEditChanged;
            edit.ShownLayerChanged -= OnEditShownLayerChanged;
            edit.PropertyChanged -= OnEditPropertyChanged;
            edit.Reported -= OnEditReported;
            edit.Completed -= OnEditCompletedAsync;
            edit.Close();
        }
        Edit = null;
        LayerTabs.Rebuild(_keymap.Current);
        ApplyActiveLayer(LayerTabs.NearestShownLayer(ActiveLayerIndex));

        // Back to the loaded keymap's combos, and the tiles lose their
        // edit-mode flags with it.
        ComboLegend.Rebuild(_keymap.Current);
    }

    /// <summary>Drop the edit and its changes.</summary>
    [RelayCommand(CanExecute = nameof(CanDiscardEdit))]
    private void DiscardEdit()
    {
        if (IsEditing) LeaveEdit();
    }

    private bool CanDiscardEdit => Edit is { IsSaving: false };

    /// <summary>Left-click on a key while editing selects it for the BoardView.</summary>
    private void OnEditKeyTapped(int keyIndex) => Edit!.SelectKey(keyIndex);

    /// <summary>Double-click on a key while editing selects it and opens the
    /// key editor on it — fire and forget, the editor owns its own errors.</summary>
    private void OnEditKeyDoubleTapped(int keyIndex)
    {
        OnEditKeyTapped(keyIndex);
        _ = Edit!.EditSelectedKeyAsync();
    }

    /// <summary>Mark the editor's current selection on every key.</summary>
    private void RefreshEditSelectionFlags()
    {
        if (Edit is not { } edit) return;
        for (var i = 0; i < Keys.Count; i++)
            Keys[i].IsEditSelected = edit.SelectedKeyIndex == i;
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

    /// <summary>
    /// A board the user picked (toolbar flyout, Settings picker): the picked
    /// board always wins. A keymap opened for another board is unloaded even
    /// when its key count happens to fit the picked one — the board then
    /// shows the keymap booked for it, or nothing.
    /// </summary>
    private void SelectKeyboard(IKeyboardProfile? profile) => SwitchBoard(profile, keepFittingKeymap: false);

    /// <summary>
    /// Switches the shown board to <paramref name="profile"/>: rebuilds the
    /// keys, re-scopes the layer push, HID and the capability bus, persists
    /// the id. The open keymap is kept only when
    /// <paramref name="keepFittingKeymap"/> (the wizard path, which books the
    /// open keymap for the new board right after) and it fits the new board's
    /// key count; otherwise it is unloaded. A board left without a keymap
    /// then loads the entry booked for it.
    /// </summary>
    private void SwitchBoard(IKeyboardProfile? profile, bool keepFittingKeymap)
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "SelectKeyboard ignored while editing");
            return;
        }

        if (profile is null) return;
        if (string.Equals(profile.Id, _profile.Id, StringComparison.OrdinalIgnoreCase)) return;

        UseProfile(profile);

        var km = _keymap.Current;
        var layoutFits = km is not null
            && km.LayerCount > 0
            && km.Layers[0].Bindings.Count == profile.KeyCount;
        // A picked board never wears another board's keymap just because the
        // key counts match; only the wizard path may keep a fitting one.
        var keepKeymap = keepFittingKeymap && layoutFits;

        if (km is not null && !keepKeymap)
        {
            ClearLoadedKeymap();
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitchedUnloaded", profile.DisplayName);
        }
        else if (km is not null)
        {
            // Same keymap, different board: tab colors and hidden buttons are
            // per profile, so the tabs must be rebuilt from the new profile.
            LayerTabs.Rebuild(_keymap.Current);
            ApplyActiveLayer(ActiveLayerIndex);
            HasUnmatchedKeymap = false;
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitched", profile.DisplayName);
        }
        else
        {
            SetLoadStatusBase(null);
            StatusMessage = Loc.Instance.Format("Status_KeyboardSwitchedUnloaded", profile.DisplayName);
        }

        PersistSetting(s => s with { Keyboard = profile.Id });
        // Recents are per-profile; the surfaced list changed with the profile.
        Recents.Refresh();
        DiagnosticLog.Info("MainVM", $"Keyboard profile switched to {profile.Id}");

        // Re-scope HID discovery (and the bus exclusion) to the new profile.
        Devices.SwitchProfile(profile);

        // Auto-load whichever entry the user last associated with this
        // keyboard (local path or GitHub reference — resolved via cache).
        // Only a keymap kept through the wizard path skips this.
        if (!HasLayoutLoaded)
        {
            if (Library.TryGetBooked(profile.Id, out var storedPath, out var storedEntry))
                LoadLayoutFromPath(storedPath, storedEntry, keepBoard: true);
            PendingGitHubCheck = CheckGitHubForUpdateAsync(storedEntry);
        }
    }

    // --- Layout wizard ("My own board…") ---

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
        var deviceNames = Devices.BusDeviceNames
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
    /// <summary>Make <paramref name="profile"/> the board on screen and in the
    /// push engines (auto-switch rules, mouse layer). Every board change goes
    /// through here; a change to another board also calls
    /// <see cref="DeviceStack.SwitchProfile"/> to re-scope HID and the bus.</summary>
    private void UseProfile(IKeyboardProfile profile)
    {
        _profile = profile;
        SelectedKeyboard = profile;
        BuildKeysFromProfile();
        Devices.Push.SetActiveProfile(profile);
    }

    private void ShowProfile(IKeyboardProfile profile)
    {
        if (!string.Equals(profile.Id, _profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            // The wizard/editor path built the new board around the open
            // keymap, so a fitting keymap rides along for the re-booking.
            SwitchBoard(profile, keepFittingKeymap: true);
            return;
        }

        UseProfile(profile);

        var km = _keymap.Current;
        if (km is not null && km.LayerCount > 0 && km.Layers[0].Bindings.Count == profile.KeyCount)
        {
            // Same keymap, edited board: repaint so the rebuilt keys carry
            // their labels again, exactly like the switch path does.
            LayerTabs.Rebuild(_keymap.Current);
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
            {
                Library.Book(_profile.Id, openEntry, takeFromProfileId: previousProfileId);
                Recents.Refresh();
            }
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

    /// <summary>Runs the layout wizard and applies the layout it saved.</summary>
    [RelayCommand]
    private async Task AddOwnBoardAsync()
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "AddOwnBoard ignored while editing");
            return;
        }
        var result = await _shell.RunLayoutWizardAsync(GetLayoutWizardContext());
        if (result is not null)
            ApplyLayoutWizardResult(result);
    }

    /// <summary>Opens the "Edit layout…" window and applies the file it saved.</summary>
    [RelayCommand]
    private async Task EditLayoutAsync()
    {
        if (IsEditing)
        {
            DiagnosticLog.Info("MainVM", "EditLayout ignored while editing");
            return;
        }
        var ctx = GetLayoutEditContext();
        if (ctx is null) return;
        var result = await _shell.EditLayoutAsync(ctx);
        if (result is not null)
            ApplyEditedLayout(result);
    }

    /// <summary>Opens the GitHub keymap picker and loads the chosen keymap.</summary>
    [RelayCommand]
    private async Task OpenFromGitHubAsync()
    {
        var result = await _shell.PickGitHubKeymapAsync(Devices.AttachedHidNames);
        if (result is not null)
            LoadLayoutFromPath(result.LocalPath, result.Keymap.ToString());
    }

    /// <summary>A tab click. While editing the edit owns the shown layer and
    /// the repaint follows its event, so the board never paints a layer the edit
    /// does not know about.</summary>
    private void OnLayerPicked(int index)
    {
        if (Edit is { } edit) edit.ShowLayer(index);
        else ApplyActiveLayer(index);
    }

    /// <summary>A layer colour changed: re-resolve every key's fill (&amp;lt; / &amp;mo
    /// keys reference arbitrary layer colours, so recolouring layer 2 repaints
    /// layer 0's view too) and the press-pulse tint.</summary>
    private void OnLayerColorsChanged()
    {
        if (_keymap.HasLayout) ApplyActiveLayer(ActiveLayerIndex);
        OnPropertyChanged(nameof(ActiveLayerTintColor));
    }

    /// <summary>Routes a layer push through the HID pipeline. Test-tab plumbing for SettingsViewModel.</summary>
    public void PushLayerToKeyboard(int index) => Devices.Hid.PushLayer(index);

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
            try { await Devices.Pipelines.RunAsync(pipeline).ConfigureAwait(false); }
            catch (Exception ex) { DiagnosticLog.Warn("Pipeline", $"test run '{pipeline.Name}' failed: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Sends a 0xFD GetDeviceInfo request and awaits the 0xFE reply.
    /// Returns null when no HID is connected, on timeout, or on transport
    /// failure. Test-tab plumbing only.
    /// </summary>
    public Task<DeviceInfo?> QueryDeviceInfoAsync(TimeSpan timeout, CancellationToken ct) =>
        Devices.Hid.QueryDeviceInfoAsync(timeout, ct);

    /// <summary>
    /// Sends a 0xFB GetConfigId request and awaits the 0xFA reply.
    /// Returns null when no HID is connected, on timeout, or on transport
    /// failure. Empty string means the firmware has no
    /// CONFIG_HID_VIZ_CONFIG_ID set. Test-tab plumbing only.
    /// </summary>
    public Task<string?> QueryConfigIdAsync(TimeSpan timeout, CancellationToken ct) =>
        Devices.Hid.QueryConfigIdAsync(timeout, ct);

    private void ApplyActiveLayer(int index)
    {
        // While editing, the edited config paints instead of the loaded one.
        var edit = Edit;
        var km = edit?.Keymap ?? _keymap.Current;
        if (km is null) return;
        if (index < 0 || index >= km.LayerCount) return;

        ActiveLayerIndex = index;

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
            Keys[i].IsEditChanged = edit.Session.IsKeyChanged(index, i);
            Keys[i].IsEditSelected = edit.SelectedKeyIndex == i;
        }

        LayerTabs.SetActive(index);
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
        Devices.Start();
        // Initial label sync — the source may already have settled the
        // active state before our subscription was attached above.
        OnActiveSourceChanged();
    }

    private void StopKeyEventTracking(bool isAppExit = false)
    {
        Devices.Stop(isAppExit);
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
        var hidActive = Devices.Hid.IsConnected;
        var label = Devices.Hid.SourceLabel;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsHidSourceActive = hidActive;
            LayerSourceHint = string.IsNullOrEmpty(label)
                ? ""
                : Loc.Instance.Format("Status_LayerSourceHintFormat", label);
            // Mouse-layer engine only fires when HID is connected; toggling
            // it stops the OS-level mouse tap while disconnected.
            Devices.Push.OnHidConnectionChanged();
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
