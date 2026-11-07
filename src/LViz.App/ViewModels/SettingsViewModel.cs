using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// View model for the Settings window: the General and Layers tabs, the hidden
/// Testing tab, and the composition point for the per-tab view-models
/// (<see cref="AutoSwitch"/>, <see cref="MouseLayer"/>, <see cref="About"/>,
/// <see cref="DeviceRouting"/>, <see cref="ActionPipelines"/>), which the tab
/// views bind to directly. The main VM owns the state and persistence; bindings
/// here are TwoWay so live tweaks update the board immediately.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _mainViewModel;
    private bool _disposed;

    /// <summary>
    /// The owning main view model. Exposed publicly so XAML bindings for
    /// pure pass-through properties (BackgroundOpacity, HotkeyKey, etc.)
    /// route directly to it instead of through facade properties on this
    /// VM. Only transforming/derived properties (radio bool ↔ enum,
    /// localized labels, layer entries) still live here.
    /// </summary>
    public MainWindowViewModel MainViewModel => _mainViewModel;

    /// <summary>Auto-switch tab VM: engine options, rules edit buffer, process picker. Disposed with this VM.</summary>
    public AutoSwitchSettingsViewModel AutoSwitch { get; }

    /// <summary>Mouse Layer tab VM: per-keyboard mouse-layer settings.</summary>
    public MouseLayerSettingsViewModel MouseLayer { get; }

    /// <summary>Version, updates and command-line tool sections. Disposed with this VM.</summary>
    public AboutSettingsViewModel About { get; }

    /// <summary>
    /// Device Routing tab VM: observes the long-lived
    /// <see cref="MainWindowViewModel.CapabilityRouter"/> while Settings is open
    /// (inventory + routing table) and drives app-originated control. Disposed
    /// with this VM so the router doesn't retain a closed window's observer.
    /// </summary>
    public DeviceRoutingViewModel DeviceRouting { get; }

    /// <summary>
    /// Action Pipelines tab VM: edits the named pipeline library + signal
    /// bindings, observing the same <see cref="MainWindowViewModel.CapabilityRouter"/>
    /// for device-step targets. Committed by <see cref="Views.SettingsWindow"/> on close;
    /// disposed with this VM.
    /// </summary>
    public ActionPipelinesViewModel ActionPipelines { get; }

    public SettingsViewModel(
        ISettingsService settingsService,
        MainWindowViewModel mainViewModel,
        UpdateService updateService,
        ICapabilityControl? capabilityControl = null,
        ICliToolInstaller? cliToolInstaller = null)
    {
        _mainViewModel = mainViewModel;
        DeviceRouting = new DeviceRoutingViewModel(
            mainViewModel.CapabilityRouter,
            capabilityControl ?? new CapabilityControl(),
            settingsService);
        ActionPipelines = new ActionPipelinesViewModel(
            mainViewModel.CapabilityRouter,
            settingsService,
            mainViewModel);
        AutoSwitch = new AutoSwitchSettingsViewModel(mainViewModel);
        MouseLayer = new MouseLayerSettingsViewModel(settingsService, mainViewModel);
        About = new AboutSettingsViewModel(
            settingsService, updateService, cliToolInstaller ?? new CliToolInstaller());
        HotkeyKeyChoices = PlatformCapabilities.GetAvailableFKeys(mainViewModel.HotkeyKey);
        _mainViewModel.PropertyChanged += OnMainPropertyChanged;
        _mainViewModel.BoardStyle.PropertyChanged += OnBoardStylePropertyChanged;
        _mainViewModel.LayerTabs.Layers.CollectionChanged += OnLayersCollectionChanged;

        RebuildLayerEntries();
    }

    /// <summary>HID source label ("Raw HID (Go60 Left)" or "Raw HID (Go60 Left) (searching)"). Updated live as the coordinator's connection changes.</summary>
    public string LayerSourceStatus => _mainViewModel.LayerSourceHint;

    private LayerViewModel? _selectedTestLayer;
    public LayerViewModel? SelectedTestLayer
    {
        get => _selectedTestLayer;
        set
        {
            if (SetProperty(ref _selectedTestLayer, value) && value is not null)
                _mainViewModel.PushLayerToKeyboard(value.Index);
        }
    }

    // ── HID query test buttons (Testing tab) ──────────────────────────────
    // Result strings show "(not run)" until the user clicks the button,
    // then either the parsed value or a "(no reply)" / "(no device)" hint.
    // 1s timeout matches the firmware's typical reply latency; failure is
    // logged through DiagnosticLog inside MainWindowViewModel.

    [ObservableProperty] private string _deviceInfoResult = "(not run)";
    [ObservableProperty] private string _configIdResult = "(not run)";

    [RelayCommand]
    private async Task QueryDeviceInfoAsync()
    {
        DeviceInfoResult = "(querying…)";
        var info = await _mainViewModel.QueryDeviceInfoAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        DeviceInfoResult = info is null
            ? "(no reply)"
            : $"protocol v{info.ProtocolVersion}, name=\"{info.Name}\"";
    }

    [RelayCommand]
    private async Task QueryConfigIdAsync()
    {
        ConfigIdResult = "(querying…)";
        var id = await _mainViewModel.QueryConfigIdAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        ConfigIdResult = id is null
            ? "(no reply)"
            : id.Length == 0 ? "(empty)" : id;
    }
    /// <summary>All registered keyboard profiles, surfaced for the Settings
    /// dropdown. Order matches <see cref="Core.Layout.KeyboardProfileRegistry.All"/>.</summary>
    public IReadOnlyList<Core.Layout.IKeyboardProfile> KeyboardChoices =>
        Core.Layout.KeyboardProfileRegistry.All;

    /// <summary>False while the main VM is editing a keymap — the keyboard
    /// picker is locked, because a board switch underneath the editor would
    /// repaint the keys the edit is showing.</summary>
    public bool CanPickKeyboard => !_mainViewModel.IsEditing;

    /// <summary>TwoWay-bound profile selector. Routes user picks through
    /// <see cref="MainWindowViewModel.SelectKeyboardCommand"/> so the full
    /// switch path (rebuild keys, re-scope HID, drop incompatible layout,
    /// persist) runs — direct property assignment would skip all of that.
    /// The setter ignores no-op selections and null (combo-box clear). While
    /// editing it refuses every change and re-raises so the combo snaps back
    /// to the still-active board.</summary>
    public Core.Layout.IKeyboardProfile SelectedKeyboardProfile
    {
        get => _mainViewModel.SelectedKeyboard;
        set
        {
            if (!CanPickKeyboard)
            {
                OnPropertyChanged(nameof(SelectedKeyboardProfile));
                return;
            }
            if (value is null) return;
            if (string.Equals(value.Id, _mainViewModel.SelectedKeyboard?.Id, StringComparison.OrdinalIgnoreCase))
                return;
            _mainViewModel.SelectKeyboardCommand.Execute(value);
        }
    }

    /// <summary>
    /// Detaches the long-lived <see cref="MainWindowViewModel"/> event hooks
    /// so this VM (and its window's whole DataContext graph) becomes
    /// collectible. Settings reopens build a fresh instance every time, so
    /// without this every closed-then-reopened window leaked a zombie VM
    /// still firing on every property change of the main VM.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mainViewModel.PropertyChanged -= OnMainPropertyChanged;
        _mainViewModel.BoardStyle.PropertyChanged -= OnBoardStylePropertyChanged;
        _mainViewModel.LayerTabs.Layers.CollectionChanged -= OnLayersCollectionChanged;
        AutoSwitch.Dispose();
        About.Dispose();
        DeviceRouting.Dispose();
        ActionPipelines.Dispose();
    }

    /// <summary>Formatted percentage label next to the opacity slider. Reads
    /// straight off the main VM — re-raised by <see cref="OnMainPropertyChanged"/>
    /// when BackgroundOpacity changes.</summary>
    public string BackgroundOpacityPercent => $"{(int)(_mainViewModel.BackgroundOpacity * 100)}%";

    /// <summary>
    /// Press-highlight color exposed as Avalonia <see cref="Color"/> so the
    /// <c>ColorView</c> picker can bind directly. Round-trips through the
    /// main VM's hex string (single source of truth + persistence).
    /// </summary>
    public Color PressHighlightColor
    {
        get => Color.TryParse(_mainViewModel.BoardStyle.PressHighlightColor, out var c) ? c : Colors.Yellow;
        set => _mainViewModel.BoardStyle.PressHighlightColor = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
    }

    /// <summary>Hex string for the swatch preview Border in the picker button.</summary>
    public string PressHighlightColorHex => _mainViewModel.BoardStyle.PressHighlightColor;

    /// <summary>F-key choices offered for the global hotkey. F13–F24 first
    /// (effectively always free), then F1–F12. Filtered per OS — macOS
    /// drops keys claimed by symbolic hotkeys, Windows probes
    /// <c>RegisterHotKey</c> and drops anything another process already owns.
    /// Computed once when this VM is constructed (i.e. on each Settings
    /// window open) so the data is fresh every time, and the user's own
    /// current pick is excluded from the claim set so it stays selectable.
    /// See <see cref="PlatformCapabilities.GetAvailableFKeys"/>.</summary>
    public IReadOnlyList<string> HotkeyKeyChoices { get; }

    /// <summary>Shortcut to <see cref="PlatformCapabilities.IsGlobalHotkeySupported"/>
    /// for XAML bindings on the General tab.</summary>
    public static bool IsGlobalHotkeySupported => PlatformCapabilities.IsGlobalHotkeySupported;

    /// <summary>Top-hand picker choices for stacked layout. Mirrors svalboard's UX.</summary>
    public IReadOnlyList<string> AvailableTopHands { get; } = new[] { "Left", "Right" };

    /// <summary>Per-layer rows for the Layers tab: each row has a color picker.</summary>
    public ObservableCollection<LayerSettingsEntry> LayerEntries { get; } = new();

    /// <summary>"Layers — {profile name}" — section heading scoped to active keyboard.</summary>
    [ObservableProperty]
    private string _layersHeader = "";

    /// <summary>True iff a layout is loaded — toggles between the entries list and the placeholder hint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLayersHint))]
    private bool _hasLayoutForLayers;

    /// <summary>Inverse of <see cref="HasLayoutForLayers"/> for XAML visibility (Avalonia's compiled-binding `!` is finicky in non-trivial templates).</summary>
    public bool ShowsLayersHint => !HasLayoutForLayers;


    // Dispatch table for re-raising derived/transforming SettingsVM
    // properties when their underlying MainWindowViewModel properties
    // change. Pass-through properties (BackgroundOpacity, HotkeyKey,
    // IsStackedLayout, StackedTopHand, Layers) are bound straight to
    // MainViewModel in XAML and need no entry here; the tab VMs observe the
    // engines they serve themselves. Only properties this VM still owns —
    // formatted labels, radio booleans over an enum, per-profile rebuilds —
    // appear here, plus the keyboard-switch fan-out to the tab VMs.
    private static readonly Dictionary<string, Action<SettingsViewModel>> _mainPropagators = new()
    {
        [nameof(MainWindowViewModel.BackgroundOpacity)] = s => s.OnPropertyChanged(nameof(BackgroundOpacityPercent)),
        [nameof(MainWindowViewModel.SelectedKeyboard)] = s =>
        {
            s.RebuildLayerEntries();
            s.OnPropertyChanged(nameof(SelectedKeyboardProfile));
            s.AutoSwitch.OnActiveKeyboardChanged();
            s.MouseLayer.Reload();
        },
        [nameof(MainWindowViewModel.LayerSourceHint)] = s => s.OnPropertyChanged(nameof(LayerSourceStatus)),
        // The keyboard picker locks while an edit is open (CanPickKeyboard).
        [nameof(MainWindowViewModel.IsEditing)] = s => s.OnPropertyChanged(nameof(CanPickKeyboard)),
    };

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && _mainPropagators.TryGetValue(name, out var propagate))
            propagate(this);
    }

    // Re-raises the SettingsVM press-color properties off the board-style VM's
    // own PropertyChanged. SettingsVM binds BoardStyle directly (no MainVM
    // pass-through), so it subscribes here exactly as it does for AutoSwitch.
    private static readonly Dictionary<string, Action<SettingsViewModel>> _boardStylePropagators = new()
    {
        [nameof(BoardStyleViewModel.PressHighlightColor)] = s =>
        {
            s.OnPropertyChanged(nameof(PressHighlightColor));
            s.OnPropertyChanged(nameof(PressHighlightColorHex));
        },
    };

    private void OnBoardStylePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && _boardStylePropagators.TryGetValue(name, out var propagate))
            propagate(this);
    }

    /// <summary>Test-only handle on the propagator dispatch table. The
    /// SettingsViewModelMainPropertyCoverageTests guard against silent
    /// drift when new MainViewModel properties are added.</summary>
    internal static IReadOnlyCollection<string> PropagatedMainPropertyNames => _mainPropagators.Keys;

    private void OnLayersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A keymap reload renames or renumbers layers; rule-row layer
        // labels re-evaluate automatically via the MultiBinding's Count
        // trigger in XAML, so only the per-layer settings rows need to
        // rebuild here.
        RebuildLayerEntries();
    }

    /// <summary>
    /// Rebuilds the per-layer rows for the Layers tab. Each row wraps a
    /// <see cref="LayerColorEntry"/> (color picker + reset). Cheap to rebuild
    /// — layer counts are small, and entries are recreated rather than reused
    /// so the swatch colors stay in sync after Reset.
    /// </summary>
    private void RebuildLayerEntries()
    {
        LayerEntries.Clear();
        var profile = _mainViewModel.SelectedKeyboard;
        var profileName = profile?.DisplayName ?? "";
        LayersHeader = string.Format(Loc.Instance["Settings_LayersFormat"], profileName);
        HasLayoutForLayers = _mainViewModel.LayerTabs.Layers.Count > 0;
        if (!HasLayoutForLayers || profile is null) return;

        foreach (var layer in _mainViewModel.LayerTabs.Layers)
        {
            var hex = LayerColorPalette.GetColor(profile.Id, layer.Index);
            var color = Color.TryParse(hex, out var c) ? c : Colors.Gray;
            var colorEntry = new LayerColorEntry(
                layer.Index,
                layer.DisplayName,
                color,
                _mainViewModel.LayerTabs.SetColorOverride,
                LayerColorPalette.GetDefaultColor);

            LayerEntries.Add(new LayerSettingsEntry(colorEntry, layer.IsShown, _mainViewModel.LayerTabs.SetShown));
        }
    }
}
