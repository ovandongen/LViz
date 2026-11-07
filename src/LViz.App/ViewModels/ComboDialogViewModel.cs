using System.Globalization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Writer;
using LViz.Core.Layout;
using LViz.Core.Models;

namespace LViz.App.ViewModels;

/// <summary>
/// One layer row of the dialog's layer scope: <see cref="IsOn"/> when the
/// combo is active on that layer. Only consulted while
/// <see cref="ComboDialogViewModel.AllLayers"/> is off.
/// </summary>
public partial class ComboLayerToggle : ObservableObject
{
    /// <summary>Layer index in the keymap.</summary>
    public int Index { get; }

    /// <summary>The layer's <c>display-name</c>.</summary>
    public string Name { get; }

    /// <summary>True when the combo fires on this layer.</summary>
    [ObservableProperty]
    private bool _isOn;

    public ComboLayerToggle(int index, string name, bool isOn)
    {
        Index = index;
        Name = name;
        _isOn = isOn;
    }
}

/// <summary>
/// The combo dialog: pick the trigger keys on a copy of the board (a click
/// toggles a cap gold), name the combo, choose what it fires through the key
/// dialog, and set its timeout, layer scope, slow release and prior idle.
/// <see cref="Errors"/> carries what blocks saving, <see cref="Warnings"/> the
/// firmware limits that are only advisory; <see cref="Result()"/> builds the
/// combo the caller writes back.
///
/// <para>Owns a <see cref="BoardSurfaceViewModel"/> like
/// <see cref="ExitKeyPickerViewModel"/>, so <c>BoardView</c> renders it with
/// the same canvas. The caps carry their labels from
/// <see cref="LabelLayer"/> (switchable, starting at the shown layer) and the
/// numbered badges of the <em>other</em> combos, so the keys a combo already
/// uses are visible while picking.</para>
/// </summary>
public partial class ComboDialogViewModel : ObservableObject
{
    private const string DefaultFill = AppTheme.KeyDefaultFillHex;
    private const string SelectedFill = AppTheme.AccentGoldHex;

    /// <summary>Behaviors whose first parameter is a layer reference. Core
    /// keeps its copy of this set <c>internal</c> to its own assembly, so the
    /// App restates it here as it does in
    /// <see cref="KeyDialogViewModel"/>.</summary>
    private static readonly HashSet<string> LayerBehaviors = new(StringComparer.Ordinal)
    {
        "&mo", "&to", "&tog", "&sl", "&lt",
    };

    private readonly ComboDialogRequest _request;
    private readonly KeyboardConfig _config;
    private readonly IReadOnlyDictionary<string, ZmkMacro> _macros;

    /// <summary>Every combo but the edited one (all of them for a new combo)
    /// — what the badges show and what <see cref="ComboRules"/> checks
    /// against. Paired with <see cref="_otherNumbers"/> by position.</summary>
    private readonly List<ZmkCombo> _others = new();

    /// <summary>The 1-based legend number of each entry of
    /// <see cref="_others"/> — its index in the keymap plus one.</summary>
    private readonly List<int> _otherNumbers = new();

    /// <summary>The combo this dialog opened on, null for a new one.</summary>
    private readonly ZmkCombo? _original;

    /// <summary>The picked key indices, in the order they were picked.</summary>
    private readonly List<int> _selected = new();

    private string _nodeName = "";
    private IReadOnlyList<string> _errors = Array.Empty<string>();
    private IReadOnlyList<string> _warnings = Array.Empty<string>();

    public ComboDialogViewModel(ComboDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _config = request.Config;

        if (_config.Layers.Count == 0)
            throw new ArgumentException("The keymap has no layers to label keys with.", nameof(request));

        var comboIndex = request.ComboIndex;
        if (comboIndex is { } check && (check < 0 || check >= _config.Combos.Count))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), check, "Combo index is outside the keymap.");
        }

        _original = comboIndex is { } which ? _config.Combos[which] : null;
        for (var i = 0; i < _config.Combos.Count; i++)
        {
            if (comboIndex == i) continue;
            _others.Add(_config.Combos[i]);
            _otherNumbers.Add(i + 1);
        }

        _macros = _config.Macros
            .GroupBy(macro => macro.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        // Picker-style board: fixed press palette, nothing persisted, legend
        // always on. Labels, fill and combo pills are set by RefreshBoard below.
        var style = new BoardStyleViewModel(
            AppTheme.PressHighlightDefaultHex,
            strokeOverride: AppTheme.PickerPressStrokeHex);
        Board = new BoardSurfaceViewModel(
            request.Profile, new BoardLayoutViewModel(request.Profile), style,
            keys => new ComboLegendViewModel(keys, style, overlayVisible: true, profileId: () => ""),
            pos => new KeyViewModel(pos) { KeyFillColor = DefaultFill })
        {
            OnKeyTapped = ToggleKey,
        };

        var layers = new List<LayerChoice>(_config.Layers.Count);
        for (var i = 0; i < _config.Layers.Count; i++)
            layers.Add(new LayerChoice(i, _config.Layers[i].Name));
        LabelLayers = layers;
        _labelLayer = layers[Math.Clamp(request.ShownLayerIndex, 0, layers.Count - 1)];

        var scope = _original?.Layers;
        var allLayers = scope is null || ZmkCombo.IsAllLayers(scope);
        var toggles = new List<ComboLayerToggle>(_config.Layers.Count);
        for (var i = 0; i < _config.Layers.Count; i++)
        {
            var toggle = new ComboLayerToggle(i, _config.Layers[i].Name,
                !allLayers && scope is not null && scope.Contains(i));
            toggle.PropertyChanged += OnLayerToggleChanged;
            toggles.Add(toggle);
        }
        LayerToggles = toggles;
        _allLayers = allLayers;

        _timeoutText = Milliseconds(_original?.TimeoutMs);
        _idleText = Milliseconds(_original?.RequirePriorIdleMs);
        _slowRelease = _original?.SlowRelease ?? false;

        TitleText = _original is { } original
            ? Loc.Instance.Format("Combo_TitleEditFormat", Identity(original))
            : Loc.Instance["Combo_TitleNew"];

        // The name and the binding: an edited combo keeps what it has, a new
        // one starts unnamed and the auto-name follows its binding until the
        // user types a name.
        if (_original is { } editedCombo)
        {
            _nodeName = Identity(editedCombo);
            _isNameAuto = false;
            foreach (var key in editedCombo.KeyPositions)
                if (key >= 0 && key < Board.Keys.Count && !_selected.Contains(key))
                    _selected.Add(key);
        }
        else
        {
            _isNameAuto = true;
            _nodeName = ComboRules.AutoName(binding: null, TakenNames());
        }

        RefreshBoard();
        SetBinding(_original?.Binding);
        RefreshDerived();
    }

    // ─── Board ────────────────────────────────────────────────────────────

    /// <summary>The board <c>BoardView</c> renders; a tap on a cap toggles it
    /// as a trigger key. Its legend is always on — the picked keys' badges are
    /// the point of showing the board here.</summary>
    public BoardSurfaceViewModel Board { get; }

    // ─── Trigger keys ─────────────────────────────────────────────────────

    /// <summary>The combo's keys, in the order they were picked.</summary>
    public IReadOnlyList<int> SelectedKeys { get; private set; } = Array.Empty<int>();

    /// <summary>The picked keys' labels on <see cref="LabelLayer"/>, joined
    /// with <c> + </c>; a key whose binding shows no label appears as
    /// <c>#&lt;index&gt;</c>. Empty when nothing is picked.</summary>
    public string KeysSummary { get; private set; } = "";

    // ─── Label layer ──────────────────────────────────────────────────────

    /// <summary>One entry per layer of the keymap — what the board can label
    /// its caps with. The shared <see cref="LayerChoice"/> carries the layer
    /// index and, here, its plain <c>display-name</c> as the text.</summary>
    public IReadOnlyList<LayerChoice> LabelLayers { get; }

    /// <summary>Which layer's bindings name the caps. Starts at the layer the
    /// main window shows.</summary>
    [ObservableProperty]
    private LayerChoice _labelLayer;

    partial void OnLabelLayerChanged(LayerChoice value)
    {
        RefreshBoard();
        RefreshDerived();
    }

    // ─── Name ─────────────────────────────────────────────────────────────

    /// <summary>The combo's devicetree node name. A write from the view means
    /// "the user named it" and stops <see cref="IsNameAuto"/>; the view model's
    /// own auto-naming doesn't.</summary>
    public string NodeName
    {
        get => _nodeName;
        set
        {
            IsNameAuto = false;
            if (string.Equals(_nodeName, value, StringComparison.Ordinal)) return;
            _nodeName = value;
            OnPropertyChanged();
            RefreshDerived();
        }
    }

    /// <summary>True while the name still follows the binding.</summary>
    [ObservableProperty]
    private bool _isNameAuto;

    // ─── Binding ──────────────────────────────────────────────────────────

    /// <summary>What the combo fires; null until the user chooses one (and for
    /// a combo answered <c>&amp;none</c>).</summary>
    public KeyBinding? Binding { get; private set; }

    /// <summary><see cref="Binding"/> as keymap text, or the "choose" prompt
    /// while none is set.</summary>
    public string BindingText { get; private set; } = "";

    /// <summary>Opens the key dialog on the combo's binding; answers the
    /// binding, or null when cancelled. Not wired up = the button does
    /// nothing.</summary>
    public Func<KeyDialogRequest, Task<KeyBinding?>>? BindingEditorRequested { get; set; }

    /// <summary>Choose what the combo fires. A <c>&amp;none</c> answer means
    /// "no binding" and clears it; cancelling leaves it as it was.</summary>
    [RelayCommand]
    private async Task EditBindingAsync()
    {
        if (BindingEditorRequested is not { } open) return;

        var answer = await open(new KeyDialogRequest(_config, _request.Defines, LabelLayer.Index, -1)
        {
            LayerDefines = _request.LayerDefines,
            Binding = Binding ?? KeyBinding.None,
            Caption = TitleText,
        });

        if (answer is null) return;
        SetBinding(IsNone(answer) ? null : answer);
        RefreshDerived();
    }

    // ─── Options ──────────────────────────────────────────────────────────

    /// <summary><c>timeout-ms</c> as typed; empty leaves it out of the file.</summary>
    [ObservableProperty]
    private string _timeoutText = "";

    /// <summary><c>require-prior-idle-ms</c> as typed; empty leaves it out.</summary>
    [ObservableProperty]
    private string _idleText = "";

    /// <summary>The <c>slow-release</c> flag.</summary>
    [ObservableProperty]
    private bool _slowRelease;

    /// <summary>True when the combo fires on every layer — the per-layer
    /// <see cref="LayerToggles"/> are then irrelevant.</summary>
    [ObservableProperty]
    private bool _allLayers;

    /// <summary>One row per layer, on for the layers the combo fires on.</summary>
    public IReadOnlyList<ComboLayerToggle> LayerToggles { get; }

    partial void OnTimeoutTextChanged(string value) => RefreshDerived();
    partial void OnIdleTextChanged(string value) => RefreshDerived();
    partial void OnSlowReleaseChanged(bool value) => RefreshDerived();
    partial void OnAllLayersChanged(bool value) => RefreshDerived();

    private void OnLayerToggleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComboLayerToggle.IsOn)) RefreshDerived();
    }

    // ─── Checks and result ────────────────────────────────────────────────

    /// <summary>What blocks saving, as translated messages, in the order the
    /// dialog catches them.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Firmware limits the combo trips, as translated messages.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>True when nothing blocks saving.</summary>
    public bool CanConfirm => _errors.Count == 0;

    /// <summary>The dialog's title: which combo is open (its name at open
    /// time, not as renamed).</summary>
    public string TitleText { get; }

    /// <summary>The combo as the dialog left it, or null while something
    /// blocks saving. An existing combo with a label (its Name differs from
    /// its node name) keeps that label through a rename; any other combo's
    /// Name is its node name, as the parser reads it.</summary>
    public ZmkCombo? Result()
    {
        if (!CanConfirm || Binding is not { } binding) return null;

        var name = _original is { } original
            && !string.Equals(original.Name, Identity(original), StringComparison.Ordinal)
            ? original.Name
            : _nodeName;

        return new ZmkCombo(name, "", SelectedKeys, LayerScope(), binding)
        {
            NodeName = _nodeName,
            TimeoutMs = ParseMilliseconds(TimeoutText),
            RequirePriorIdleMs = ParseMilliseconds(IdleText),
            SlowRelease = SlowRelease,
        };
    }

    // ─── Internals ────────────────────────────────────────────────────────

    /// <summary>The names already used by the other combos, for auto-naming.</summary>
    private IEnumerable<string> TakenNames() => _others.Select(Identity);

    private static string Identity(ZmkCombo combo) => combo.NodeName ?? combo.Name;

    private static bool IsNone(KeyBinding binding) =>
        string.Equals(binding.Behavior, KeyBinding.None.Behavior, StringComparison.Ordinal);

    /// <summary>The layer scope the combo would be written with: all layers,
    /// or the layers its toggles are on, ascending.</summary>
    private IReadOnlyList<int> LayerScope() => AllLayers
        ? new[] { -1 }
        : LayerToggles.Where(toggle => toggle.IsOn).Select(toggle => toggle.Index).Order().ToArray();

    private static string Milliseconds(int? value) =>
        value is { } ms ? ms.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>The typed milliseconds, or null when the box is empty. A
    /// value the checks reject reads as "not set" here.</summary>
    private static int? ParseMilliseconds(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private void SetBinding(KeyBinding? binding)
    {
        Binding = binding;
        BindingText = binding is { } value
            ? ZmkBindingFormatter.Format(value, _request.LayerDefines, _config.HoldTaps)
            : Loc.Instance["Combo_BindingNone"];

        if (IsNameAuto) _nodeName = ComboRules.AutoName(binding, TakenNames());

        OnPropertyChanged(nameof(Binding));
        OnPropertyChanged(nameof(BindingText));
        OnPropertyChanged(nameof(NodeName));
    }

    /// <summary>Re-applies everything the board shows: the other combos'
    /// badges, the caps' labels for the current label layer, and the gold
    /// fill of the picked caps. Labels run after the badges because
    /// <see cref="KeyViewModel.SetCombos"/> rewrites the tooltip, and the
    /// per-cap position-plus-binding line is the one to keep.</summary>
    private void RefreshBoard()
    {
        RefreshBadges();
        RefreshKeyLabels();
        RefreshKeyFills();
    }

    private void RefreshBadges()
    {
        string LabelLookup(int index) =>
            index >= 0 && index < Board.Keys.Count ? Board.Keys[index].Label : "";

        for (var i = 0; i < Board.Keys.Count; i++)
        {
            var combos = new List<ZmkCombo>();
            var numbers = new List<int>();
            for (var c = 0; c < _others.Count; c++)
            {
                if (!_others[c].KeyPositions.Contains(i)) continue;
                combos.Add(_others[c]);
                numbers.Add(_otherNumbers[c]);
            }

            Board.Keys[i].SetCombos(combos, numbers, LabelLookup);
        }
    }

    /// <summary>Labels every cap from the label layer's binding — the same
    /// formatting the board itself uses, with the target layer and hold-tap
    /// resolved the way the key dialog resolves them for its preview.</summary>
    private void RefreshKeyLabels()
    {
        var bindings = _config.Layers[LabelLayer.Index].Bindings;
        foreach (var key in Board.Keys)
        {
            var index = key.Position.Index;
            var binding = index >= 0 && index < bindings.Count ? bindings[index] : KeyBinding.None;

            var icon = KeyLabelFormatter.NormalizeIconName(binding.DecorationIcon);
            key.Behavior = binding.Behavior;
            key.IconName = icon;
            if (icon.Length > 0)
            {
                key.Label = binding.DecorationLabel ?? "";
                key.Subscript = "";
                key.TopLeftLabel = "";
            }
            else
            {
                var (label, subscript, topLeft) = KeyLabelFormatter.FormatBinding(
                    binding, TargetLayerName(binding), HoldTapFor(binding), _macros);
                key.Label = label;
                key.Subscript = subscript;
                key.TopLeftLabel = topLeft;
            }

            key.Tooltip = (key.Position.Description is { } description
                ? $"{description}  (idx {index})"
                : $"idx {index}") + "\n\n" + binding.Display;
        }
    }

    /// <summary>The layer a layer behavior points at, by name — the parameter
    /// has to be a number addressing an existing layer.</summary>
    private string? TargetLayerName(KeyBinding binding)
    {
        if (!LayerBehaviors.Contains(binding.Behavior) || binding.Params.Count == 0)
            return null;
        if (!int.TryParse(binding.Params[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
            return null;
        if (index < 0 || index >= _config.Layers.Count) return null;
        return _config.Layers[index].Name;
    }

    private HoldTap? HoldTapFor(KeyBinding binding) =>
        _config.HoldTaps.FirstOrDefault(tap => tap.Name == binding.Behavior);

    private void RefreshKeyFills()
    {
        for (var i = 0; i < Board.Keys.Count; i++)
            Board.Keys[i].KeyFillColor = _selected.Contains(i) ? SelectedFill : DefaultFill;
    }

    private void ToggleKey(int index)
    {
        if (index < 0 || index >= Board.Keys.Count) return;
        if (!_selected.Remove(index)) _selected.Add(index);
        RefreshKeyFills();
        RefreshDerived();
    }

    /// <summary>Publishes the picked keys and the label summary read off the
    /// caps as they stand — runs inside <see cref="RefreshDerived"/>, so
    /// anything that changes the labels refreshes the summary too.</summary>
    private void CommitSelection()
    {
        SelectedKeys = _selected.ToArray();
        KeysSummary = string.Join(" + ", _selected.Select(index =>
            Board.Keys[index].Label is { Length: > 0 } label ? label : $"#{index}"));
        OnPropertyChanged(nameof(SelectedKeys));
        OnPropertyChanged(nameof(KeysSummary));
    }

    /// <summary>Recomputes <see cref="Errors"/>, <see cref="Warnings"/> and
    /// <see cref="CanConfirm"/>, and the key summary that reads the caps'
    /// labels. Called after anything the dialog can change.</summary>
    private void RefreshDerived()
    {
        // Publish the picked keys first: the checks draft the combo from the
        // published list.
        CommitSelection();

        var errors = new List<string>();
        var warnings = new List<string>();

        if (Binding is null) errors.Add(Loc.Instance["Combo_ErrorNoBinding"]);
        if (IsSet(TimeoutText) && ParseMilliseconds(TimeoutText) is not > 0)
            errors.Add(Loc.Instance["Combo_ErrorTimeout"]);
        if (IsSet(IdleText) && ParseMilliseconds(IdleText) is null or < 0)
            errors.Add(Loc.Instance["Combo_ErrorIdle"]);
        if (!AllLayers && !LayerToggles.Any(toggle => toggle.IsOn))
            errors.Add(Loc.Instance["Combo_ErrorNoLayers"]);

        foreach (var problem in ComboRules.Check(Draft(), _others))
            (problem.IsError ? errors : warnings).Add(Message(problem));

        _errors = errors;
        _warnings = warnings;

        OnPropertyChanged(nameof(Errors));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(CanConfirm));
    }

    /// <summary>What the dialog would write right now, for the rule checks.
    /// <see cref="ZmkCombo.Binding"/> is non-nullable, so a combo without a
    /// binding is drafted as <c>&amp;none</c> — no check reads it.</summary>
    private ZmkCombo Draft() => new(_nodeName, "", SelectedKeys, LayerScope(), Binding ?? KeyBinding.None)
    {
        NodeName = _nodeName,
        TimeoutMs = ParseMilliseconds(TimeoutText),
        RequirePriorIdleMs = ParseMilliseconds(IdleText),
        SlowRelease = SlowRelease,
    };

    private static bool IsSet(string text) => text.Trim().Length > 0;

    /// <summary>A problem as the UI words it — errors and warnings are each
    /// kind's own resx text.</summary>
    private static string Message(ComboProblem problem) => problem.Kind switch
    {
        ComboProblemKind.TooFewKeys => Loc.Instance["Combo_ErrorTooFewKeys"],
        ComboProblemKind.InvalidName => Loc.Instance["Combo_ErrorInvalidName"],
        ComboProblemKind.DuplicateName =>
            Loc.Instance.Format("Combo_ErrorDuplicateNameFormat", problem.Other ?? ""),
        ComboProblemKind.SameKeysAsOther =>
            Loc.Instance.Format("Combo_WarnSameKeysFormat", problem.Other ?? ""),
        ComboProblemKind.TooManyKeys =>
            Loc.Instance.Format("Combo_WarnTooManyKeysFormat", problem.Count ?? 0),
        ComboProblemKind.KeyInManyCombos =>
            Loc.Instance.Format("Combo_WarnKeyInManyCombosFormat", problem.KeyIndex ?? 0, problem.Count ?? 0),
        _ => throw new ArgumentOutOfRangeException(
            nameof(problem), problem.Kind, "Combo problem kind has no message."),
    };
}
