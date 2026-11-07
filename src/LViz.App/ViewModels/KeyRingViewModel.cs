using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;
using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;

namespace LViz.App.ViewModels;

/// <summary>
/// What the ring shows on its main circle: the behavior categories, or the
/// editor of one binding parameter.
/// </summary>
public enum KeyRingStepKind { Behavior, Layer, Command, Keycode, Text }

/// <summary>
/// What the centre disc offers: nothing picked yet, a value still missing,
/// the binding unchanged, or a binding to write.
/// </summary>
public enum KeyRingCenterState { Choosing, Incomplete, Unchanged, Changed }

/// <summary>How far a crumb is: filled in, the step the ring is on, or empty.</summary>
public enum KeyRingCrumbState { Done, Current, Open }

/// <summary>
/// One selectable sector of the ring. <see cref="Id"/> is what
/// <see cref="KeyRingViewModel.Choose"/> takes; <see cref="Label"/> and
/// <see cref="Sub"/> are the two lines the sector draws (a
/// <see cref="LabelIsCode"/>/<see cref="SubIsCode"/> line in the mono code
/// font, the other in the UI font), <see cref="Icon"/> an optional FontAwesome
/// class. <see cref="IsCurrent"/> marks the sector holding the current value,
/// <see cref="IsOn"/> one switched on, <see cref="IsEnabled"/> false for a
/// sector that is shown but not pickable.
/// </summary>
public sealed record KeyRingSegment(
    string Id, string Label, string? Sub, bool LabelIsCode, bool SubIsCode,
    string? Icon, bool IsCurrent, bool IsOn = false, bool IsEnabled = true);

/// <summary>
/// One crumb above the ring — the behavior's category, its code, then one per
/// parameter — with its <see cref="KeyRingCrumbState"/> and whether it
/// differs from what the key opened with.
/// </summary>
public sealed record KeyRingCrumb(string Text, bool IsCode, KeyRingCrumbState State, bool IsChanged);

/// <summary>
/// A sector of the keycode ring. The ten groups are fixed (not the picker's
/// category list): <see cref="Used"/> is the keymap's own ranking, and
/// <see cref="More"/> stands for the categories without a group of their own
/// as well as for the picker's "All" list.
/// </summary>
public enum KeyRingKeyGroup
{
    Used,
    Modifiers,
    Letters,
    Digits,
    Punctuation,
    Symbols,
    Navigation,
    Editing,
    Function,
    More,
}

/// <summary>
/// One row of the list panel: <see cref="Id"/> is what
/// <see cref="KeyRingViewModel.Choose"/> takes, <see cref="Label"/> and
/// <see cref="Sub"/> are the two lines (a <see cref="LabelIsCode"/> line in
/// the mono code font), <see cref="IsSelected"/> marks the row holding the
/// current value and <see cref="IsHighlighted"/> the row the keyboard cursor
/// is on — the view draws the highlight from the row, so it travels with the
/// rows a rebuild produces.
/// </summary>
public sealed record KeyRingPanelRow(
    string Id, string Label, string? Sub, bool LabelIsCode, bool IsSelected,
    bool IsHighlighted = false);

/// <summary>
/// The list panel beside a ring that would need more sectors than it fits
/// (and the keycode list, which always shows). The rows come from a function
/// of the search text, so the panel owns its own search box while the ring
/// view-model owns what the rows hold; <see cref="Refresh"/> re-reads them
/// after anything the source depends on changed.
/// </summary>
public sealed partial class KeyRingPanelViewModel : ObservableObject
{
    private readonly Func<string, IReadOnlyList<KeyRingPanelRow>> _rows;

    /// <summary>The rows as the source produced them, without the highlight.</summary>
    private IReadOnlyList<KeyRingPanelRow> _source = [];

    public KeyRingPanelViewModel(string title, Func<string, IReadOnlyList<KeyRingPanelRow>> rows)
    {
        Title = title;
        _rows = rows;
        Refresh();
    }

    /// <summary>What the panel is listing.</summary>
    public string Title { get; }

    /// <summary>The rows with <see cref="KeyRingPanelRow.IsHighlighted"/> set
    /// on the highlighted row only.</summary>
    public IReadOnlyList<KeyRingPanelRow> Rows { get; private set; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _highlightIndex = -1;

    /// <summary>Id of the highlighted row, or null while no row is.</summary>
    public string? HighlightedId =>
        HighlightIndex >= 0 && HighlightIndex < Rows.Count ? Rows[HighlightIndex].Id : null;

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnHighlightIndexChanged(int value) => ApplyHighlight();

    /// <summary>Moves the highlight by <paramref name="delta"/> rows, clamped
    /// to the list; with no rows there is nothing to highlight.</summary>
    public void MoveHighlight(int delta)
    {
        if (_source.Count == 0)
        {
            HighlightIndex = -1;
            return;
        }

        HighlightIndex = Math.Clamp(HighlightIndex + delta, 0, _source.Count - 1);
    }

    /// <summary>Re-reads the rows and puts the highlight on the selected row,
    /// else on the first row, else nowhere.</summary>
    public void Refresh()
    {
        _source = _rows(SearchText);

        var selected = -1;
        for (int i = 0; i < _source.Count; i++)
        {
            if (_source[i].IsSelected)
            {
                selected = i;
                break;
            }
        }

        HighlightIndex = selected >= 0 ? selected : _source.Count > 0 ? 0 : -1;
        ApplyHighlight();
    }

    private void ApplyHighlight()
    {
        Rows = [.. _source.Select((row, index) => row with { IsHighlighted = index == HighlightIndex })];
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(HighlightedId));
    }
}

/// <summary>
/// The radial key editor: a second view over <see cref="KeyDialogViewModel"/>,
/// so the same binding can be edited by pointing at ring sectors instead of by
/// working through tabs. Everything is derived from the dialog on read — the
/// ring holds no editing state of its own beyond which step it is showing.
///
/// A key that opens on a real binding starts on a summary of it; the category
/// ring (<see cref="Segments"/>) replaces that as soon as a choice or a crumb
/// is taken, and <see cref="BandFor"/> gives the behaviors of the category the
/// view pins. <see cref="Choose"/> applies a sector, <see cref="Back"/> undoes
/// one step, and <see cref="CloseResult"/> hands back the binding to write
/// (null when the key would not change).
/// </summary>
public sealed partial class KeyRingViewModel : ObservableObject
{
    /// <summary>Behaviors still drawn as an outer band; more than this go to a
    /// list (part 5).</summary>
    public const int MaxBandItems = 6;

    /// <summary>Sectors the main ring fits; more than this go to a list.</summary>
    public const int MaxRingSegments = 14;

    private const string CategoryPrefix = "cat:";
    private const string BehaviorPrefix = "beh:";
    private const string LayerPrefix = "layer:";
    private const string CommandPrefix = "cmd:";
    private const string ProfilePrefix = "prof:";
    private const string KeysPrefix = "keys:";
    private const string ModifierPrefix = "mod:";
    private const string KeycodePrefix = "keycode:";

    /// <summary>Id of the free-text Custom entry in a behavior band.</summary>
    private const string CustomCode = "custom";
    private const string CustomId = BehaviorPrefix + CustomCode;

    /// <summary>The piece the centre shows for a parameter without a value.</summary>
    private const string MissingPiece = "___";

    /// <summary>Scratch behavior to re-nest one param group through the
    /// formatter (the dialog's raw seeding uses the same trick).</summary>
    private const string ScratchBehavior = "&x";
    private const string ScratchPrefix = "&x ";

    /// <summary>Codes of the fixed categories, in band order. The keymap group
    /// has none — it is the open keymap's own behaviors plus Custom.</summary>
    private static readonly IReadOnlyDictionary<ZmkBehaviorGroup, string[]> BandCodes =
        new Dictionary<ZmkBehaviorGroup, string[]>
        {
            [ZmkBehaviorGroup.Key] = ["&kp", "&sk", "&caps_word", "&key_repeat"],
            [ZmkBehaviorGroup.HoldTap] = ["&mt", "&lt"],
            [ZmkBehaviorGroup.Layer] = ["&mo", "&to", "&tog", "&sl"],
            [ZmkBehaviorGroup.Bluetooth] = ["&bt"],
            [ZmkBehaviorGroup.Output] = ["&out"],
            [ZmkBehaviorGroup.Lighting] = ["&rgb_ug"],
            [ZmkBehaviorGroup.Mouse] = ["&mkp", "&msc", "&mmv"],
            [ZmkBehaviorGroup.System] = ["&bootloader", "&sys_reset", "&soft_off", "&studio_unlock"],
            [ZmkBehaviorGroup.Clear] = ["&trans", "&none"],
        };

    /// <summary>The inner modifier ring: clockwise from the top, right hand
    /// first, so the right hand fills the right half and the left hand the
    /// left half. The resx keys are the four modifier names plus which hand.</summary>
    private static readonly (ZmkModifiers Flag, string Kind, bool Right)[] ModifierRing =
    [
        (ZmkModifiers.RightCtrl, "Ctrl", true),
        (ZmkModifiers.RightShift, "Shift", true),
        (ZmkModifiers.RightAlt, "Alt", true),
        (ZmkModifiers.RightGui, "Gui", true),
        (ZmkModifiers.LeftGui, "Gui", false),
        (ZmkModifiers.LeftAlt, "Alt", false),
        (ZmkModifiers.LeftShift, "Shift", false),
        (ZmkModifiers.LeftCtrl, "Ctrl", false),
    ];

    /// <summary>The categories a keycode group stands for; groups not listed
    /// here (Keypad, Locks, …) live inside <see cref="KeyRingKeyGroup.More"/>.</summary>
    private static readonly IReadOnlyDictionary<ZmkKeycodeCategory, KeyRingKeyGroup> CategoryGroup =
        new Dictionary<ZmkKeycodeCategory, KeyRingKeyGroup>
        {
            [ZmkKeycodeCategory.Modifiers] = KeyRingKeyGroup.Modifiers,
            [ZmkKeycodeCategory.Letters] = KeyRingKeyGroup.Letters,
            [ZmkKeycodeCategory.Digits] = KeyRingKeyGroup.Digits,
            [ZmkKeycodeCategory.Punctuation] = KeyRingKeyGroup.Punctuation,
            [ZmkKeycodeCategory.Symbols] = KeyRingKeyGroup.Symbols,
            [ZmkKeycodeCategory.Navigation] = KeyRingKeyGroup.Navigation,
            [ZmkKeycodeCategory.Editing] = KeyRingKeyGroup.Editing,
            [ZmkKeycodeCategory.Function] = KeyRingKeyGroup.Function,
        };

    private readonly KeyDialogRequest _request;
    private readonly KeyBinding _original;

    /// <summary>The opened binding as keymap text — what the centre shows as
    /// replaced, and Custom's stand-in "code" for the crumb comparisons.</summary>
    private readonly string _originalText;

    /// <summary>Whether the key opened on Custom (leaving it then always
    /// counts as a behavior change).</summary>
    private readonly bool _openedAsCustom;

    /// <summary>Each parameter's <see cref="ValueShort"/> as the key opened
    /// with, for the crumbs' IsChanged.</summary>
    private readonly IReadOnlyList<string> _openedShorts;

    private bool _isSummary;
    private bool _showCustomText;

    /// <summary>The cached list panel and the key of the source it was built
    /// for; a new instance only when that source changes (part 5).</summary>
    private KeyRingPanelViewModel? _panel;
    private string? _panelSource;

    /// <summary>The category whose too-wide behavior list the panel shows, or
    /// null when no behaviors panel is open.</summary>
    private ZmkBehaviorGroup? _behaviorsPanel;

    /// <summary>The keycode step's state: which parameter it was entered on
    /// and which group the ring and panel show. A new parameter (or leaving
    /// the step) resets both.</summary>
    private KeyParamViewModel? _keycodeParam;
    private KeyRingKeyGroup _keycodeGroup = KeyRingKeyGroup.More;

    public KeyRingViewModel(KeyDialogViewModel dialog, KeyDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(request);

        Dialog = dialog;
        _request = request;
        _original = request.Binding
            ?? _request.Config.Layers[_request.LayerIndex].Bindings[_request.KeyIndex];
        _originalText = ZmkBindingFormatter.Format(
            _original, _request.LayerDefines ?? _request.Defines, _request.Config.HoldTaps);
        _openedShorts = [.. dialog.Params.Select(ValueShortOf)];

        // A key that opened on Custom goes straight to its text; one that
        // opened on &trans/&none has nothing to summarise and starts on the
        // category ring.
        _openedAsCustom = dialog.IsCustom;
        _showCustomText = dialog.IsCustom;
        _isSummary = !dialog.IsCustom
            && !string.Equals(_original.Behavior, "&trans", StringComparison.Ordinal)
            && !string.Equals(_original.Behavior, "&none", StringComparison.Ordinal);

        dialog.PropertyChanged += (_, _) => NotifyAll();

        // The panel and the keycode step's group are state, not derived, so
        // they need the one sync the constructor never gets a notification for.
        SyncKeycodeStep();
        SyncPanel();
    }

    /// <summary>The dialog this ring edits. The ring never owns it.</summary>
    public KeyDialogViewModel Dialog { get; }

    /// <summary>Whether the ring shows the summary of the binding the key
    /// opened with, instead of a step. Ended by the first accepted choice or
    /// crumb; never returned to automatically.</summary>
    public bool IsSummary => _isSummary;

    /// <summary>Which step the main ring is on.</summary>
    public KeyRingStepKind StepKind
    {
        get
        {
            if (_isSummary) return KeyRingStepKind.Behavior;

            int tab = CurrentTab;
            if (tab == 0)
                return Dialog.IsCustom && _showCustomText
                    ? KeyRingStepKind.Text
                    : KeyRingStepKind.Behavior;

            return Dialog.Params[tab - 1].Kind switch
            {
                ZmkBehaviorParamKind.Layer => KeyRingStepKind.Layer,
                ZmkBehaviorParamKind.Command => KeyRingStepKind.Command,
                ZmkBehaviorParamKind.Raw => KeyRingStepKind.Text,
                _ => KeyRingStepKind.Keycode,
            };
        }
    }

    /// <summary>The main ring: the ten behavior categories on the behavior
    /// step, the parameter's own choices on a parameter step (layers,
    /// commands, the ten keycode groups), and nothing on the free-text step
    /// or where a ring would not fit.</summary>
    public IReadOnlyList<KeyRingSegment> Segments
    {
        get
        {
            switch (StepKind)
            {
                case KeyRingStepKind.Behavior:
                    return CategorySegments();

                case KeyRingStepKind.Layer when LayerParam is { } layer
                    && layer.Layers.Count <= MaxRingSegments:
                    return
                    [
                        .. layer.Layers.Select(choice => new KeyRingSegment(
                            LayerPrefix + choice.Index.ToString(CultureInfo.InvariantCulture),
                            LayerName(choice.Index),
                            choice.Index.ToString(CultureInfo.InvariantCulture),
                            LabelIsCode: false, SubIsCode: false, Icon: null,
                            IsCurrent: choice.Index == layer.SelectedLayer?.Index)),
                    ];

                case KeyRingStepKind.Command when CommandParam is { } command
                    && command.Commands.Count <= MaxRingSegments:
                    return
                    [
                        .. command.Commands.Select(choice => new KeyRingSegment(
                            CommandPrefix + choice.Command.Name,
                            choice.Name,
                            choice.Command.Name,
                            LabelIsCode: false, SubIsCode: true, Icon: null,
                            IsCurrent: ReferenceEquals(choice, command.SelectedCommand))),
                    ];

                case KeyRingStepKind.Keycode when KeycodePicker is { } picker:
                    return KeycodeSegments(picker);

                default:
                    return [];
            }
        }
    }

    /// <summary>The inner modifier ring: the eight wrap toggles in
    /// <see cref="ModifierRing"/> order on a keycode step whose picker shows
    /// them. Empty everywhere else (a <c>&amp;mt</c>-style Modifier param has
    /// none, and no other step has an inner ring).</summary>
    public IReadOnlyList<KeyRingSegment> InnerSegments
    {
        get
        {
            if (StepKind != KeyRingStepKind.Keycode || KeycodePicker is not { } picker) return [];
            if (!picker.ShowWrapToggles) return [];

            var loc = Loc.Instance;
            var flags = picker.WrapFlags;
            return
            [
                .. ModifierRing.Select(entry => new KeyRingSegment(
                    ModifierPrefix + entry.Flag,
                    loc[$"Ring_Mod_{entry.Kind}"],
                    entry.Right ? Loc.Instance["Ring_Mod_Right"] : Loc.Instance["Ring_Mod_Left"],
                    LabelIsCode: false, SubIsCode: false, Icon: null,
                    IsCurrent: false,
                    IsOn: flags.HasFlag(entry.Flag))),
            ];
        }
    }

    /// <summary>The list panel for the current step, or null when the ring
    /// shows everything. Cached: a new instance only when the source changes
    /// (the step, the keycode group, or the behaviors list of a category), so
    /// the search text and the highlight survive an ordinary dialog change.</summary>
    public KeyRingPanelViewModel? Panel => _panel;

    private IReadOnlyList<KeyRingSegment> CategorySegments()
    {
        var loc = Loc.Instance;
        var current = CategoryOf(Dialog.SelectedBehavior);
        var segments = new List<KeyRingSegment>();

        foreach (var group in Enum.GetValues<ZmkBehaviorGroup>())
        {
            segments.Add(new KeyRingSegment(
                CategoryPrefix + group,
                loc[$"Ring_Category_{group}"],
                CategorySub(group),
                LabelIsCode: false,
                SubIsCode: group != ZmkBehaviorGroup.Keymap,
                Icon: IconFor(group),
                IsCurrent: group == current));
        }

        return segments;
    }

    /// <summary>The ten fixed keycode groups; <c>Used</c> is only pickable
    /// when the picker lists a Used category, and the picked keycode's own
    /// group is the current sector.</summary>
    private IReadOnlyList<KeyRingSegment> KeycodeSegments(KeycodePickerViewModel picker)
    {
        var loc = Loc.Instance;
        var hasUsed = picker.Categories.Any(category => category.IsUsed);
        var current = picker.SelectedKeycode is { } picked ? GroupOf(picked.Category) : (KeyRingKeyGroup?)null;

        return
        [
            .. Enum.GetValues<KeyRingKeyGroup>().Select(group => new KeyRingSegment(
                KeysPrefix + group,
                loc[$"Ring_Keys_{group}"],
                group == KeyRingKeyGroup.Used
                    ? Loc.Instance.Format("Ring_Keys_Used_SubFormat", Dialog.Usage.Ranked.Count)
                    : loc[$"Ring_Keys_{group}_Sub"],
                LabelIsCode: false, SubIsCode: false, Icon: null,
                IsCurrent: group == current,
                IsEnabled: group != KeyRingKeyGroup.Used || hasUsed)),
        ];
    }

    /// <summary>The outer band of behavior codes for one category segment: one
    /// sector each for a category with 2 to <see cref="MaxBandItems"/>
    /// behaviors. Single-behavior categories have no band (they are chosen on
    /// the category itself) and wide ones go to the list panel. For a command
    /// that takes a profile number the band is that command's profiles.</summary>
    public IReadOnlyList<KeyRingSegment> BandFor(string segmentId)
    {
        if (TryCategory(segmentId, out var group))
        {
            var behaviors = BehaviorsIn(group);
            if (behaviors.Count < 2 || behaviors.Count > MaxBandItems) return [];

            return behaviors
                .Select(behavior => BehaviorSegment(behavior, IsSelected(behavior)))
                .ToArray();
        }

        if (segmentId.StartsWith(CommandPrefix, StringComparison.Ordinal)
            && CommandParam is { } command
            && FindCommand(command, segmentId[CommandPrefix.Length..]) is { Command.TakesNumber: true } choice)
        {
            return
            [
                .. command.Profiles.Select(profile => new KeyRingSegment(
                    ProfilePrefix + choice.Command.Name + ":" + profile.Number,
                    profile.Text,
                    Loc.Instance["Ring_Profile_Sub"],
                    LabelIsCode: true, SubIsCode: false, Icon: null,
                    IsCurrent: ReferenceEquals(choice, command.SelectedCommand)
                        && ReferenceEquals(profile, command.SelectedProfile))),
            ];
        }

        return [];
    }

    // ─── Applying a choice ────────────────────────────────────────────────

    /// <summary>
    /// Applies the sector <paramref name="id"/> and returns whether something
    /// happened. A category with exactly one behavior is that behavior (a wider
    /// category is pinned to its band by the view, and one too wide for a band
    /// opens the list panel); a behavior switches to it or, when it is already
    /// selected, moves on to the first parameter still without a value; Custom
    /// opens the text step. A parameter sector (a layer, a command without a
    /// profile number, a profile number, a keycode group, a modifier toggle, a
    /// keycode) edits its parameter and steps on to what still needs a value.
    /// Anything that isn't a category choice drops the behaviors panel.
    /// Unknown ids do nothing.
    /// </summary>
    public bool Choose(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        if (id.StartsWith(CategoryPrefix, StringComparison.Ordinal))
        {
            if (!TryCategory(id, out var group)) return false;

            var behaviors = BehaviorsIn(group);
            if (behaviors.Count > 1)
            {
                // Too wide for the ring: over a band it goes to the list
                // panel, in band size the view pins the band instead.
                if (behaviors.Count <= MaxBandItems) return false;
                _behaviorsPanel = group;
                NotifyAll();
                return true;
            }

            if (!ApplyChoice(behaviors[0])) return false;
        }
        else
        {
            var applied = id switch
            {
                _ when id.StartsWith(BehaviorPrefix, StringComparison.Ordinal) => ChooseBehavior(id),
                _ when id.StartsWith(LayerPrefix, StringComparison.Ordinal) => ChooseLayer(id),
                _ when id.StartsWith(ProfilePrefix, StringComparison.Ordinal) => ChooseProfile(id),
                _ when id.StartsWith(CommandPrefix, StringComparison.Ordinal) => ChooseCommand(id),
                _ when id.StartsWith(KeysPrefix, StringComparison.Ordinal) => ChooseKeys(id),
                _ when id.StartsWith(ModifierPrefix, StringComparison.Ordinal) => ChooseModifier(id),
                _ when id.StartsWith(KeycodePrefix, StringComparison.Ordinal) => ChooseKeycode(id),
                _ => false,
            };

            if (!applied) return false;
        }

        _behaviorsPanel = null;
        NotifyAll();
        return true;
    }

    /// <summary>Whether choosing <paramref name="id"/> sets part of the
    /// binding (a behavior, layer, command, profile or keycode), as opposed to
    /// browsing a keycode group or flipping a modifier toggle. A double-click
    /// on such a sector saves once the binding is complete.</summary>
    public static bool PicksAValue(string id) =>
        !id.StartsWith(KeysPrefix, StringComparison.Ordinal)
        && !id.StartsWith(ModifierPrefix, StringComparison.Ordinal);

    private bool ChooseBehavior(string id)
    {
        var choice = FindBehavior(id[BehaviorPrefix.Length..]);
        return choice is not null && ApplyChoice(choice);
    }

    /// <summary>The layer sector's layer, then step on.</summary>
    private bool ChooseLayer(string id)
    {
        if (LayerParam is not { } param) return false;
        if (!int.TryParse(id[LayerPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            return false;

        var choice = param.Layers.FirstOrDefault(layer => layer.Index == index);
        if (choice is null) return false;

        param.SelectedLayer = choice;
        Advance();
        return true;
    }

    /// <summary>A command that takes a profile number is picked together with
    /// that number; stepping onto its band is the view's job (see
    /// <see cref="ChooseCommand"/>).</summary>
    private bool ChooseProfile(string id)
    {
        if (CommandParam is not { } param) return false;

        var rest = id[ProfilePrefix.Length..];
        var split = rest.LastIndexOf(':');
        if (split <= 0) return false;
        if (FindCommand(param, rest[..split]) is not { Command.TakesNumber: true } choice) return false;
        if (!int.TryParse(rest[(split + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return false;

        var profile = param.Profiles.FirstOrDefault(p => p.Number == number);
        if (profile is null) return false;

        param.SelectedCommand = choice;
        param.SelectedProfile = profile;
        Advance();
        return true;
    }

    /// <summary>A command without a number is picked here; one that takes a
    /// number returns false — the view pins its profile band instead.</summary>
    private bool ChooseCommand(string id)
    {
        if (CommandParam is not { } param) return false;
        if (FindCommand(param, id[CommandPrefix.Length..]) is not { } choice) return false;
        if (choice.Command.TakesNumber) return false;

        param.SelectedCommand = choice;
        Advance();
        return true;
    }

    /// <summary>Shows one keycode group: the picker searches nothing and lists
    /// that category (Used, the category itself, or All for More). The step
    /// stays where it is.</summary>
    private bool ChooseKeys(string id)
    {
        if (KeycodePicker is not { } picker) return false;
        if (!TryKeyGroup(id[KeysPrefix.Length..], out var group)) return false;
        if (CategoryChoice(picker, group) is not { } category) return false;

        picker.SearchText = "";
        picker.SelectedCategory = category;
        _keycodeGroup = group;
        return true;
    }

    /// <summary>Flips one wrap toggle.</summary>
    private bool ChooseModifier(string id)
    {
        if (KeycodePicker is not { } picker) return false;

        var name = id[ModifierPrefix.Length..];
        if (!Enum.TryParse(name, out ZmkModifiers flag) || flag.ToString() != name) return false;
        if (flag == ZmkModifiers.None || (flag & (flag - 1)) != 0) return false;

        picker.ToggleWrap(flag);
        return true;
    }

    /// <summary>Picks the listed keycode with that canonical name, then steps
    /// on. A keycode the list does not hold (search filtering, a different
    /// group) does nothing.</summary>
    private bool ChooseKeycode(string id)
    {
        if (KeycodePicker is not { } picker) return false;

        var name = id[KeycodePrefix.Length..];
        var item = picker.Items.FirstOrDefault(entry =>
            string.Equals(entry.Keycode.Name, name, StringComparison.Ordinal));
        if (item is null) return false;

        picker.SelectedItem = item;
        Advance();
        return true;
    }

    /// <summary>Moves to the first parameter still without a value; with all
    /// of them filled in the ring stays put.</summary>
    private void Advance()
    {
        for (int i = 0; i < Dialog.Params.Count; i++)
        {
            if (!Dialog.Params[i].HasValue)
            {
                Dialog.SelectedTabIndex = i + 1;
                return;
            }
        }
    }

    /// <summary>Applies a behavior; <see cref="Choose"/> raises the
    /// notification once for every accepted id.</summary>
    private bool ApplyChoice(KeyBehaviorChoice choice)
    {
        _isSummary = false;

        if (choice.IsCustom)
        {
            // Switching to Custom hands the current preview to the text box;
            // picking it again only reopens that text step.
            if (!Dialog.IsCustom) Dialog.SelectedBehavior = choice;
            _showCustomText = true;
            return true;
        }

        _showCustomText = false;

        if (IsSelected(choice))
        {
            // Already the behavior: step on to what still needs a value, or to
            // the first parameter when all have one. With nothing to fill in
            // the ring stays put and the centre offers save.
            int firstMissing = -1;
            for (int i = 0; i < Dialog.Params.Count; i++)
            {
                if (!Dialog.Params[i].HasValue)
                {
                    firstMissing = i + 1;
                    break;
                }
            }

            if (firstMissing > 0) Dialog.SelectedTabIndex = firstMissing;
            else if (Dialog.Params.Count > 0) Dialog.SelectedTabIndex = 1;
        }
        else
        {
            // The dialog lands the tab on the first parameter without a value
            // and carries over what still fits.
            Dialog.SelectedBehavior = choice;
        }

        return true;
    }

    /// <summary>Jumps to a crumb: 0 the category ring, 1 the behavior (its
    /// free text hidden), 2 and up the parameters. An index past the last
    /// parameter does nothing.</summary>
    public void GoToCrumb(int index)
    {
        if (index < 0) return;

        if (index <= 1)
        {
            _isSummary = false;
            _showCustomText = false;
            Dialog.SelectedTabIndex = 0;
            NotifyAll();
            return;
        }

        int tab = index - 1;
        if (tab > Dialog.Params.Count) return;

        _isSummary = false;
        Dialog.SelectedTabIndex = tab;
        NotifyAll();
    }

    /// <summary>One step back: the free-text step to the category ring, a
    /// parameter to the previous tab. The summary and the category ring have
    /// nothing back to.</summary>
    public void Back()
    {
        if (_isSummary) return;

        if (CurrentTab == 0)
        {
            if (Dialog.IsCustom && _showCustomText)
            {
                _showCustomText = false;
                NotifyAll();
            }

            return;
        }

        Dialog.PreviousTab();
        NotifyAll();
    }

    // ─── The text step ────────────────────────────────────────────────────

    /// <summary>The editable text of the current text step: Custom's binding,
    /// or a raw parameter's token. Empty on any other step; setting it there
    /// does nothing.</summary>
    public string Text
    {
        get => Dialog.IsCustom ? Dialog.CustomText : RawParam?.Text ?? "";
        set
        {
            if (Dialog.IsCustom) Dialog.CustomText = value;
            else if (RawParam is { } raw) raw.Text = value;
        }
    }

    /// <summary>Why the current text does not parse; empty when it does.</summary>
    public string TextError => Dialog.ErrorText;

    // ─── Closing ──────────────────────────────────────────────────────────

    /// <summary>Whether the binding is complete enough to close on.</summary>
    public bool CanClose => Dialog.Result is not null;

    /// <summary>The binding to write, or null when nothing is complete or the
    /// key would keep the binding it opened with.</summary>
    public KeyBinding? CloseResult
    {
        get
        {
            if (Dialog.Result is not { } result) return null;
            return Same(result, _original) ? null : result;
        }
    }

    // ─── Crumbs, centre and hint ──────────────────────────────────────────

    /// <summary>The steps above the ring: the behavior's category, its code
    /// (Custom shows its name, not a code), then one per parameter — with a
    /// value as "label: value", without one as "label: ?". The crumb of the
    /// step the ring is on is <see cref="KeyRingCrumbState.Current"/> (the
    /// summary has none), parameters still without a value are Open, and the
    /// rest Done; a crumb reports IsChanged when it differs from what the key
    /// opened with.</summary>
    public IReadOnlyList<KeyRingCrumb> Crumbs
    {
        get
        {
            var current = CurrentCrumb;
            var behaviorChanged = BehaviorChanged;
            var editors = Dialog.Params;

            var crumbs = new List<KeyRingCrumb>(editors.Count + 2)
            {
                new(Loc.Instance[$"Ring_Category_{CategoryOf(Dialog.SelectedBehavior)}"],
                    IsCode: false, StateOf(0, hasValue: true), IsChanged: false),
                new(Dialog.IsCustom
                        ? Loc.Instance["Ring_Custom_Name"]
                        : Dialog.SelectedBehavior.Code,
                    IsCode: !Dialog.IsCustom, StateOf(1, hasValue: true), behaviorChanged),
            };

            for (int i = 0; i < editors.Count; i++)
            {
                var param = editors[i];
                var valueShort = ValueShortOf(param);
                crumbs.Add(new KeyRingCrumb(
                    param.HasValue
                        ? Loc.Instance.Format("Ring_DescriptionItemFormat", param.Label, valueShort)
                        : Loc.Instance.Format("Ring_CrumbOpenFormat", param.Label),
                    IsCode: false,
                    StateOf(i + 2, param.HasValue),
                    behaviorChanged || !string.Equals(
                        valueShort, i < _openedShorts.Count ? _openedShorts[i] : "",
                        StringComparison.Ordinal)));
            }

            return crumbs;
        }
    }

    /// <summary>What the centre disc offers: nothing complete yet, the
    /// binding the key opened with (only shown as <see cref="KeyRingCenterState.Unchanged"/>
    /// on the summary — away from it the same binding means the ring is
    /// simply pointing somewhere), or a binding to write.</summary>
    public KeyRingCenterState CenterState
    {
        get
        {
            if (Dialog.Result is not { } result) return KeyRingCenterState.Incomplete;
            if (Same(result, _original))
                return _isSummary ? KeyRingCenterState.Unchanged : KeyRingCenterState.Choosing;
            return KeyRingCenterState.Changed;
        }
    }

    /// <summary>Which key on which layer, or the request's caption when it
    /// carries one (the view upper-cases it).</summary>
    public string CenterCaption => _request.Caption ?? Loc.Instance.Format(
        "Ring_CaptionFormat", _request.KeyIndex, _request.Config.Layers[_request.LayerIndex].Name);

    /// <summary>The binding text the key opened with — what a write would
    /// replace; empty unless the centre shows a change or a gap.</summary>
    public string CenterBefore =>
        CenterState is KeyRingCenterState.Changed or KeyRingCenterState.Incomplete
            ? _originalText
            : "";

    /// <summary>The binding as it would be written: the preview once one is
    /// complete; otherwise the code plus one piece per parameter, <c>___</c>
    /// for the ones without a value; Custom shows its typed text.</summary>
    public string CenterBinding
    {
        get
        {
            if (Dialog.Result is not null) return Dialog.PreviewText;

            if (Dialog.IsCustom)
            {
                var text = Dialog.CustomText.Trim();
                return text.Length > 0 ? text : MissingPiece;
            }

            var pieces = new List<string>(Dialog.Params.Count + 1) { Dialog.SelectedBehavior.Code };
            pieces.AddRange(Dialog.Params.Select(param =>
                param.HasValue ? FormatGroup(param.Values!) : MissingPiece));
            return string.Join(" ", pieces);
        }
    }

    /// <summary>The line under the centre binding: save it, what is still
    /// missing, a description of the unchanged binding, or the prompt to
    /// point at a sector.</summary>
    public string CenterLine
    {
        get
        {
            switch (CenterState)
            {
                case KeyRingCenterState.Changed:
                    return Loc.Instance["Ring_Center_Save"];

                case KeyRingCenterState.Incomplete when Dialog.IsCustom:
                    return Dialog.ErrorText.Length > 0
                        ? Dialog.ErrorText
                        : Loc.Instance["Ring_Center_TypeBinding"];

                case KeyRingCenterState.Incomplete:
                    var missing = Dialog.Params.FirstOrDefault(param => !param.HasValue);
                    if (missing is not null)
                        return Loc.Instance.Format("Ring_Center_PickFormat", missing.Label);

                    // Every parameter has a value but the whole does not
                    // parse (a raw token the loader rejects): the error text
                    // is the only thing worth showing.
                    return Dialog.ErrorText.Length > 0
                        ? Dialog.ErrorText
                        : Loc.Instance["Ring_Center_TypeBinding"];

                case KeyRingCenterState.Unchanged when Dialog.Params.Count > 0:
                    return string.Join(
                        " · ",
                        Dialog.Params.Select(param => Loc.Instance.Format(
                            "Ring_DescriptionItemFormat", param.Label, ValueShortOf(param))));

                case KeyRingCenterState.Unchanged:
                    return Dialog.SelectedBehavior.Name;

                default:
                    return Loc.Instance["Ring_Center_Choose"];
            }
        }
    }

    /// <summary>The gesture reminder for the current step.</summary>
    public string Hint => _isSummary
        ? Loc.Instance["Ring_Hint_Summary"]
        : StepKind == KeyRingStepKind.Text
            ? Loc.Instance["Ring_Hint_Text"]
            : Loc.Instance["Ring_Hint_Choose"];

    /// <summary>Which crumb the ring is on: none in the summary, the category
    /// on the behavior ring, the behavior on the Custom text step, and
    /// parameter <c>i</c> on tab <c>i</c>.</summary>
    private int CurrentCrumb =>
        _isSummary ? -1
        : CurrentTab == 0 ? (Dialog.IsCustom && _showCustomText ? 1 : 0)
        : CurrentTab + 1;

    private KeyRingCrumbState StateOf(int index, bool hasValue) =>
        index == CurrentCrumb ? KeyRingCrumbState.Current
        : hasValue ? KeyRingCrumbState.Done
        : KeyRingCrumbState.Open;

    /// <summary>Whether the picked behavior differs from the one the key
    /// opened with. Custom has no code, so its text stands in: the dialog
    /// seeds it with exactly the original's keymap text, which makes an
    /// untouched Custom open no change; leaving Custom always is one.</summary>
    private bool BehaviorChanged =>
        Dialog.IsCustom
            ? !string.Equals(Dialog.CustomText.Trim(), _originalText, StringComparison.Ordinal)
            : _openedAsCustom
                || !string.Equals(Dialog.SelectedBehavior.Code, _original.Behavior, StringComparison.Ordinal);

    /// <summary>A parameter's value for crumbs and descriptions: a layer's
    /// name, a command's flat tokens (command plus profile number), anything
    /// else the editor's own display text; empty while it has no value.</summary>
    private string ValueShortOf(KeyParamViewModel param) => param switch
    {
        LayerParamViewModel { SelectedLayer: { } layer } => _request.Config.Layers[layer.Index].Name,
        CommandParamViewModel => param.Values is { } values ? string.Join(" ", values) : "",
        _ => param.ValueText,
    };

    /// <summary>One logical param group as keymap text without the behavior
    /// cell, so a picked group renders nested modifiers like the file does.</summary>
    private string FormatGroup(IReadOnlyList<string> values)
    {
        var text = ZmkBindingFormatter.Format(new KeyBinding(ScratchBehavior, values), _request.Defines);
        return text.StartsWith(ScratchPrefix, StringComparison.Ordinal)
            ? text[ScratchPrefix.Length..]
            : text;
    }

    // ─── Internals shared with the later parts ────────────────────────────

    /// <summary>The dialog tab the ring follows, clamped to the parameters the
    /// behavior actually has.</summary>
    private int CurrentTab => Math.Clamp(Dialog.SelectedTabIndex, 0, Dialog.Params.Count);

    private RawParamViewModel? RawParam =>
        CurrentTab > 0 ? Dialog.Params[CurrentTab - 1] as RawParamViewModel : null;

    private void NotifyAll()
    {
        SyncKeycodeStep();
        SyncPanel();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Every change goes through here, so this is where the keycode
    /// step notices that it was entered: the ring shows the picked keycode's
    /// group (or, with nothing picked, the group of the picker's own default
    /// category), and the picker's category is set to match so the list
    /// holds what is picked. A step whose editor is not a keycode picker (a
    /// number parameter) has no group and no ring.</summary>
    private void SyncKeycodeStep()
    {
        if (StepEditor as KeycodeParamViewModel is not { } editor)
        {
            _keycodeParam = null;
            return;
        }

        if (ReferenceEquals(_keycodeParam, editor)) return;
        _keycodeParam = editor;

        var picker = editor.Picker;
        _keycodeGroup = picker.SelectedKeycode is { } picked
            ? GroupOf(picked.Category)
            : GroupOfChoice(picker.SelectedCategory);

        if (CategoryChoice(picker, _keycodeGroup) is { } category) picker.SelectedCategory = category;
    }

    /// <summary>Rebuilds the panel only when its source changes; every other
    /// change just re-reads its rows, which keeps the search text and (after
    /// a rebuild) the highlight on the picked row.</summary>
    private void SyncPanel()
    {
        var (key, title, rows) = PanelSource();
        if (key is null)
        {
            _panel = null;
            _panelSource = null;
            return;
        }

        if (_panel is not null && _panelSource == key)
        {
            _panel.Refresh();
            return;
        }

        _panel = new KeyRingPanelViewModel(title!, rows!);
        _panelSource = key;
    }

    /// <summary>The panel that applies: the behaviors of a category too wide
    /// for a band while that panel is open (only a non-category choice drops
    /// it), else the keycode list (always beside that step), else the layer or
    /// command list a ring cannot fit. Null plus a null key when none does.
    /// Every key names the editor instance the row function closes over, so a
    /// rebuilt editor can never leave a panel reading the old one.</summary>
    private (string? Key, string? Title, Func<string, IReadOnlyList<KeyRingPanelRow>>? Rows) PanelSource()
    {
        var loc = Loc.Instance;

        if (_behaviorsPanel is { } group)
        {
            var behaviors = BehaviorsIn(group);
            return (
                $"behaviors/{group}/{string.Join(",", behaviors.Select(behavior => behavior.Code))}",
                loc[$"Ring_Category_{group}"],
                search => FilterRows(BehaviorRows(behaviors), search));
        }

        var tab = CurrentTab.ToString(CultureInfo.InvariantCulture);

        switch (StepKind)
        {
            case KeyRingStepKind.Keycode when StepEditor is { } editor && KeycodePicker is { } picker:
                return (
                    $"keys/{RuntimeHelpers.GetHashCode(editor)}/{tab}/{_keycodeGroup}",
                    loc[$"Ring_Keys_{_keycodeGroup}"],
                    search => KeycodeRows(picker, search));

            case KeyRingStepKind.Layer when LayerParam is { Layers: { Count: > MaxRingSegments } } layer:
                return (
                    $"layers/{RuntimeHelpers.GetHashCode(layer)}/{tab}",
                    Loc.Instance["Ring_Panel_Layers"],
                    search => FilterRows(LayerRows(layer), search));

            case KeyRingStepKind.Command when CommandParam is { Commands: { Count: > MaxRingSegments } } command:
                return (
                    $"commands/{RuntimeHelpers.GetHashCode(command)}/{tab}",
                    command.Label,
                    search => FilterRows(CommandRows(command), search));
        }

        return (null, null, null);
    }

    /// <summary>The picker's own list: the search runs through the picker, so
    /// the panel and the dialog's own keycode editor cannot drift apart.</summary>
    private static IReadOnlyList<KeyRingPanelRow> KeycodeRows(KeycodePickerViewModel picker, string search)
    {
        picker.SearchText = search;
        var picked = picker.SelectedKeycode;

        return
        [
            .. picker.Items.Select(item => new KeyRingPanelRow(
                KeycodePrefix + item.Keycode.Name,
                item.Name,
                item.Description,
                LabelIsCode: true,
                IsSelected: item.Keycode == picked)),
        ];
    }

    private IReadOnlyList<KeyRingPanelRow> LayerRows(LayerParamViewModel param) =>
        [
            .. param.Layers.Select(choice => new KeyRingPanelRow(
                LayerPrefix + choice.Index.ToString(CultureInfo.InvariantCulture),
                LayerName(choice.Index),
                choice.Index.ToString(CultureInfo.InvariantCulture),
                LabelIsCode: false,
                IsSelected: choice.Index == param.SelectedLayer?.Index)),
        ];

    /// <summary>A command that takes a profile number is one row per profile
    /// instead of one row of its own — the row says what it writes.</summary>
    private IReadOnlyList<KeyRingPanelRow> CommandRows(CommandParamViewModel param)
    {
        var rows = new List<KeyRingPanelRow>();

        foreach (var choice in param.Commands)
        {
            if (!choice.Command.TakesNumber)
            {
                rows.Add(new KeyRingPanelRow(
                    CommandPrefix + choice.Command.Name,
                    choice.Name,
                    choice.Command.Name,
                    LabelIsCode: false,
                    IsSelected: ReferenceEquals(choice, param.SelectedCommand)));
                continue;
            }

            rows.AddRange(param.Profiles.Select(profile => new KeyRingPanelRow(
                ProfilePrefix + choice.Command.Name + ":" + profile.Number,
                Loc.Instance.Format("KeyDialog_CommandProfileFormat", choice.Name, profile.Number),
                null,
                LabelIsCode: false,
                IsSelected: ReferenceEquals(choice, param.SelectedCommand)
                    && ReferenceEquals(profile, param.SelectedProfile))));
        }

        return rows;
    }

    /// <summary>The too-wide behavior list, rowed like the band draws it.</summary>
    private IReadOnlyList<KeyRingPanelRow> BehaviorRows(IReadOnlyList<KeyBehaviorChoice> behaviors) =>
        [
            .. behaviors.Select(behavior =>
            {
                var segment = BehaviorSegment(behavior, IsSelected(behavior));
                return new KeyRingPanelRow(
                    segment.Id, segment.Label, segment.Sub, segment.LabelIsCode, segment.IsCurrent);
            }),
        ];

    /// <summary>The list panels filter what the ring cannot show: label or sub
    /// containing the search text. The keycode panel does its own searching
    /// through the picker.</summary>
    private static IReadOnlyList<KeyRingPanelRow> FilterRows(
        IReadOnlyList<KeyRingPanelRow> rows, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return rows;

        return rows
            .Where(row => row.Label.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (row.Sub?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToArray();
    }

    /// <summary>The editor of the step the ring is on, or null on a step that
    /// has none.</summary>
    private KeyParamViewModel? StepEditor =>
        StepKind is KeyRingStepKind.Layer or KeyRingStepKind.Command or KeyRingStepKind.Keycode
            ? Dialog.Params[CurrentTab - 1]
            : null;

    private LayerParamViewModel? LayerParam => StepEditor as LayerParamViewModel;

    private CommandParamViewModel? CommandParam => StepEditor as CommandParamViewModel;

    private KeycodePickerViewModel? KeycodePicker =>
        StepEditor is KeycodeParamViewModel keycode ? keycode.Picker : null;

    private string LayerName(int index) => _request.Config.Layers[index].Name;

    private static CommandChoice? FindCommand(CommandParamViewModel param, string name) =>
        param.Commands.FirstOrDefault(choice =>
            string.Equals(choice.Command.Name, name, StringComparison.Ordinal));

    /// <summary>The group a category belongs to; the ones without a group of
    /// their own are inside More.</summary>
    private static KeyRingKeyGroup GroupOf(ZmkKeycodeCategory category) =>
        CategoryGroup.TryGetValue(category, out var group) ? group : KeyRingKeyGroup.More;

    private static KeyRingKeyGroup GroupOfChoice(KeycodeCategoryChoice choice) =>
        choice.IsUsed ? KeyRingKeyGroup.Used
        : choice.Category is { } category ? GroupOf(category)
        : KeyRingKeyGroup.More;

    /// <summary>The picker's category list entry for a group, or null when the
    /// picker does not list it (a Used category only exists when the keymap
    /// uses keys).</summary>
    private static KeycodeCategoryChoice? CategoryChoice(
        KeycodePickerViewModel picker, KeyRingKeyGroup group) => group switch
    {
        KeyRingKeyGroup.Used => picker.Categories.FirstOrDefault(choice => choice.IsUsed),
        KeyRingKeyGroup.More => picker.Categories.FirstOrDefault(choice => !choice.IsUsed && choice.Category is null),
        _ => picker.Categories.FirstOrDefault(choice =>
            choice.Category is { } category && GroupOf(category) == group),
    };

    /// <summary>Whether <paramref name="name"/> names a keycode group exactly
    /// as the segment id was built.</summary>
    private static bool TryKeyGroup(string name, out KeyRingKeyGroup group)
    {
        group = default;
        return Enum.TryParse(name, out group) && group.ToString() == name;
    }

    private static bool Same(KeyBinding one, KeyBinding other) =>
        string.Equals(one.Behavior, other.Behavior, StringComparison.Ordinal)
        && one.Params.SequenceEqual(other.Params);

    /// <summary>The category a choice belongs to; Custom counts as the
    /// keymap's own.</summary>
    private static ZmkBehaviorGroup CategoryOf(KeyBehaviorChoice choice) =>
        choice.Behavior?.Group ?? ZmkBehaviorGroup.Keymap;

    private bool IsSelected(KeyBehaviorChoice choice) =>
        string.Equals(choice.Code, Dialog.SelectedBehavior.Code, StringComparison.Ordinal)
        && choice.IsCustom == Dialog.SelectedBehavior.IsCustom;

    /// <summary>The behaviors of one category, in band order, taken from the
    /// dialog's own list so the ring picks the very instances the dialog
    /// expects.</summary>
    private IReadOnlyList<KeyBehaviorChoice> BehaviorsIn(ZmkBehaviorGroup group)
    {
        if (group == ZmkBehaviorGroup.Keymap)
        {
            return Dialog.AllOtherBehaviors
                .Where(choice => choice.IsCustom
                    || choice.Behavior?.Group == ZmkBehaviorGroup.Keymap)
                .ToArray();
        }

        if (!BandCodes.TryGetValue(group, out var codes)) return [];

        var found = new List<KeyBehaviorChoice>(codes.Length);
        foreach (var code in codes)
        {
            var choice = FindBehavior(code);
            if (choice is not null) found.Add(choice);
        }

        return found;
    }

    /// <summary>The behavior list entry for a code (<see cref="CustomCode"/>
    /// for Custom), or null when the dialog does not offer it.</summary>
    private KeyBehaviorChoice? FindBehavior(string code)
    {
        if (code == CustomCode) return Dialog.AllOtherBehaviors.FirstOrDefault(c => c.IsCustom);

        return Dialog.AllOtherBehaviors.FirstOrDefault(
            c => !c.IsCustom && string.Equals(c.Code, code, StringComparison.Ordinal));
    }

    private KeyRingSegment BehaviorSegment(KeyBehaviorChoice choice, bool isCurrent)
    {
        var loc = Loc.Instance;

        if (choice.IsCustom)
        {
            return new KeyRingSegment(
                CustomId, loc["Ring_Custom_Name"], loc["Ring_Short_Custom"],
                LabelIsCode: false, SubIsCode: false, Icon: null, IsCurrent: isCurrent);
        }

        // A built-in has one description per code; the keymap's own behaviors
        // (no stem) share one.
        var sub = KeyBehaviorChoices.StemOf(choice.Code) is { } stem
            ? loc[$"Ring_Short_{stem}"]
            : loc["Ring_Short_Own"];

        return new KeyRingSegment(
            BehaviorPrefix + choice.Code, choice.Code, sub,
            LabelIsCode: true, SubIsCode: false, Icon: null, IsCurrent: isCurrent);
    }

    private string CategorySub(ZmkBehaviorGroup group)
    {
        if (group == ZmkBehaviorGroup.Keymap) return Loc.Instance["Ring_Category_KeymapSub"];

        var behaviors = BehaviorsIn(group);
        if (behaviors.Count == 0) return "";

        var codes = string.Join(" ", behaviors.Take(2).Select(behavior => behavior.Code));
        return behaviors.Count > 2 ? codes + " …" : codes;
    }

    /// <summary>Whether <paramref name="segmentId"/> names a category, as the
    /// segment id was built: an exact enum name, no numbers, no other case.</summary>
    private static bool TryCategory(string segmentId, out ZmkBehaviorGroup group)
    {
        group = default;
        if (!segmentId.StartsWith(CategoryPrefix, StringComparison.Ordinal)) return false;

        var name = segmentId[CategoryPrefix.Length..];
        return Enum.TryParse(name, out group) && group.ToString() == name;
    }

    private static string IconFor(ZmkBehaviorGroup group) => group switch
    {
        ZmkBehaviorGroup.Key => "fa-solid fa-keyboard",
        ZmkBehaviorGroup.HoldTap => "fa-solid fa-arrows-up-down",
        ZmkBehaviorGroup.Layer => "fa-solid fa-layer-group",
        ZmkBehaviorGroup.Bluetooth => "fa-brands fa-bluetooth-b",
        ZmkBehaviorGroup.Output => "fa-solid fa-right-left",
        ZmkBehaviorGroup.Lighting => "fa-solid fa-lightbulb",
        ZmkBehaviorGroup.Mouse => "fa-solid fa-computer-mouse",
        ZmkBehaviorGroup.System => "fa-solid fa-power-off",
        ZmkBehaviorGroup.Clear => "fa-solid fa-eraser",
        _ => "fa-solid fa-cube",
    };
}
