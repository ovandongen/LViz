using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Services;
using LViz.Core.Keymap;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// The persisted window preferences of the main window: always-on-top, the
/// background opacity (and the two backgrounds derived from it), the global
/// hotkey, the tray-icon colouring, the key-editor choice and the modifier
/// glyph style. Each property writes itself to settings when it changes;
/// <see cref="MainWindowViewModel"/> exposes this as <c>Preferences</c> and
/// the views bind to it directly.
/// </summary>
public partial class WindowPreferencesViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    public WindowPreferencesViewModel(ISettingsService settings, UserSettings seed)
    {
        _settings = settings;
        _isAlwaysOnTop = seed.AlwaysOnTop;
        _colorTrayIconByActiveLayer = seed.ColorTrayIconByActiveLayer;
        _useRingKeyEditor = seed.RingKeyEditor;
        _backgroundOpacity = Math.Clamp(seed.BackgroundOpacity, 0.0, 1.0);
        if (!string.IsNullOrWhiteSpace(seed.HotkeyKey))
            _hotkeyKey = seed.HotkeyKey;
        _isWindowsModifierStyle = string.Equals(seed.ModifierStyle, "Windows", StringComparison.OrdinalIgnoreCase);
        // Seed the static *before* the first key render so the initial paint
        // already uses the persisted glyph set (the partial change handler is
        // not invoked when the backing field is assigned directly).
        ZmkKeycodeLabel.CurrentModifierStyle = _isWindowsModifierStyle ? ModifierStyle.Windows : ModifierStyle.Mac;
    }

    [ObservableProperty] private bool _isAlwaysOnTop;
    [ObservableProperty] private bool _colorTrayIconByActiveLayer;
    [ObservableProperty] private bool _useRingKeyEditor;

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

    /// <summary>
    /// Global show/hide hotkey key name (e.g. "F12"). Modifier handling
    /// lives in <see cref="UserSettings.HotkeyModifiers"/> and isn't
    /// user-editable today. Changing this raises <see cref="HotkeyKeyChanged"/>
    /// so the live <see cref="Services.IGlobalHotkeyService"/> rewires
    /// without restart.
    /// </summary>
    [ObservableProperty]
    private string _hotkeyKey = "F12";

    public event Action<string>? HotkeyKeyChanged;

    /// <summary>Read-through to the persisted modifier name. Not user-editable today; keeps the hotkey-rewire call site in App self-contained.</summary>
    public string HotkeyModifiers => _settings.Load().HotkeyModifiers;

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
    /// Raised after the glyph style changed (and the static was updated), so
    /// the owner can relabel the keys it shows.
    /// </summary>
    public event Action? ModifierStyleChanged;

    /// <summary>
    /// SVG path data for the toolbar button's icon: Apple silhouette when the
    /// Mac style is active, four-square Windows logo when the Windows style is
    /// active. Both paths are designed against a 24×24 viewBox so PathIcon
    /// scales them like the rest of the toolbar icons.
    /// </summary>
    public string ModifierStyleIconData => IsWindowsModifierStyle
        ? "M3 5.479L10.768 4.5v8.385H3V5.479zM11.232 4.5L21 3v9.885h-9.768V4.5zM3 13.115h7.768V21.5L3 20.521V13.115zM11.232 13.115H21V22.5l-9.768-1.385V13.115z"
        : "M17.05 20.28c-.98.95-2.05.8-3.08.35-1.09-.46-2.09-.48-3.24 0-1.44.62-2.2.44-3.06-.35C2.79 15.25 3.51 7.59 9.05 7.31c1.35.07 2.29.74 3.08.8 1.18-.24 2.31-.93 3.57-.84 1.51.12 2.65.72 3.4 1.8-3.12 1.87-2.38 5.98.48 7.13-.57 1.5-1.31 2.99-2.54 4.09zM12.03 7.25c-.15-2.23 1.66-4.07 3.74-4.25.29 2.58-2.34 4.5-3.74 4.25z";

    partial void OnIsAlwaysOnTopChanged(bool value) =>
        Persist(s => s with { AlwaysOnTop = value });

    partial void OnBackgroundOpacityChanged(double value) =>
        Persist(s => s with { BackgroundOpacity = value });

    partial void OnColorTrayIconByActiveLayerChanged(bool value) =>
        Persist(s => s with { ColorTrayIconByActiveLayer = value });

    partial void OnUseRingKeyEditorChanged(bool value) =>
        Persist(s => s with { RingKeyEditor = value });

    partial void OnHotkeyKeyChanged(string value)
    {
        Persist(s => s with { HotkeyKey = value });
        HotkeyKeyChanged?.Invoke(value);
    }

    partial void OnIsWindowsModifierStyleChanged(bool value)
    {
        ZmkKeycodeLabel.CurrentModifierStyle = value ? ModifierStyle.Windows : ModifierStyle.Mac;
        Persist(s => s with { ModifierStyle = value ? "Windows" : "Mac" });
        ModifierStyleChanged?.Invoke();
    }

    [RelayCommand]
    private void TogglePin() => IsAlwaysOnTop = !IsAlwaysOnTop;

    [RelayCommand]
    private void ToggleWindowsModifierStyle() => IsWindowsModifierStyle = !IsWindowsModifierStyle;

    private void Persist(Func<UserSettings, UserSettings> update) =>
        _settings.Update(update, "MainVM");
}
