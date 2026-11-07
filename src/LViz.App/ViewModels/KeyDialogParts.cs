using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;
using LViz.Core.Keymap.Catalog;
using LViz.Core.Models;

namespace LViz.App.ViewModels;

/// <summary>
/// One entry of the key dialog's behavior list: the <see cref="Code"/> it
/// writes (empty for "Custom binding"), the translated <see cref="Name"/> and
/// <see cref="Help"/>, and the catalog behavior whose parameters it edits
/// (null exactly for the free-text Custom entry, see <see cref="IsCustom"/>).
/// </summary>
public sealed record KeyBehaviorChoice(
    string Code, string Name, string Help, ZmkBehavior? Behavior)
{
    /// <summary>True for the free-text "Custom binding" entry.</summary>
    public bool IsCustom => Behavior is null;
}

/// <summary>
/// The key dialog's behavior list in its fixed order: the quick buttons
/// (<see cref="Quick"/>), then everything else (<see cref="Others"/>) —
/// mod-tap, layer-tap, the keymap's own behaviors, the plain layer
/// behaviors, and "Custom binding" last. Friendly names and explanations are
/// resx UI text; behaviors defined in the open keymap show their code.
/// </summary>
public static class KeyBehaviorChoices
{
    /// <summary>Behavior code → resx stem (Behavior_&lt;Stem&gt;_Name/Help).</summary>
    private static readonly Dictionary<string, string> Stems = new(StringComparer.Ordinal)
    {
        ["&kp"] = "Kp",
        ["&trans"] = "Trans",
        ["&none"] = "None",
        ["&mt"] = "Mt",
        ["&lt"] = "Lt",
        ["&mo"] = "Mo",
        ["&to"] = "To",
        ["&tog"] = "Tog",
        ["&sl"] = "Sl",
        ["&sk"] = "Sk",
        ["&bt"] = "Bt",
        ["&out"] = "Out",
        ["&rgb_ug"] = "RgbUg",
        ["&mkp"] = "Mkp",
        ["&msc"] = "Msc",
        ["&mmv"] = "Mmv",
        ["&caps_word"] = "CapsWord",
        ["&key_repeat"] = "KeyRepeat",
        ["&bootloader"] = "Bootloader",
        ["&sys_reset"] = "SysReset",
        ["&soft_off"] = "SoftOff",
        ["&studio_unlock"] = "StudioUnlock",
    };

    private static readonly string[] QuickCodes = ["&kp", "&trans", "&none"];
    private static readonly string[] OthersBeforeOwn = ["&mt", "&lt"];
    private static readonly string[] OthersAfterOwn =
    [
        "&to", "&mo", "&tog", "&sl", "&sk",
        "&bt", "&out", "&rgb_ug", "&mkp", "&msc", "&mmv",
        "&caps_word", "&key_repeat", "&bootloader", "&sys_reset", "&soft_off", "&studio_unlock",
    ];

    /// <summary>The quick buttons: &amp;kp, &amp;trans, &amp;none. They head
    /// <see cref="Others"/> too, so the behavior filter finds them.</summary>
    public static IReadOnlyList<KeyBehaviorChoice> Quick(KeyboardConfig? config) =>
        QuickCodes.Select(BuiltIn).ToArray();

    /// <summary>
    /// The behavior list: the quick behaviors (&amp;kp, &amp;trans,
    /// &amp;none, also shown as buttons), &amp;mt, &amp;lt, the open keymap's
    /// own hold-taps and macros (code as name, "Defined in this keymap." as
    /// help), then &amp;to, &amp;mo, &amp;tog, &amp;sl, &amp;sk, &amp;bt,
    /// &amp;out, &amp;rgb_ug, &amp;mkp, &amp;msc, &amp;mmv, &amp;caps_word,
    /// &amp;key_repeat, &amp;bootloader, &amp;sys_reset, &amp;soft_off,
    /// &amp;studio_unlock, and the free-text "Custom binding" last.
    /// </summary>
    public static IReadOnlyList<KeyBehaviorChoice> Others(KeyboardConfig? config)
    {
        var choices = new List<KeyBehaviorChoice>(
            QuickCodes.Concat(OthersBeforeOwn).Select(BuiltIn));

        foreach (var behavior in ZmkBehaviorCatalog.For(config))
        {
            if (behavior.Group != ZmkBehaviorGroup.Keymap) continue;
            choices.Add(new KeyBehaviorChoice(
                behavior.Code, behavior.Code, Loc.Instance["Behavior_Keymap_Help"], behavior));
        }

        choices.AddRange(OthersAfterOwn.Select(BuiltIn));
        choices.Add(new KeyBehaviorChoice(
            "",
            Loc.Instance["Behavior_Custom_Name"],
            Loc.Instance["Behavior_Custom_Help"],
            null));
        return choices;
    }

    /// <summary>Whether <paramref name="choice"/> survives the dialog's
    /// behavior filter: code or name containing the trimmed
    /// <paramref name="filter"/>, case-insensitive; blank matches everything.</summary>
    public static bool Matches(KeyBehaviorChoice choice, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        var text = filter.Trim();
        return choice.Code.Contains(text, StringComparison.OrdinalIgnoreCase)
            || choice.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The resx stem (Behavior_&lt;Stem&gt;_Name/Help) for a built-in
    /// behavior code, or null when the code is not a built-in one.</summary>
    public static string? StemOf(string code) => Stems.GetValueOrDefault(code);

    private static KeyBehaviorChoice BuiltIn(string code)
    {
        var stem = Stems[code];
        return new KeyBehaviorChoice(
            code,
            Loc.Instance[$"Behavior_{stem}_Name"],
            Loc.Instance[$"Behavior_{stem}_Help"],
            ZmkBehaviorCatalog.Find(null, code));
    }
}

/// <summary>
/// Base of the key dialog's parameter editors: one per binding parameter,
/// showing a translated <see cref="Label"/>, holding a value of its
/// <see cref="Kind"/>, and producing it as flat binding params
/// (<see cref="Values"/>, null while the user has not picked one).
/// Subclasses raise <see cref="Changed"/> on every user edit.
/// </summary>
public abstract partial class KeyParamViewModel : ObservableObject
{
    protected KeyParamViewModel(string label, ZmkBehaviorParamKind kind)
    {
        Label = label;
        Kind = kind;
    }

    /// <summary>The translated caption above the editor.</summary>
    public string Label { get; }

    /// <summary>Which editor shape this parameter has.</summary>
    public ZmkBehaviorParamKind Kind { get; }

    /// <summary>The parameter as flat binding params, or null while no
    /// value has been picked yet.</summary>
    public abstract IReadOnlyList<string>? Values { get; }

    /// <summary>Whether <see cref="Values"/> holds a value.</summary>
    public bool HasValue => Values is not null;

    /// <summary>What the user sees as the current value; empty when none.</summary>
    public abstract string ValueText { get; }

    /// <summary>Raised once per user edit that may change the value.</summary>
    public event EventHandler? Changed;

    /// <summary>Notifies the value properties and raises
    /// <see cref="Changed"/>; called by the subclasses on every edit.</summary>
    protected void RaiseChanged()
    {
        OnPropertyChanged(nameof(Values));
        OnPropertyChanged(nameof(HasValue));
        OnPropertyChanged(nameof(ValueText));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// The keycode/modifier parameter editor: hosts a
/// <see cref="KeycodePickerViewModel"/> (modifier-first categories for a
/// <see cref="ZmkBehaviorParamKind.Modifier"/> parameter) and hands on its
/// flat value and change notifications.
/// </summary>
public sealed class KeycodeParamViewModel : KeyParamViewModel
{
    public KeycodeParamViewModel(string label, ZmkBehaviorParamKind kind, ZmkKeycodeUsage usage)
        : base(label, kind)
    {
        Picker = new KeycodePickerViewModel(usage, modifiersFirst: kind == ZmkBehaviorParamKind.Modifier);
        Picker.Changed += (_, _) => RaiseChanged();
    }

    /// <summary>The hosted picker; the view binds to it directly.</summary>
    public KeycodePickerViewModel Picker { get; }

    public override IReadOnlyList<string>? Values => Picker.Value;

    public override string ValueText => Picker.ValueText;
}

/// <summary>One entry of a layer parameter's dropdown: the layer
/// <see cref="Index"/> (the value written) and its "index: name"
/// <see cref="Text"/>.</summary>
public sealed record LayerChoice(int Index, string Text);

/// <summary>
/// The layer parameter editor: the open keymap's layers as "index: name",
/// writing the selected layer's index as the flat value.
/// </summary>
public sealed partial class LayerParamViewModel : KeyParamViewModel
{
    public LayerParamViewModel(string label, KeyboardConfig config)
        : base(label, ZmkBehaviorParamKind.Layer)
    {
        Layers = config.Layers
            .Select((layer, index) => new LayerChoice(index, $"{index}: {layer.Name}"))
            .ToArray();
    }

    /// <summary>The dropdown: one entry per layer, in layer order.</summary>
    public IReadOnlyList<LayerChoice> Layers { get; }

    [ObservableProperty]
    private LayerChoice? _selectedLayer;

    public override IReadOnlyList<string>? Values =>
        SelectedLayer is { } layer
            ? [layer.Index.ToString(CultureInfo.InvariantCulture)]
            : null;

    public override string ValueText => SelectedLayer?.Text ?? "";

    partial void OnSelectedLayerChanged(LayerChoice? value) => RaiseChanged();

    /// <summary>Opens the dropdown on one existing logical param: a single
    /// token naming an in-range layer index. Returns whether it took.</summary>
    public bool Seed(IReadOnlyList<string> group)
    {
        if (group.Count != 1) return false;
        if (!int.TryParse(group[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
            return false;

        var choice = Layers.FirstOrDefault(layer => layer.Index == index);
        if (choice is null) return false;

        SelectedLayer = choice;
        return true;
    }
}

/// <summary>
/// The free-text parameter editor: one whitespace-free token (a raw keycode,
/// a define name, a macro parameter — see
/// <c>LViz.Core.Keymap.Parser.ZmkBindingText</c> for validating it).
/// </summary>
public sealed partial class RawParamViewModel : KeyParamViewModel
{
    [ObservableProperty]
    private string _text = "";

    public RawParamViewModel(string label)
        : base(label, ZmkBehaviorParamKind.Raw)
    {
    }

    public override IReadOnlyList<string>? Values
    {
        get
        {
            var trimmed = Text.Trim();
            return trimmed.Length > 0 && !trimmed.Any(char.IsWhiteSpace)
                ? [trimmed]
                : null;
        }
    }

    public override string ValueText => Text.Trim();

    partial void OnTextChanged(string value) => RaiseChanged();
}

/// <summary>One entry of a command parameter's list: the catalog
/// <see cref="Command"/>, its translated <see cref="Name"/> and
/// <see cref="Help"/>.</summary>
public sealed record CommandChoice(ZmkCommand Command, string Name, string Help);

/// <summary>One profile-number button: the number written and its caption.</summary>
public sealed record ProfileChoice(int Number, string Text);

/// <summary>
/// The command parameter editor: the behavior's named commands
/// (<see cref="ZmkCommandCatalog.For"/> order, translated; extras from the
/// open keymap show their ZMK name), and for a command that takes one
/// (<see cref="ZmkCommand.TakesNumber"/>) a profile number picked from
/// <see cref="Profiles"/> on the same tab. The flat value is the command
/// name, plus the number when the command takes one and a profile is picked.
/// </summary>
public sealed partial class CommandParamViewModel : KeyParamViewModel
{
    public CommandParamViewModel(string label, string behaviorCode, KeyboardConfig? config)
        : base(label, ZmkBehaviorParamKind.Command)
    {
        Commands = ZmkCommandCatalog.For(behaviorCode, config)
            .Select(command => new CommandChoice(
                command,
                command.FromKeymap
                    ? command.Name
                    : Loc.Instance[$"Command_{command.Name}_Name"],
                command.FromKeymap
                    ? Loc.Instance["Command_FromKeymap_Help"]
                    : Loc.Instance[$"Command_{command.Name}_Help"]))
            .ToArray();
        Profiles = ZmkCommandCatalog.ProfileNumbers
            .Select(number => Profile(number))
            .ToArray();
        ProfileLabel = Loc.Instance["KeyDialog_ParamProfile"];
    }

    /// <summary>The command list: bundled commands, then the keymap's extras.</summary>
    public IReadOnlyList<CommandChoice> Commands { get; }

    [ObservableProperty]
    private CommandChoice? _selectedCommand;

    /// <summary>The profile-number buttons; <see cref="ZmkCommandCatalog.ProfileNumbers"/>,
    /// plus a higher number <see cref="Seed"/> found in the binding.</summary>
    public IReadOnlyList<ProfileChoice> Profiles { get; private set; }

    [ObservableProperty]
    private ProfileChoice? _selectedProfile;

    /// <summary>Whether the picked command is followed by a profile number.</summary>
    public bool NeedsProfile => SelectedCommand?.Command.TakesNumber == true;

    /// <summary>The caption above the profile buttons.</summary>
    public string ProfileLabel { get; }

    public override IReadOnlyList<string>? Values
    {
        get
        {
            if (SelectedCommand is not { } choice) return null;
            if (!choice.Command.TakesNumber) return [choice.Command.Name];
            return SelectedProfile is { } profile
                ? [choice.Command.Name, profile.Number.ToString(CultureInfo.InvariantCulture)]
                : null;
        }
    }

    public override string ValueText
    {
        get
        {
            if (SelectedCommand is not { } choice) return "";
            if (choice.Command.TakesNumber && SelectedProfile is { } profile)
                return Loc.Instance.Format("KeyDialog_CommandProfileFormat", choice.Name, profile.Number);
            return choice.Name;
        }
    }

    partial void OnSelectedCommandChanged(CommandChoice? value)
    {
        OnPropertyChanged(nameof(NeedsProfile));
        RaiseChanged();
    }

    // The picked profile survives a command change; it only counts while
    // NeedsProfile is true (see Values).
    partial void OnSelectedProfileChanged(ProfileChoice? value) => RaiseChanged();

    /// <summary>Opens the editor on one existing logical param: one token
    /// naming a listed command that takes no number, or a command that takes
    /// one followed by its profile number written the way it is written back,
    /// e.g. <c>0</c>, not <c>00</c> (a number beyond the bundled range is
    /// added to <see cref="Profiles"/>). Returns whether it took.</summary>
    public bool Seed(IReadOnlyList<IReadOnlyList<string>> groups)
    {
        if (groups.Count is not (1 or 2)) return false;
        if (groups.Any(group => group.Count != 1)) return false;

        var choice = Commands.FirstOrDefault(
            c => string.Equals(c.Command.Name, groups[0][0], StringComparison.Ordinal));
        if (choice is null) return false;

        if (groups.Count == 1)
        {
            if (choice.Command.TakesNumber) return false;
            SelectedCommand = choice;
            return true;
        }

        if (!choice.Command.TakesNumber) return false;
        if (!int.TryParse(groups[1][0], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number < 0)
        {
            return false;
        }

        // The number has to be spelled the way this editor writes it, or OK
        // would rewrite "&bt BT_SEL 00" to "&bt BT_SEL 0" and break the
        // dialog's "open unchanged, confirm unchanged" rule. Such a binding
        // opens as custom text instead.
        if (!string.Equals(
                number.ToString(CultureInfo.InvariantCulture), groups[1][0], StringComparison.Ordinal))
        {
            return false;
        }

        SelectedCommand = choice;
        SelectedProfile = ProfileFor(number);
        return true;
    }

    /// <summary>The button for a profile number, adding one when the list
    /// doesn't have it yet (kept sorted).</summary>
    private ProfileChoice ProfileFor(int number)
    {
        var existing = Profiles.FirstOrDefault(profile => profile.Number == number);
        if (existing is not null) return existing;

        Profiles = [.. Profiles.OrderBy(profile => profile.Number).Append(Profile(number))];
        OnPropertyChanged(nameof(Profiles));
        return Profiles.First(profile => profile.Number == number);
    }

    private static ProfileChoice Profile(int number) =>
        new(number, number.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// The translated captions for a behavior's parameters: macro parameters are
/// numbered, hold-tap-like pairs are Hold/Tap, a single parameter is named
/// after its kind.
/// </summary>
public static class KeyParamLabels
{
    /// <summary>One label per parameter of <paramref name="behavior"/>, in
    /// order; empty for a parameterless behavior. A macro (its code among
    /// <c>config.Macros</c>) numbers its parameters instead.</summary>
    public static IReadOnlyList<string> For(ZmkBehavior behavior, KeyboardConfig? config)
    {
        if (config is not null && config.Macros.Any(macro => macro.Name == behavior.Code))
        {
            return Enumerable.Range(1, behavior.Params.Count)
                .Select(n => Loc.Instance.Format("KeyDialog_ParamFormat", n))
                .ToArray();
        }

        return behavior.Params.Count switch
        {
            2 =>
            [
                Loc.Instance["KeyDialog_ParamHold"],
                Loc.Instance["KeyDialog_ParamTap"],
            ],
            1 => [KindLabel(behavior.Params[0])],
            _ => [],
        };
    }

    private static string KindLabel(ZmkBehaviorParamKind kind) => kind switch
    {
        ZmkBehaviorParamKind.Keycode or ZmkBehaviorParamKind.Modifier =>
            Loc.Instance["KeyDialog_ParamKey"],
        ZmkBehaviorParamKind.Layer => Loc.Instance["KeyDialog_ParamLayer"],
        ZmkBehaviorParamKind.Command => Loc.Instance["KeyDialog_ParamCommand"],
        _ => Loc.Instance.Format("KeyDialog_ParamFormat", 1),
    };
}
