using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;

namespace LViz.App.ViewModels;

/// <summary>
/// One entry of the keycode picker's category dropdown: a translated
/// <see cref="Name"/>, the <see cref="ZmkKeycodeCategory"/> it lists (null
/// for the pseudo-categories), and whether it is the usage-ranked
/// "Used in this keymap" list.
/// </summary>
public sealed record KeycodeCategoryChoice(string Name, ZmkKeycodeCategory? Category, bool IsUsed);

/// <summary>
/// One row of the keycode picker list: the catalog entry, the spelling the
/// dialog would write (<see cref="ZmkKeycodeUsage.NameFor"/>), the glyph it
/// renders as on the board, the glyph of the key this one produces with
/// Shift (null when there is none), the English catalog description and how
/// often the open keymap uses it.
/// </summary>
public sealed record KeycodeItem(
    ZmkKeycode Keycode,
    string Name,
    string Glyph,
    string? ShiftGlyph,
    string Description,
    int UseCount);

/// <summary>
/// The keycode list of the key dialog: category dropdown ("Used in this
/// keymap" first when the open keymap uses keys), search, the
/// Shift/Ctrl/Alt/GUI wrap toggles for both hands (the key dialog shows the
/// left-hand ones), key recording, and the flat params the
/// selection produces (<see cref="Value"/>, e.g. <c>["LS","A"]</c> for
/// <c>&amp;kp LS(A)</c>). One instance per keycode parameter of a binding
/// (<c>&amp;kp</c>'s key, <c>&amp;mt</c>'s hold and tap); the key dialog wraps it.
/// </summary>
public sealed partial class KeycodePickerViewModel : ObservableObject
{
    private const string KpPrefix = "&kp ";

    private readonly ZmkKeycodeUsage _usage;
    private readonly bool _modifiersFirst;

    /// <summary>Canonical name → the keycode it produces with Shift.</summary>
    private readonly Dictionary<string, ZmkKeycode> _shiftedByOf;

    /// <summary>Keycode and exact spelling <see cref="Seed"/> opened with;
    /// re-selecting that keycode keeps writing the old spelling.</summary>
    private ZmkKeycode? _seedKeycode;
    private string? _seedSpelling;

    /// <summary>Keeps compound operations (<see cref="Seed"/>,
    /// <see cref="ApplyRecorded"/>) to a single <see cref="Changed"/>.</summary>
    private bool _suppressChanged;

    public KeycodePickerViewModel(ZmkKeycodeUsage usage, bool modifiersFirst)
    {
        ArgumentNullException.ThrowIfNull(usage);

        _usage = usage;
        _modifiersFirst = modifiersFirst;
        _shiftedByOf = BuildShiftedByOf();
        Categories = BuildCategories(usage);
        SelectedCategory = DefaultCategory();
    }

    /// <summary>The category dropdown: Used (only when the keymap uses keys),
    /// All, then the categories in enum order; with <c>modifiersFirst</c> the
    /// Modifiers entry sits at the front instead.</summary>
    public IReadOnlyList<KeycodeCategoryChoice> Categories { get; }

    /// <summary>False for a <c>&amp;mt</c>-style picker whose parameter list
    /// shows plain keycodes without the wrap rows.</summary>
    public bool ShowWrapToggles => !_modifiersFirst;

    /// <summary>The currently listed keycodes; rebuilt when the category or
    /// the search text changes.</summary>
    public IReadOnlyList<KeycodeItem> Items { get; private set; } = [];

    /// <summary>Search result count as dialog text.</summary>
    public string MatchCountText =>
        Loc.Instance.Format("KeyDialog_MatchCountFormat", Items.Count);

    /// <summary>The picked keycode, or null while nothing is picked.</summary>
    public KeycodeItem? SelectedItem
    {
        get => Items.FirstOrDefault(item => item.Keycode == SelectedKeycode);
        set
        {
            // null is ignored: an Items refresh that no longer lists the key
            // must not clear the pick (the category change keeps it).
            if (value is not null) SelectedKeycode = value.Keycode;
        }
    }

    /// <summary>The flat params the selection produces — modifier functions in
    /// <see cref="ZmkModifierWrap.Wrap"/> order, then the keycode — or null
    /// while nothing is picked. The seeded keycode keeps its opened spelling;
    /// every other keycode writes <see cref="ZmkKeycodeUsage.NameFor"/>.</summary>
    public IReadOnlyList<string>? Value
    {
        get
        {
            if (SelectedKeycode is not { } keycode) return null;

            var name = _seedKeycode == keycode && _seedSpelling is { } spelling
                ? spelling
                : _usage.NameFor(keycode);
            return ZmkModifierWrap.Wrap(name, LeftToggleFlags | RightToggleFlags);
        }
    }

    public bool HasValue => SelectedKeycode is not null;

    /// <summary>The binding text without the behavior, e.g. <c>LS(A)</c>;
    /// empty while nothing is picked.</summary>
    public string ValueText
    {
        get
        {
            if (Value is not { } value) return "";
            var text = ZmkBindingFormatter.Format(new KeyBinding("&kp", value), []);
            return text.StartsWith(KpPrefix, StringComparison.Ordinal)
                ? text[KpPrefix.Length..]
                : text;
        }
    }

    /// <summary>The wrap flags the toggles add, both hands combined; the
    /// radial key editor's inner ring reads this.</summary>
    public ZmkModifiers WrapFlags => LeftToggleFlags | RightToggleFlags;

    /// <summary>Raised once per user action that may change
    /// <see cref="Value"/>: picking a keycode, flipping a toggle, seeding or
    /// recording a key.</summary>
    public event EventHandler? Changed;

    // Hand-written, not [ObservableProperty]: a list that loses its selection
    // writes null here, and the picker always lists some category.
    private KeycodeCategoryChoice _selectedCategory = null!;

    /// <summary>The listed category; never null (a null set is ignored).</summary>
    public KeycodeCategoryChoice SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || !SetProperty(ref _selectedCategory, value)) return;
            RebuildItems();
            OnPropertyChanged(nameof(Items));
            OnPropertyChanged(nameof(MatchCountText));
            OnPropertyChanged(nameof(SelectedItem));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Items))]
    [NotifyPropertyChangedFor(nameof(MatchCountText))]
    [NotifyPropertyChangedFor(nameof(SelectedItem))]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedItem))]
    [NotifyPropertyChangedFor(nameof(Value))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    private ZmkKeycode? _selectedKeycode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    [NotifyPropertyChangedFor(nameof(WrapFlags))]
    private bool _isShift;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    [NotifyPropertyChangedFor(nameof(WrapFlags))]
    private bool _isCtrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    [NotifyPropertyChangedFor(nameof(WrapFlags))]
    private bool _isAlt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    [NotifyPropertyChangedFor(nameof(WrapFlags))]
    private bool _isGui;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    [NotifyPropertyChangedFor(nameof(WrapFlags))]
    private bool _isRightShift;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    [NotifyPropertyChangedFor(nameof(WrapFlags))]
    private bool _isRightCtrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    [NotifyPropertyChangedFor(nameof(WrapFlags))]
    private bool _isRightAlt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    [NotifyPropertyChangedFor(nameof(WrapFlags))]
    private bool _isRightGui;

    [ObservableProperty]
    private bool _isRecording;

    partial void OnSearchTextChanged(string value) => RebuildItems();

    partial void OnSelectedKeycodeChanged(ZmkKeycode? value) => RaiseChanged();

    partial void OnIsShiftChanged(bool value) => RaiseChanged();

    partial void OnIsCtrlChanged(bool value) => RaiseChanged();

    partial void OnIsAltChanged(bool value) => RaiseChanged();

    partial void OnIsGuiChanged(bool value) => RaiseChanged();

    partial void OnIsRightShiftChanged(bool value) => RaiseChanged();

    partial void OnIsRightCtrlChanged(bool value) => RaiseChanged();

    partial void OnIsRightAltChanged(bool value) => RaiseChanged();

    partial void OnIsRightGuiChanged(bool value) => RaiseChanged();

    /// <summary>Starts or stops key recording; the view feeds
    /// <see cref="ApplyRecorded"/> once the recorder has a result.</summary>
    [RelayCommand]
    private void ToggleRecording() => IsRecording = !IsRecording;

    /// <summary>
    /// Opens the picker on one existing logical param (the flat form of a
    /// <c>&amp;kp</c> key, e.g. <c>["LS","N1"]</c>): selects its keycode,
    /// checks the wrap toggles of both hands from its modifiers, and
    /// remembers the exact spelling. Unknown wrapper or keycode → false and
    /// nothing changes. Afterwards <see cref="Value"/> is the same keycode
    /// spelling and the same flags, in
    /// <see cref="ZmkModifierWrap.Wrap"/> order.
    /// </summary>
    public bool Seed(IReadOnlyList<string> group)
    {
        var unwrapped = ZmkModifierWrap.Unwrap(group);
        if (unwrapped is null) return false;
        if (ZmkKeycodeCatalog.Find(unwrapped.Keycode) is not { } keycode) return false;

        _suppressChanged = true;
        try
        {
            SelectedKeycode = keycode;
            ApplyLeftFlags(unwrapped.Modifiers);
            ApplyRightFlags(unwrapped.Modifiers);
            _seedKeycode = keycode;
            _seedSpelling = unwrapped.Keycode;
        }
        finally
        {
            _suppressChanged = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Applies one recorded key: selects its keycode, sets the toggles from
    /// the recorded modifiers (right-side carry-overs dropped) and shows the
    /// keycode's own category. An unknown name only
    /// stops the recording. Either way recording ends; the return says
    /// whether the key took.
    /// </summary>
    public bool ApplyRecorded(RecordedKey key)
    {
        if (ZmkKeycodeCatalog.Find(key.ZmkName) is not { } keycode)
        {
            IsRecording = false;
            return false;
        }

        _suppressChanged = true;
        try
        {
            SelectedKeycode = keycode;
            ApplyLeftFlags(key.Modifiers);
            ApplyRightFlags(ZmkModifiers.None);
            SearchText = "";
            SelectedCategory = Categories.First(c => c.Category == keycode.Category);
            IsRecording = false;
        }
        finally
        {
            _suppressChanged = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private ZmkModifiers LeftToggleFlags =>
        (IsShift ? ZmkModifiers.LeftShift : ZmkModifiers.None) |
        (IsCtrl ? ZmkModifiers.LeftCtrl : ZmkModifiers.None) |
        (IsAlt ? ZmkModifiers.LeftAlt : ZmkModifiers.None) |
        (IsGui ? ZmkModifiers.LeftGui : ZmkModifiers.None);

    private ZmkModifiers RightToggleFlags =>
        (IsRightShift ? ZmkModifiers.RightShift : ZmkModifiers.None) |
        (IsRightCtrl ? ZmkModifiers.RightCtrl : ZmkModifiers.None) |
        (IsRightAlt ? ZmkModifiers.RightAlt : ZmkModifiers.None) |
        (IsRightGui ? ZmkModifiers.RightGui : ZmkModifiers.None);

    /// <summary>Flips the one wrap toggle a single flag names
    /// (<see cref="ZmkModifiers.LeftShift"/> → <see cref="IsShift"/>,
    /// <see cref="ZmkModifiers.RightGui"/> → <see cref="IsRightGui"/>, …),
    /// through the normal property setter, so <see cref="Changed"/> fires
    /// once. The radial key editor's inner ring calls this.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="flag"/> is not exactly one of the eight single
    /// modifier flags.</exception>
    public void ToggleWrap(ZmkModifiers flag)
    {
        switch (flag)
        {
            case ZmkModifiers.LeftShift: IsShift = !IsShift; break;
            case ZmkModifiers.LeftCtrl: IsCtrl = !IsCtrl; break;
            case ZmkModifiers.LeftAlt: IsAlt = !IsAlt; break;
            case ZmkModifiers.LeftGui: IsGui = !IsGui; break;
            case ZmkModifiers.RightShift: IsRightShift = !IsRightShift; break;
            case ZmkModifiers.RightCtrl: IsRightCtrl = !IsRightCtrl; break;
            case ZmkModifiers.RightAlt: IsRightAlt = !IsRightAlt; break;
            case ZmkModifiers.RightGui: IsRightGui = !IsRightGui; break;
            default: throw new ArgumentOutOfRangeException(
                nameof(flag), flag, "Not exactly one of the eight modifier flags.");
        }
    }

    private void ApplyLeftFlags(ZmkModifiers flags)
    {
        IsShift = flags.HasFlag(ZmkModifiers.LeftShift);
        IsCtrl = flags.HasFlag(ZmkModifiers.LeftCtrl);
        IsAlt = flags.HasFlag(ZmkModifiers.LeftAlt);
        IsGui = flags.HasFlag(ZmkModifiers.LeftGui);
    }

    private void ApplyRightFlags(ZmkModifiers flags)
    {
        IsRightShift = flags.HasFlag(ZmkModifiers.RightShift);
        IsRightCtrl = flags.HasFlag(ZmkModifiers.RightCtrl);
        IsRightAlt = flags.HasFlag(ZmkModifiers.RightAlt);
        IsRightGui = flags.HasFlag(ZmkModifiers.RightGui);
    }

    private void RebuildItems()
    {
        Items = ListKeycodes().Select(ToItem).ToList();
    }

    private IReadOnlyList<ZmkKeycode> ListKeycodes()
    {
        // A non-blank search outranks the category: it looks over everything.
        if (!string.IsNullOrWhiteSpace(SearchText))
            return ZmkKeycodeCatalog.Search(SearchText);

        var choice = SelectedCategory;
        if (choice.IsUsed)
            return _usage.Ranked.Select(use => use.Keycode).ToList();
        return choice.Category is { } category
            ? ZmkKeycodeCatalog.InCategory(category)
            : ZmkKeycodeCatalog.All;
    }

    private KeycodeItem ToItem(ZmkKeycode keycode)
    {
        var name = _usage.NameFor(keycode);
        string? shiftGlyph = null;
        if (_shiftedByOf.TryGetValue(keycode.Name, out var shifted))
            shiftGlyph = ZmkKeycodeLabel.FormatKpParams([_usage.NameFor(shifted)]).Label;

        return new KeycodeItem(
            keycode,
            name,
            ZmkKeycodeLabel.FormatKpParams([name]).Label,
            shiftGlyph,
            keycode.Description,
            _usage.CountOf(keycode));
    }

    private void RaiseChanged()
    {
        if (!_suppressChanged) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Dictionary<string, ZmkKeycode> BuildShiftedByOf()
    {
        // ShiftOf sits on the shifted keycode and names the plain one
        // (EXCLAMATION.ShiftOf == "NUMBER_1"); first file-order entry wins —
        // today the catalog has no two keycodes sharing a ShiftOf.
        var map = new Dictionary<string, ZmkKeycode>(StringComparer.Ordinal);
        foreach (var keycode in ZmkKeycodeCatalog.All)
        {
            if (keycode.ShiftOf is { } shiftOf) map.TryAdd(shiftOf, keycode);
        }
        return map;
    }

    private IReadOnlyList<KeycodeCategoryChoice> BuildCategories(ZmkKeycodeUsage usage)
    {
        var choices = new List<KeycodeCategoryChoice>();
        if (usage.Ranked.Count > 0)
        {
            choices.Add(new KeycodeCategoryChoice(
                Loc.Instance["KeyDialog_CategoryUsed"], Category: null, IsUsed: true));
        }
        choices.Add(new KeycodeCategoryChoice(
            Loc.Instance["KeyDialog_CategoryAll"], Category: null, IsUsed: false));

        KeycodeCategoryChoice? modifiers = null;
        foreach (var category in Enum.GetValues<ZmkKeycodeCategory>())
        {
            // Built key: LocalizationKeyTests does not see it; EN/NL parity
            // and the picker tests cover it. Interpolated on purpose — a
            // "KeyDialog_Category_" literal prefix here would fail that scan.
            var choice = new KeycodeCategoryChoice(
                Loc.Instance[$"KeyDialog_Category_{category}"], category, IsUsed: false);
            if (_modifiersFirst && category == ZmkKeycodeCategory.Modifiers)
                modifiers = choice;
            else
                choices.Add(choice);
        }

        if (modifiers is not null) choices.Insert(0, modifiers);
        return choices;
    }

    private KeycodeCategoryChoice DefaultCategory()
    {
        if (_modifiersFirst)
            return Categories.First(c => c.Category == ZmkKeycodeCategory.Modifiers);
        return Categories.FirstOrDefault(c => c.IsUsed)
            ?? Categories.First(c => !c.IsUsed && c.Category is null);
    }
}
