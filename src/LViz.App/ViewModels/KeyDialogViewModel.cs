using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;

namespace LViz.App.ViewModels;

/// <summary>
/// The key dialog: opens on one key's binding, lets the user pick a behavior
/// and then each parameter on its own tab (Behavior › parameter 1 ›
/// parameter 2, moved with <see cref="NextTab"/>/<see cref="PreviousTab"/>),
/// and produces the new binding in <see cref="Result"/> — null while
/// something is missing. Cancel drops everything; a key opened and confirmed
/// unchanged gives back exactly its old binding. A named-command behavior
/// (&amp;bt, &amp;out, …) edits its command on one tab taking the binding's
/// whole param list (a command name plus an optional profile number).
/// Anything the editors cannot show (an unknown behavior, a param-count
/// mismatch, a seed the editors reject) opens as Custom free text.
/// </summary>
public sealed partial class KeyDialogViewModel : ObservableObject
{
    /// <summary>Scratch behavior used to re-nest one logical param group
    /// through <see cref="ZmkBindingFormatter"/> (see <see cref="Renest"/>).</summary>
    private const string RawPrefix = "&x ";

    /// <summary>Behaviors whose first parameter is a layer reference — the
    /// set of <see cref="ZmkBindingFormatter"/> (internal there, restated
    /// here on purpose).</summary>
    private static readonly HashSet<string> LayerBehaviors = new(StringComparer.Ordinal)
    {
        "&mo", "&to", "&tog", "&sl", "&lt",
    };

    private readonly KeyboardConfig _config;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _defines;
    private readonly IReadOnlyDictionary<string, ZmkMacro> _macros;

    public KeyDialogViewModel(KeyDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _config = request.Config;
        _defines = request.Defines;

        if (request.LayerIndex < 0 || request.LayerIndex >= _config.Layers.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), request.LayerIndex, "Layer index is outside the keymap.");
        }

        var bindings = _config.Layers[request.LayerIndex].Bindings;
        if (request.KeyIndex < 0 || request.KeyIndex >= bindings.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), request.KeyIndex, "Key index is outside the layer.");
        }

        Usage = ZmkKeycodeUsage.Count(_config);
        QuickBehaviors = KeyBehaviorChoices.Quick(_config);
        AllOtherBehaviors = KeyBehaviorChoices.Others(_config);
        _macros = _config.Macros
            .GroupBy(macro => macro.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        TitleText = Loc.Instance.Format(
            "KeyDialog_TitleFormat", request.KeyIndex, _config.Layers[request.LayerIndex].Name);

        Seed(bindings[request.KeyIndex]);
    }

    // ─── Lists and selection ──────────────────────────────────────────────

    /// <summary>The keymap's keycode usage, computed once and shared by every
    /// keycode editor this dialog builds.</summary>
    public ZmkKeycodeUsage Usage { get; }

    /// <summary>The quick buttons (&amp;kp, &amp;trans, &amp;none).</summary>
    public IReadOnlyList<KeyBehaviorChoice> QuickBehaviors { get; }

    /// <summary>Everything but the quick buttons, Custom last.</summary>
    public IReadOnlyList<KeyBehaviorChoice> AllOtherBehaviors { get; }

    /// <summary><see cref="AllOtherBehaviors"/> as the filter leaves them.</summary>
    public IReadOnlyList<KeyBehaviorChoice> OtherBehaviors =>
        AllOtherBehaviors.Where(choice => KeyBehaviorChoices.Matches(choice, BehaviorFilter)).ToArray();

    /// <summary>The one editor per parameter of the selected behavior; empty
    /// for Custom and for parameterless behaviors.</summary>
    public IReadOnlyList<KeyParamViewModel> Params { get; private set; } = [];

    /// <summary>True while the free-text entry is selected.</summary>
    public bool IsCustom => SelectedBehavior.IsCustom;

    /// <summary>The selected behavior seen from the "others" list: it on the
    /// list, else null. Setting it picks the behavior; null is ignored — a
    /// list refresh or a quick pick must not clear the behavior.</summary>
    public KeyBehaviorChoice? SelectedOtherBehavior
    {
        get => OtherBehaviors.Contains(SelectedBehavior) ? SelectedBehavior : null;
        set
        {
            if (value is not null) SelectedBehavior = value;
        }
    }

    [ObservableProperty]
    private string _behaviorFilter = "";

    partial void OnBehaviorFilterChanged(string value)
    {
        OnPropertyChanged(nameof(OtherBehaviors));
        OnPropertyChanged(nameof(SelectedOtherBehavior));
        RefreshDerived();
    }

    [ObservableProperty]
    private string _customText = "";

    partial void OnCustomTextChanged(string value) => RefreshDerived();

    // Hand-written, not [ObservableProperty]: the field must stay non-null
    // (a null set is ignored) and switching rebuilds everything around it —
    // hooks on a generated setter see the value already stored, too late to
    // read the old state they carry over.
    private KeyBehaviorChoice _selectedBehavior = null!;

    /// <summary>The picked behavior; never null.</summary>
    public KeyBehaviorChoice SelectedBehavior
    {
        get => _selectedBehavior;
        set
        {
            if (value is null || value == _selectedBehavior) return;

            // State the switch carries over has to be read before the switch.
            var previousParams = Params;
            var previousPreview = PreviewText;

            if (!SetProperty(ref _selectedBehavior, value)) return;

            OnPropertyChanged(nameof(IsCustom));
            OnPropertyChanged(nameof(SelectedOtherBehavior));
            SwitchBehavior(value, previousParams, previousPreview);
        }
    }

    /// <summary>Rebuilds the editors for the new behavior, carries over what
    /// still fits, and lands on the first parameter tab without a value (the
    /// last one when all have values, tab 0 without params).</summary>
    private void SwitchBehavior(
        KeyBehaviorChoice next, IReadOnlyList<KeyParamViewModel> previousParams, string previousPreview)
    {
        var oldKeycode = previousParams
            .LastOrDefault(param => param.Kind == ZmkBehaviorParamKind.Keycode)?.Values;
        var oldLayer = previousParams
            .LastOrDefault(param => param.Kind == ZmkBehaviorParamKind.Layer)?.Values;

        SetParams(BuildParams(next.Behavior));

        if (next.IsCustom)
        {
            if (previousPreview.Length > 0) CustomText = previousPreview;
        }
        else
        {
            int keycodeIndex = -1;
            int layerIndex = -1;
            for (int i = 0; i < Params.Count; i++)
            {
                if (Params[i].Kind == ZmkBehaviorParamKind.Keycode) keycodeIndex = i;
                if (Params[i].Kind == ZmkBehaviorParamKind.Layer) layerIndex = i;
            }

            if (oldKeycode is not null && keycodeIndex >= 0)
                ((KeycodeParamViewModel)Params[keycodeIndex]).Picker.Seed(oldKeycode);
            if (oldLayer is not null && layerIndex >= 0)
                ((LayerParamViewModel)Params[layerIndex]).Seed(oldLayer);
        }

        int firstMissing = -1;
        for (int i = 0; i < Params.Count; i++)
        {
            if (!Params[i].HasValue)
            {
                firstMissing = i + 1;
                break;
            }
        }

        SelectedTabIndex = Params.Count == 0 ? 0
            : firstMissing >= 0 ? firstMissing
            : Params.Count;

        RefreshDerived();
    }

    private IReadOnlyList<KeyParamViewModel> BuildParams(ZmkBehavior? behavior)
    {
        if (behavior is null) return [];

        var labels = KeyParamLabels.For(behavior, _config);
        var editors = new List<KeyParamViewModel>(behavior.Params.Count);
        for (int i = 0; i < behavior.Params.Count; i++)
        {
            var kind = behavior.Params[i];
            // KeyParamLabels covers every arity the catalog produces; the
            // numbered fallback only catches a catalog/label drift.
            var label = i < labels.Count ? labels[i] : Loc.Instance.Format("KeyDialog_ParamFormat", i + 1);
            KeyParamViewModel editor = kind switch
            {
                ZmkBehaviorParamKind.Keycode or ZmkBehaviorParamKind.Modifier =>
                    new KeycodeParamViewModel(label, kind, Usage),
                ZmkBehaviorParamKind.Layer => new LayerParamViewModel(label, _config),
                ZmkBehaviorParamKind.Command => new CommandParamViewModel(label, behavior.Code, _config),
                _ => new RawParamViewModel(label),
            };
            editor.Changed += OnEditorChanged;
            editors.Add(editor);
        }

        return editors;
    }

    private void OnEditorChanged(object? sender, EventArgs e) => RefreshDerived();

    // ─── Tabs ─────────────────────────────────────────────────────────────

    /// <summary>The tabs: the behavior tab, then one per parameter. The list
    /// is replaced only when <see cref="Params"/> is; edits update each
    /// <see cref="KeyDialogTab.Header"/> in place, because a replaced list
    /// makes the view's tab strip drop its selection.</summary>
    public IReadOnlyList<KeyDialogTab> Tabs { get; private set; } = [];

    /// <summary>Sets <see cref="Params"/> and a matching fresh <see cref="Tabs"/> list.</summary>
    private void SetParams(IReadOnlyList<KeyParamViewModel> editors)
    {
        Params = editors;
        Tabs = Enumerable.Range(0, editors.Count + 1).Select(_ => new KeyDialogTab()).ToArray();
        OnPropertyChanged(nameof(Params));
        OnPropertyChanged(nameof(Tabs));
        UpdateTabHeaders();
    }

    /// <summary>Tab captions: "Behavior: code", then per parameter
    /// "label: value" once it has one, the bare label while it has none.</summary>
    private void UpdateTabHeaders()
    {
        var loc = Loc.Instance;
        var code = IsCustom ? SelectedBehavior.Name : SelectedBehavior.Code;
        Tabs[0].Header = loc.Format("KeyDialog_TabValueFormat", loc["KeyDialog_TabBehavior"], code);
        for (int i = 0; i < Params.Count; i++)
        {
            var param = Params[i];
            Tabs[i + 1].Header = param.ValueText.Length > 0
                ? loc.Format("KeyDialog_TabValueFormat", param.Label, param.ValueText)
                : param.Label;
        }
    }

    // Hand-written, not [ObservableProperty]: the value is clamped *before*
    // it is stored (an On…Changing hook sees it already stored).
    private int _selectedTabIndex;

    /// <summary>0 = the behavior tab, 1…<see cref="Params.Count"/> = the
    /// parameter tabs. Values past the last tab are clamped; a negative
    /// value (the tab strip without a selection) is ignored.</summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (value < 0) return;
            if (SetProperty(ref _selectedTabIndex, Math.Min(value, Params.Count)))
                OnPropertyChanged(nameof(CurrentParam));
        }
    }

    /// <summary>The editor of the current tab; null on the behavior tab.</summary>
    public KeyParamViewModel? CurrentParam =>
        SelectedTabIndex > 0 && SelectedTabIndex <= Params.Count
            ? Params[SelectedTabIndex - 1]
            : null;

    /// <summary>One tab forward; stops at the last parameter tab.</summary>
    public void NextTab() => SelectedTabIndex = SelectedTabIndex + 1;

    /// <summary>One tab back; stops at the behavior tab.</summary>
    public void PreviousTab() => SelectedTabIndex = Math.Max(0, SelectedTabIndex - 1);

    // ─── Result and preview ───────────────────────────────────────────────

    /// <summary>The binding the dialog would write, or null while something
    /// is missing or typed text does not parse.</summary>
    public KeyBinding? Result
    {
        get
        {
            if (SelectedBehavior.IsCustom)
                return ZmkBindingText.TryParse(CustomText, _defines, _config);

            foreach (var param in Params)
            {
                if (param.Values is null) return null;
            }

            var binding = new KeyBinding(
                SelectedBehavior.Code, Params.SelectMany(param => param.Values!).ToList());
            if (!Params.Any(param => param.Kind == ZmkBehaviorParamKind.Raw)) return binding;

            // Raw tokens go through the parser too: it validates them and
            // flattens typed modifier nests like the other editors' picks.
            return ZmkBindingText.TryParse(
                ZmkBindingFormatter.Format(binding, _defines), _defines, _config);
        }
    }

    /// <summary>Whether <see cref="Result"/> holds a binding to write.</summary>
    public bool CanConfirm => Result is not null;

    /// <summary>The invalid-text hint: free text (or a Raw parameter) that is
    /// non-blank but does not parse; empty otherwise.</summary>
    public string ErrorText
    {
        get
        {
            if (Result is not null) return "";

            if (SelectedBehavior.IsCustom)
            {
                return CustomText.Trim().Length > 0 ? Loc.Instance["KeyDialog_CustomInvalid"] : "";
            }

            return Params.Any(param => param.Kind == ZmkBehaviorParamKind.Raw && param.ValueText.Length > 0)
                ? Loc.Instance["KeyDialog_CustomInvalid"]
                : "";
        }
    }

    /// <summary>The binding as keymap text (the file's layer defines come
    /// back through the formatter); empty without a result.</summary>
    public string PreviewText =>
        Result is { } result ? ZmkBindingFormatter.Format(result, _defines) : "";

    /// <summary>The main label the key would show.</summary>
    public string PreviewLabel => PreviewParts().Label;

    /// <summary>The subscript the key would show.</summary>
    public string PreviewSubscript => PreviewParts().Subscript;

    /// <summary>The dialog caption: which key on which layer.</summary>
    public string TitleText { get; }

    private (string Label, string Subscript) PreviewParts()
    {
        if (Result is not { } result) return ("", "");

        var (label, subscript, _) = KeyLabelFormatter.FormatBinding(
            result, TargetLayerName(result), HoldTapFor(result), _macros);
        return (label, subscript);
    }

    /// <summary>The layer a layer behavior points at, by name — the
    /// parameter has to be a number addressing an existing layer.</summary>
    private string? TargetLayerName(KeyBinding binding)
    {
        if (!LayerBehaviors.Contains(binding.Behavior) || binding.Params.Count == 0) return null;
        if (!int.TryParse(binding.Params[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
            return null;
        if (index < 0 || index >= _config.Layers.Count) return null;
        return _config.Layers[index].Name;
    }

    private HoldTap? HoldTapFor(KeyBinding binding) =>
        _config.HoldTaps.FirstOrDefault(tap => tap.Name == binding.Behavior);

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(PreviewLabel));
        OnPropertyChanged(nameof(PreviewSubscript));
        UpdateTabHeaders();
        OnPropertyChanged(nameof(CurrentParam));
    }

    // ─── Seeding ──────────────────────────────────────────────────────────

    /// <summary>
    /// Opens on the original binding: its behavior by code among Quick +
    /// AllOther, its logical params seeded into the editors. A behavior
    /// whose only parameter is a command skips the param-count check — that
    /// one editor is seeded with all groups (a command name plus an
    /// optional profile number, see <see cref="CommandParamViewModel.Seed"/>).
    /// Anything the editors cannot show — an unknown behavior, a
    /// param-count mismatch, a seed that returns false — opens as Custom
    /// text instead. Afterwards <see cref="Result"/> equals the original
    /// binding.
    /// </summary>
    private void Seed(KeyBinding binding)
    {
        var choice = QuickBehaviors.Concat(AllOtherBehaviors)
            .FirstOrDefault(c => !c.IsCustom
                && string.Equals(c.Code, binding.Behavior, StringComparison.Ordinal));

        if (choice?.Behavior is not { } behavior)
        {
            OpenAsCustom(binding);
            return;
        }

        var groups = ZmkModifierWrap.SplitLogical(binding.Params);
        // A single command parameter covers the whole param list, so the
        // binding carries the command's groups (1, or 2 with a profile
        // number) rather than one group per parameter.
        if (behavior.Params is not [ZmkBehaviorParamKind.Command]
            && groups.Count != behavior.Params.Count)
        {
            OpenAsCustom(binding);
            return;
        }

        // The ctor is the one "seed" behavior change: the field goes in
        // directly, so the user-side switch logic (carry-over, tab lands)
        // does not run and the start tab is simply the last parameter.
        _selectedBehavior = choice;
        SetParams(BuildParams(behavior));

        for (int i = 0; i < Params.Count; i++)
        {
            bool seeded = Params[i] switch
            {
                KeycodeParamViewModel keycode => keycode.Picker.Seed(groups[i]),
                LayerParamViewModel layer => layer.Seed(groups[i]),
                RawParamViewModel raw => SeedRaw(raw, groups[i]),
                CommandParamViewModel command => command.Seed(groups),
                _ => false,
            };

            if (!seeded)
            {
                OpenAsCustom(binding);
                return;
            }
        }

        SelectedTabIndex = Params.Count > 0 ? Params.Count : 0;
        RefreshDerived();
    }

    private static bool SeedRaw(RawParamViewModel editor, IReadOnlyList<string> group)
    {
        editor.Text = Renest(group);
        return true;
    }

    /// <summary>One logical param group back to the text the user typed it
    /// as: re-nested through the formatter (<c>["LS","A"]</c> →
    /// <c>LS(A)</c>) with the scratch behavior stripped.</summary>
    private static string Renest(IReadOnlyList<string> group)
    {
        var text = ZmkBindingFormatter.Format(new KeyBinding("&x", group), []);
        return text.StartsWith(RawPrefix, StringComparison.Ordinal) ? text[RawPrefix.Length..] : text;
    }

    private void OpenAsCustom(KeyBinding binding)
    {
        _selectedBehavior = AllOtherBehaviors.First(choice => choice.IsCustom);
        SetParams([]);
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(SelectedOtherBehavior));
        SelectedTabIndex = 0;
        CustomText = ZmkBindingFormatter.Format(binding, _defines);
        RefreshDerived();
    }
}

/// <summary>One tab of the key dialog's tab strip; its caption follows the
/// parameter's value (see <see cref="KeyDialogViewModel.Tabs"/>).</summary>
public sealed partial class KeyDialogTab : ObservableObject
{
    [ObservableProperty]
    private string _header = "";
}
