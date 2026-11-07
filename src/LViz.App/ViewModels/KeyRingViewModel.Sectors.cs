using System.Globalization;
using LViz.App.Localization;
using LViz.Core.Keymap.Catalog;
using LViz.Core.Models;

namespace LViz.App.ViewModels;

// The sectors the ring draws: the main ring, the inner modifier ring, the
// outer band of one category, and the catalogs of behaviors and keycode groups
// they are built from.
public sealed partial class KeyRingViewModel
{
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
                            new KeyRingTarget.Layer(choice.Index),
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
                            new KeyRingTarget.Command(choice),
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
                    new KeyRingTarget.Modifier(entry.Flag),
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
                new KeyRingTarget.Category(group),
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
                new KeyRingTarget.KeyGroup(group),
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
    public IReadOnlyList<KeyRingSegment> BandFor(KeyRingTarget target)
    {
        switch (target)
        {
            case KeyRingTarget.Category category:
                var behaviors = BehaviorsIn(category.Group);
                if (behaviors.Count < 2 || behaviors.Count > MaxBandItems) return [];

                return behaviors
                    .Select(behavior => BehaviorSegment(behavior, IsSelected(behavior)))
                    .ToArray();

            case KeyRingTarget.Command { Choice.Command.TakesNumber: true } command
                when CommandParam is { } param && param.Commands.Contains(command.Choice):
                var choice = command.Choice;
                return
                [
                    .. param.Profiles.Select(profile => new KeyRingSegment(
                        new KeyRingTarget.Profile(choice, profile),
                        profile.Text,
                        Loc.Instance["Ring_Profile_Sub"],
                        LabelIsCode: true, SubIsCode: false, Icon: null,
                        IsCurrent: ReferenceEquals(param.SelectedCommand, choice)
                            && ReferenceEquals(profile, param.SelectedProfile))),
                ];

            default:
                return [];
        }
    }

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

    /// <summary>The behavior list entry for a code, or null when the dialog
    /// does not offer it.</summary>
    private KeyBehaviorChoice? FindBehavior(string code) =>
        Dialog.AllOtherBehaviors.FirstOrDefault(
            c => !c.IsCustom && string.Equals(c.Code, code, StringComparison.Ordinal));

    private KeyRingSegment BehaviorSegment(KeyBehaviorChoice choice, bool isCurrent)
    {
        var loc = Loc.Instance;

        var target = new KeyRingTarget.Behavior(choice);

        if (choice.IsCustom)
        {
            return new KeyRingSegment(
                target, loc["Ring_Custom_Name"], loc["Ring_Short_Custom"],
                LabelIsCode: false, SubIsCode: false, Icon: null, IsCurrent: isCurrent);
        }

        // A built-in has one description per code; the keymap's own behaviors
        // (no stem) share one.
        var sub = KeyBehaviorChoices.StemOf(choice.Code) is { } stem
            ? loc[$"Ring_Short_{stem}"]
            : loc["Ring_Short_Own"];

        return new KeyRingSegment(
            target, choice.Code, sub,
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
