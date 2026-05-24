using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Services;
using LViz.Core.Keymap;
using LViz.Core.Layout;
using LViz.Core.Models;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Per-key bindable: position (from the profile) + display (label + highlight
/// fill derived from the active layer's binding). Pure string formatting lives
/// in <see cref="KeyLabelFormatter"/>; this class owns the observable state
/// and orchestrates a label rebuild on <see cref="ApplyBinding"/>.
/// </summary>
public partial class KeyViewModel : ObservableObject
{
    public KeyPosition Position { get; }

    /// <summary>
    /// Rotation pivot in the form Avalonia's <c>RenderTransformOrigin</c> wants.
    /// Defaults to the key centre (<c>0.5, 0.5</c> relative); when the profile
    /// supplies an absolute canvas pivot (Glove80 thumbs share one well outside
    /// any individual key), it's translated to the key-local absolute frame
    /// — RenderTransformOrigin's absolute mode is measured from the key's own
    /// top-left, not the parent canvas.
    /// </summary>
    public RelativePoint RotationOrigin =>
        Position.RotationOriginX is double rx && Position.RotationOriginY is double ry
            ? new RelativePoint(rx - Position.X, ry - Position.Y, RelativeUnit.Absolute)
            : RelativePoint.Center;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelFontSize))]
    [NotifyPropertyChangedFor(nameof(IconFontSize))]
    private string _label = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubscriptFontSize))]
    private string _subscript = "";
    [ObservableProperty] private string _topLeftLabel = "";
    [ObservableProperty] private string _iconName = "";

    // Icon size grows when there's no main label competing for centre space.
    // TopLeftLabel is corner-positioned and Subscript is small — neither
    // collides with a centred icon, so only Label gates the size.
    public double IconFontSize => string.IsNullOrEmpty(Label) ? 32 : 14;
    [ObservableProperty] private string _behavior = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyForegroundColor))]
    private string _keyFillColor = DefaultKeyFill;

    internal const string DefaultKeyFill = AppTheme.KeyDefaultFillHex;
    private const string DarkForeground = AppTheme.BgBaseHex;
    private const string LightForeground = AppTheme.KeyDefaultFillHex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyHighlight))]
    private bool _isPressed;
    [ObservableProperty] private bool _isInCombo;
    [ObservableProperty] private string _tooltip = "";

    /// <summary>
    /// True when a combo this key participates in is currently being hovered
    /// (from either the on-key pill or the legend tile). Re-uses the
    /// press-dot visual via <see cref="IsAnyHighlight"/> — no separate
    /// indicator, so the hover preview reads identically to a live keypress.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyHighlight))]
    private bool _isComboKeyHighlighted;

    /// <summary>
    /// OR of <see cref="IsPressed"/> and <see cref="IsComboKeyHighlighted"/>;
    /// bound by the press-dot Ellipse in BoardView so live HID presses and
    /// combo hovers share the exact same on-key highlight.
    /// </summary>
    public bool IsAnyHighlight => IsPressed || IsComboKeyHighlighted;

    /// <summary>
    /// One numbered pill per combo this key participates in, capped at
    /// <see cref="KeyComboBadgeViewModel.MaxBadgesPerKey"/> — higher
    /// numbers don't fit the bottom-left strip. Each badge tracks its
    /// own <see cref="KeyComboBadgeViewModel.IsHighlighted"/>, so the
    /// pill and the legend tile light up 1:1.
    /// </summary>
    public ObservableCollection<KeyComboBadgeViewModel> ComboBadges { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelFontSize))]
    private double? _labelFontSizeOverride;

    [ObservableProperty] private bool _isLabelBold;

    /// <summary>
    /// Auto-contrasting label/icon color for the current <see cref="KeyFillColor"/>.
    /// Uses Rec. 709 luminance (0.2126R + 0.7152G + 0.0722B) with a threshold
    /// around mid-grey so pastel fills still read dark while navy / black
    /// decoration backgrounds get a light foreground.
    /// </summary>
    public string KeyForegroundColor => IsLightBackground(KeyFillColor) ? DarkForeground : LightForeground;

    private static bool IsLightBackground(string hex)
    {
        if (!TryParseHex(hex, out var r, out var g, out var b)) return true;
        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return luminance > 140; // ~55% of max — tuned so pastel layer palette stays "light".
    }

    /// <summary>
    /// Parses an <c>#RGB</c>, <c>#RRGGBB</c>, or <c>#RRGGBBAA</c> hex string
    /// into 0–255 RGB components. Returns false (with components zeroed)
    /// when the input doesn't match any of those shapes — callers treat
    /// that as "no usable color" rather than an error.
    /// </summary>
    private static bool TryParseHex(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        if (hex.StartsWith('#')) hex = hex.Substring(1);
        if (hex.Length == 8) hex = hex.Substring(0, 6);
        if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length != 6) return false;
        var style = System.Globalization.NumberStyles.HexNumber;
        return int.TryParse(hex.AsSpan(0, 2), style, null, out r)
            && int.TryParse(hex.AsSpan(2, 2), style, null, out g)
            && int.TryParse(hex.AsSpan(4, 2), style, null, out b);
    }

    /// <summary>
    /// Auto-scales the label font to the label length so single glyphs read
    /// big while multi-word labels still fit a 60px cap. Slim arrow glyphs
    /// get an explicit bump so they're not lost at the default body weight.
    /// </summary>
    public double LabelFontSize =>
        LabelFontSizeOverride
        ?? (IsArrowGlyph(Label) ? 28 : LabelFontSizeForLength(LongestWordLength(Label)));

    /// <summary>
    /// Same length-based scaling for the subscript — single-char glyphs (the
    /// modifier icons ⇧ ⌃ ⌥ ⌘) get a bump so they read as icons rather than
    /// vestigial tags. Long layer-name subscripts (now possibly multi-word
    /// after <see cref="KeyLabelFormatter.FormatLayerName"/>) shrink further so they fit when wrapped.
    /// </summary>
    public double SubscriptFontSize => SubscriptFontSizeForLength(LongestWordLength(Subscript));

    private static bool IsArrowGlyph(string s) => s is "↑" or "↓" or "←" or "→";

    private static double LabelFontSizeForLength(int length) => length switch
    {
        0 => 14,
        1 => 20,
        2 => 18,
        3 => 15,
        <= 5 => 13,
        <= 8 => 11,
        _ => 10,
    };

    private static double SubscriptFontSizeForLength(int length) => length switch
    {
        0 => 11,
        1 => 15,
        2 => 12,
        <= 4 => 11,
        <= 7 => 10,
        _ => 9,
    };

    private static int LongestWordLength(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int best = 0, run = 0;
        foreach (var c in s)
        {
            if (c == ' ' || c == '\n') { if (run > best) best = run; run = 0; }
            else run++;
        }
        return run > best ? run : best;
    }

    public KeyViewModel(KeyPosition position)
    {
        Position = position;
    }

    // Per-binding tooltip (rebuilt per layer switch) and per-combo
    // tooltip lines (rebuilt once at keymap load via SetCombos). Kept
    // separate so ApplyBinding doesn't have to know about the combo set.
    private string _baseTooltip = "";
    private string _comboTooltipSections = "";

    public void ApplyBinding(KeyBinding binding, int activeLayerIndex, int? targetLayer, string? targetLayerName, string profileId, HoldTap? holdTap = null)
    {
        Behavior = binding.Behavior;
        KeyFillColor = ResolveFillColor(binding, targetLayer, profileId);
        IconName = KeyLabelFormatter.NormalizeIconName(binding.DecorationIcon);
        _baseTooltip = BuildTooltip(binding, targetLayerName, holdTap);
        Tooltip = AppendEditHint(ComposeTooltip());

        var (label, sub, topLeft) = ComputeLabels(binding, targetLayerName, holdTap);
        Label = label;
        Subscript = sub;
        TopLeftLabel = topLeft;

        // User-authored override wins over the formatter — same precedence as
        // decoration.label in KeyLabelFormatter.FormatBinding.
        var ov = KeyLabelOverrides.Get(profileId, activeLayerIndex, Position.Index);
        if (ov is not null)
        {
            Label = ov.MainLabel;
            Subscript = ov.Subscript;
            TopLeftLabel = ov.TopLeftBadge;
            if (!string.IsNullOrEmpty(ov.Icon))
                IconName = KeyLabelFormatter.NormalizeIconName(ov.Icon);
            LabelFontSizeOverride = ov.FontSize;
            IsLabelBold = ov.Bold;
        }
        else
        {
            LabelFontSizeOverride = null;
            IsLabelBold = false;
        }
    }

    // decoration.icon acts as flair above the label — if a decoration.label
    // is also set, it still drives the main label, but the derived
    // label/subscript/badge logic is skipped so the user-authored pairing
    // wins cleanly.
    private (string Label, string Subscript, string TopLeft) ComputeLabels(
        KeyBinding binding, string? targetLayerName, HoldTap? holdTap)
    {
        if (!string.IsNullOrEmpty(IconName))
            return (binding.DecorationLabel ?? "", "", "");
        return KeyLabelFormatter.FormatBinding(binding, targetLayerName, holdTap);
    }

    /// <summary>
    /// Multi-line tooltip dump of every piece of data we have for this key,
    /// minus the decoration icon (already rendered visually). Sections are
    /// separated by blank lines:
    /// <list type="bullet">
    /// <item>Header: position + raw binding (<c>&amp;kp LS(LBKT)</c>, …)</item>
    /// <item>Category: hold-tap / standard layer-switch detail</item>
    /// <item>Decoration: user-authored label and background hex if present</item>
    /// </list>
    /// </summary>
    private string BuildTooltip(KeyBinding b, string? targetLayerName, HoldTap? holdTap)
    {
        var sections = new List<string> { BuildHeaderSection(b) };

        var category = KeyLabelFormatter.BuildCategorySection(b, targetLayerName, holdTap);
        if (category is not null) sections.Add(category);

        var decoration = KeyLabelFormatter.BuildDecorationSection(b);
        if (decoration is not null) sections.Add(decoration);

        return string.Join("\n\n", sections);
    }

    private const string EditHintSection = "Right-click to edit label";

    private static string AppendEditHint(string body) =>
        string.IsNullOrEmpty(body) ? EditHintSection : body + "\n\n" + EditHintSection;

    private string BuildHeaderSection(KeyBinding b)
    {
        var posLine = Position.Description is { } desc
            ? $"{desc}  (idx {Position.Index})"
            : $"idx {Position.Index}";
        return $"{posLine}\n\n{b.Display}";
    }

    /// <summary>
    /// Sets the combo set this key participates in. Called once per keymap
    /// load (not per layer switch) since combos aren't layer-scoped today.
    /// Owns three pieces of state: <see cref="IsInCombo"/>, the
    /// <see cref="ComboBadges"/> collection (capped at
    /// <see cref="KeyComboBadgeViewModel.MaxBadgesPerKey"/>), and the
    /// cached combo-tooltip lines that <see cref="ApplyBinding"/> later
    /// appends to the rebuilt per-binding tooltip.
    /// </summary>
    public void SetCombos(
        IReadOnlyList<ZmkCombo> combos,
        IReadOnlyList<int> comboNumbers,
        Func<int, string> labelLookup)
    {
        if (combos is null || combos.Count == 0)
        {
            IsInCombo = false;
            IsComboKeyHighlighted = false;
            ComboBadges.Clear();
            _comboTooltipSections = "";
            Tooltip = AppendEditHint(_baseTooltip);
            return;
        }

        IsInCombo = true;

        var take = Math.Min(comboNumbers.Count, KeyComboBadgeViewModel.MaxBadgesPerKey);
        ComboBadges.Clear();
        for (var i = 0; i < take; i++)
            ComboBadges.Add(new KeyComboBadgeViewModel(comboNumbers[i]));

        var sections = new string[combos.Count];
        for (var i = 0; i < combos.Count; i++)
            sections[i] = KeyLabelFormatter.BuildComboSection(combos[i], labelLookup);
        _comboTooltipSections = string.Join("\n\n", sections);

        Tooltip = AppendEditHint(ComposeTooltip());
    }

    /// <summary>
    /// Merges the per-binding tooltip (rebuilt every layer switch by
    /// <see cref="ApplyBinding"/>) with the cached combo-tooltip lines
    /// (set once at keymap load by <see cref="SetCombos"/>). Either side
    /// can be empty.
    /// </summary>
    private string ComposeTooltip()
    {
        if (string.IsNullOrEmpty(_comboTooltipSections)) return _baseTooltip;
        if (string.IsNullOrEmpty(_baseTooltip)) return _comboTooltipSections;
        return _baseTooltip + "\n\n" + _comboTooltipSections;
    }

    /// <summary>
    /// Fill-color precedence:
    /// <list type="number">
    /// <item><c>decoration.background</c> from the Moergo editor (user-authored).</item>
    /// <item>Target layer's palette color (for <c>&amp;lt</c> keys).</item>
    /// <item>Default key fill.</item>
    /// </list>
    /// </summary>
    private static string ResolveFillColor(KeyBinding b, int? targetLayer, string profileId)
    {
        if (!string.IsNullOrWhiteSpace(b.DecorationBackground))
            return b.DecorationBackground!;
        if (targetLayer is int layer)
            return LayerColorPalette.GetColor(profileId, layer);
        return DefaultKeyFill;
    }
}
