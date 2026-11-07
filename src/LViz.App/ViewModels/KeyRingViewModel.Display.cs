using LViz.App.Localization;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;

namespace LViz.App.ViewModels;

// What the ring shows around the sectors: the crumbs above it, the centre
// disc and the hint pill. All of it is derived from the dialog on read.
public sealed partial class KeyRingViewModel
{
    /// <summary>The piece the centre shows for a parameter without a value.</summary>
    private const string MissingPiece = "___";

    /// <summary>Scratch behavior to re-nest one param group through the
    /// formatter (the dialog's raw seeding uses the same trick).</summary>
    private const string ScratchBehavior = "&x";
    private const string ScratchPrefix = "&x ";

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
            if (result.SameAs(_original))
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
}
