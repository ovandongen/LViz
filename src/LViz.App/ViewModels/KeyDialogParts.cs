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
    };

    private static readonly string[] QuickCodes = ["&kp", "&trans", "&none"];
    private static readonly string[] OthersBeforeOwn = ["&mt", "&lt"];
    private static readonly string[] OthersAfterOwn = ["&to", "&mo", "&tog", "&sl", "&sk"];

    /// <summary>The quick buttons: &amp;kp, &amp;trans, &amp;none.</summary>
    public static IReadOnlyList<KeyBehaviorChoice> Quick(KeyboardConfig? config) =>
        QuickCodes.Select(BuiltIn).ToArray();

    /// <summary>
    /// Everything but the quick buttons: &amp;mt, &amp;lt, the open keymap's
    /// own hold-taps and macros (code as name, "Defined in this keymap." as
    /// help), then &amp;to, &amp;mo, &amp;tog, &amp;sl, &amp;sk, and the
    /// free-text "Custom binding" last.
    /// </summary>
    public static IReadOnlyList<KeyBehaviorChoice> Others(KeyboardConfig? config)
    {
        var choices = new List<KeyBehaviorChoice>(OthersBeforeOwn.Select(BuiltIn));

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
        _ => Loc.Instance.Format("KeyDialog_ParamFormat", 1),
    };
}
