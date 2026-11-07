using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.GitHub;
using LViz.App.Services.MouseIdle;
using LViz.Core.Diagnostics;
using LViz.Core.Keymap;
using LViz.Core.Layout;
using LViz.Core.Models;
using LViz.Core.Persistence;
using LViz.Core.Settings;
using ZmkHidProtocol.ActiveWindow;
using ZmkHidProtocol.Protocol;

namespace LViz.App.ViewModels;

/// <summary>
/// Top-level view model for <c>MainWindow</c>. Owns the loaded keymap, the
/// active layer, the live-key tracker, and persists the user's choice of
/// keyboard + last-loaded JSON path.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// Points the board's key callbacks at what the current mode allows. Outside
    /// keymap edit mode a tap is inert (null, so the press falls through to the
    /// window drag) and a right-click opens the per-key label-override editor;
    /// while editing a tap selects the key, a double-click opens the key editor
    /// on it, and the label dialog doesn't apply.
    /// </summary>
    private void SyncKeyCallbacks()
    {
        Board.OnKeyTapped = IsEditing ? OnEditKeyTapped : null;
        Board.OnKeyDoubleTapped = IsEditing ? OnEditKeyDoubleTapped : null;
        Board.OnKeyRightTapped = IsEditing ? null : OnKeyRightTappedInternal;
    }

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
    // Built lazily once the board's Keys collection is populated.
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


    /// <summary>The persisted window preferences (always-on-top, opacity, hotkey, tray colouring, key editor, modifier style); bound directly.</summary>
    public WindowPreferencesViewModel Preferences { get; }

    /// <summary>The board on screen: keys, canvas geometry (owns the persisted
    /// stacked-layout toggles), press style (owns the persisted highlight
    /// colour), combo legend and key callbacks. <c>BoardView</c>'s DataContext.</summary>
    public BoardSurfaceViewModel Board { get; }

    /// <summary>The layer tab strip: tabs, colours, hidden layers, edit-mode tab actions.</summary>
    public LayerTabsViewModel LayerTabs { get; }

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
        if (keyIndex < 0 || keyIndex >= Board.Keys.Count) return null;

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
        IKeymapStateService keymapState,
        KeymapBackupStore keymapBackups,
        IKeymapEditDialogs editDialogs,
        IMainWindowShell shell,
        IHostActionExecutor hostActionExecutor,
        ICapabilityControl capabilityControl,
        IGitHubKeymapService? gitHub,
        IActiveWindowMonitor? activeWindowMonitor,
        IMouseIdleMonitor? mouseIdleMonitor)
    {
        _settingsService = settingsService;
        Library = new KeymapLibrary(settingsService, gitHub);
        _editDialogs = editDialogs;
        _shell = shell;
        _keymap = keymapState;
        _gitHub = gitHub;
        _keymapBackups = keymapBackups;
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
        _isLiveHighlightingEnabled = s.LiveKeyHighlighting;
        _isAutoLayerSwitchEnabled = s.AutoLayerSwitch;
        Preferences = new WindowPreferencesViewModel(settingsService, s);
        Preferences.ModifierStyleChanged += () =>
        {
            // Rebuilds every key's labels against the freshly-set static.
            if (_keymap.HasLayout) ApplyActiveLayer(ActiveLayerIndex);
        };

        // Board geometry + press-style live in their own VMs now; seed them from
        // the same settings load. Each owns its own persistence (StackedLayout /
        // StackedTopHand / PressHighlightColor) via the injected settings service.
        // Board geometry + press-style own their persistence (StackedLayout /
        // StackedTopHand / PressHighlightColor) via the injected settings service.
        var boardLayout = new BoardLayoutViewModel(
            _profile, settingsService, s.StackedLayout,
            string.IsNullOrWhiteSpace(s.StackedTopHand) ? "Left" : s.StackedTopHand);
        var boardStyle = new BoardStyleViewModel(
            string.IsNullOrWhiteSpace(s.PressHighlightColor)
                ? AppTheme.PressHighlightDefaultHex
                : s.PressHighlightColor,
            settingsService);
        Board = new BoardSurfaceViewModel(
            _profile, boardLayout, boardStyle,
            keys => new ComboLegendViewModel(
                keys, boardStyle, s.ComboOverlayVisible, () => _profile.Id, settingsService, _shell));
        SyncKeyCallbacks();
        LayerTabs = new LayerTabsViewModel(() => _profile.Id, settingsService);
        LayerTabs.LayerPicked += OnLayerPicked;
        LayerTabs.ColorsChanged += OnLayerColorsChanged;
        // Seed the static palette with persisted per-keyboard, per-layer overrides
        // so the very first paint already reflects the user's customization.
        LayerColorPalette.SetOverrides(s.LayerColors);
        KeyLabelOverrides.SetOverrides(s.KeyLabelOverrides);
        ComboLabelOverrides.SetOverrides(s.ComboLabelOverrides);

        _hostActionExecutor = hostActionExecutor;
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

    /// <summary>Gracefully stops live tracking; idempotent. Called on window close / quit.</summary>
    public void Shutdown()
    {
        StopKeyEventTracking(isAppExit: true);
        Devices.Dispose();
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

        for (int i = 0; i < Board.Keys.Count; i++)
        {
            if (edit is null)
            {
                // Resolution (including &trans fall-through) lives on LoadedKeymap;
                // here we just paint the resolved binding onto the key VM.
                var r = km.Resolve(index, i);
                Board.Keys[i].ApplyBinding(
                    r.Binding,
                    activeLayerIndex: index,
                    targetLayer: r.TargetLayer,
                    targetLayerName: r.TargetLayerName,
                    profileId: _profile.Id,
                    holdTap: r.HoldTap,
                    macros: km.MacrosByName);
                Board.Keys[i].IsEditChanged = false;
                Board.Keys[i].IsEditSelected = false;
                continue;
            }

            // Edit mode shows the layer's OWN binding — &trans paints ▽ and
            // &none empty, no fall-through — and ignores per-key label
            // overrides, so the user sees exactly what a save will write.
            var bindings = km.Layers[index].Bindings;
            var literal = i < bindings.Count ? bindings[i] : KeyBinding.Transparent;
            if (literal.Behavior is "&trans" or "&none")
            {
                Board.Keys[i].ApplyBinding(
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
                Board.Keys[i].ApplyBinding(
                    r.Binding,
                    activeLayerIndex: index,
                    targetLayer: r.TargetLayer,
                    targetLayerName: r.TargetLayerName,
                    profileId: _profile.Id,
                    holdTap: r.HoldTap,
                    macros: km.MacrosByName,
                    ignoreLabelOverride: true);
            }
            Board.Keys[i].IsEditChanged = edit.Session.IsKeyChanged(index, i);
            Board.Keys[i].IsEditSelected = edit.SelectedKeyIndex == i;
        }

        LayerTabs.SetActive(index);
    }

    /// <summary>
    /// Lazily-instantiated press-highlight tracker. Constructed on first use
    /// so it captures the <see cref="BoardSurfaceViewModel.Keys"/> collection after
    /// <see cref="BoardSurfaceViewModel.SetProfile"/> has populated it.
    /// </summary>
    private IKeyHighlightTracker HighlightTracker =>
        _highlightTracker ??= new KeyHighlightTracker(Board.Keys);


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
    /// physical matrix position so we go straight to <see cref="BoardSurfaceViewModel.Keys"/>[position].
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
