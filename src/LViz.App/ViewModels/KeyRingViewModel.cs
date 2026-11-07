using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;
using LViz.Core.Keymap.Catalog;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;

namespace LViz.App.ViewModels;

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
    /// list.</summary>
    public const int MaxBandItems = 6;

    /// <summary>Sectors the main ring fits; more than this go to a list.</summary>
    public const int MaxRingSegments = 14;

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
    /// for; a new instance only when that source changes.</summary>
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

    // ─── Applying a choice ────────────────────────────────────────────────

    /// <summary>
    /// Applies the sector <paramref name="target"/> and returns whether something
    /// happened. A category with exactly one behavior is that behavior (a wider
    /// category is pinned to its band by the view, and one too wide for a band
    /// opens the list panel); a behavior switches to it or, when it is already
    /// selected, moves on to the first parameter still without a value; Custom
    /// opens the text step. A parameter sector (a layer, a command without a
    /// profile number, a profile number, a keycode group, a modifier toggle, a
    /// keycode) edits its parameter and steps on to what still needs a value.
    /// Anything that isn't a category choice drops the behaviors panel. A
    /// target the current step does not offer does nothing.
    /// </summary>
    public bool Choose(KeyRingTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var applied = target switch
        {
            KeyRingTarget.Category category => ChooseCategory(category),
            KeyRingTarget.Behavior behavior => ChooseBehavior(behavior),
            KeyRingTarget.Layer layer => ChooseLayer(layer),
            KeyRingTarget.Profile profile => ChooseProfile(profile),
            KeyRingTarget.Command command => ChooseCommand(command),
            KeyRingTarget.KeyGroup group => ChooseKeys(group),
            KeyRingTarget.Modifier modifier => ChooseModifier(modifier),
            KeyRingTarget.Keycode keycode => ChooseKeycode(keycode),
            _ => false,
        };

        if (!applied) return false;

        // A category choice may have opened the behaviors panel; the others
        // drop it.
        if (target is not KeyRingTarget.Category) _behaviorsPanel = null;
        NotifyAll();
        return true;
    }

    /// <summary>A single-behavior category is that behavior; a wider one that
    /// fits a band is pinned by the view (false here); one too wide opens the
    /// behaviors panel.</summary>
    private bool ChooseCategory(KeyRingTarget.Category category)
    {
        var behaviors = BehaviorsIn(category.Group);
        if (behaviors.Count == 0) return false;

        if (behaviors.Count > 1)
        {
            if (behaviors.Count <= MaxBandItems) return false;
            _behaviorsPanel = category.Group;
            return true;
        }

        if (!ApplyChoice(behaviors[0])) return false;
        _behaviorsPanel = null;
        return true;
    }

    private bool ChooseBehavior(KeyRingTarget.Behavior target)
    {
        var choice = Dialog.AllOtherBehaviors.FirstOrDefault(c => c == target.Choice);
        return choice is not null && ApplyChoice(choice);
    }

    /// <summary>The layer sector's layer, then step on.</summary>
    private bool ChooseLayer(KeyRingTarget.Layer target)
    {
        if (LayerParam is not { } param) return false;

        var choice = param.Layers.FirstOrDefault(layer => layer.Index == target.Index);
        if (choice is null) return false;

        param.SelectedLayer = choice;
        Advance();
        return true;
    }

    /// <summary>A command that takes a profile number is picked together with
    /// that number; stepping onto its band is the view's job (see
    /// <see cref="ChooseCommand"/>).</summary>
    private bool ChooseProfile(KeyRingTarget.Profile target)
    {
        if (CommandParam is not { } param) return false;

        var command = param.Commands.FirstOrDefault(choice => choice == target.Owner);
        if (command is not { Command.TakesNumber: true }) return false;

        var profile = param.Profiles.FirstOrDefault(p => p == target.Choice);
        if (profile is null) return false;

        param.SelectedCommand = command;
        param.SelectedProfile = profile;
        Advance();
        return true;
    }

    /// <summary>A command without a number is picked here; one that takes a
    /// number returns false — the view pins its profile band instead.</summary>
    private bool ChooseCommand(KeyRingTarget.Command target)
    {
        if (CommandParam is not { } param) return false;

        var choice = param.Commands.FirstOrDefault(c => c == target.Choice);
        if (choice is null || choice.Command.TakesNumber) return false;

        param.SelectedCommand = choice;
        Advance();
        return true;
    }

    /// <summary>Shows one keycode group: the picker searches nothing and lists
    /// that category (Used, the category itself, or All for More). The step
    /// stays where it is.</summary>
    private bool ChooseKeys(KeyRingTarget.KeyGroup target)
    {
        if (KeycodePicker is not { } picker) return false;
        if (CategoryChoice(picker, target.Group) is not { } category) return false;

        picker.SearchText = "";
        picker.SelectedCategory = category;
        _keycodeGroup = target.Group;
        return true;
    }

    /// <summary>Flips one wrap toggle.</summary>
    private bool ChooseModifier(KeyRingTarget.Modifier target)
    {
        if (KeycodePicker is not { } picker) return false;

        var flag = target.Flag;
        if (flag == ZmkModifiers.None || (flag & (flag - 1)) != 0) return false;

        picker.ToggleWrap(flag);
        return true;
    }

    /// <summary>Picks the listed keycode, then steps on. A keycode the list
    /// does not hold (search filtering, a different group) does nothing.</summary>
    private bool ChooseKeycode(KeyRingTarget.Keycode target)
    {
        if (KeycodePicker is not { } picker) return false;

        var item = picker.Items.FirstOrDefault(entry => entry.Keycode == target.Value);
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
    /// notification once for every accepted target.</summary>
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
            return result.SameAs(_original) ? null : result;
        }
    }

    // ─── Internals ────────────────────────────────────────────────────────

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
                search => KeyRingRows.Filter(BehaviorRows(behaviors), search));
        }

        var tab = CurrentTab.ToString(CultureInfo.InvariantCulture);

        switch (StepKind)
        {
            case KeyRingStepKind.Keycode when StepEditor is { } editor && KeycodePicker is { } picker:
                return (
                    $"keys/{RuntimeHelpers.GetHashCode(editor)}/{tab}/{_keycodeGroup}",
                    loc[$"Ring_Keys_{_keycodeGroup}"],
                    search => KeyRingRows.Keycodes(picker, search));

            case KeyRingStepKind.Layer when LayerParam is { Layers: { Count: > MaxRingSegments } } layer:
                return (
                    $"layers/{RuntimeHelpers.GetHashCode(layer)}/{tab}",
                    Loc.Instance["Ring_Panel_Layers"],
                    search => KeyRingRows.Filter(KeyRingRows.Layers(layer, LayerName), search));

            case KeyRingStepKind.Command when CommandParam is { Commands: { Count: > MaxRingSegments } } command:
                return (
                    $"commands/{RuntimeHelpers.GetHashCode(command)}/{tab}",
                    command.Label,
                    search => KeyRingRows.Filter(KeyRingRows.Commands(command), search));
        }

        return (null, null, null);
    }

    /// <summary>The too-wide behavior list, rowed like the band draws it.</summary>
    private IReadOnlyList<KeyRingPanelRow> BehaviorRows(IReadOnlyList<KeyBehaviorChoice> behaviors) =>
        [
            .. behaviors.Select(behavior =>
            {
                var segment = BehaviorSegment(behavior, IsSelected(behavior));
                return new KeyRingPanelRow(
                    segment.Target, segment.Label, segment.Sub, segment.LabelIsCode, segment.IsCurrent);
            }),
        ];

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

    /// <summary>The category a choice belongs to; Custom counts as the
    /// keymap's own.</summary>
    private static ZmkBehaviorGroup CategoryOf(KeyBehaviorChoice choice) =>
        choice.Behavior?.Group ?? ZmkBehaviorGroup.Keymap;

    private bool IsSelected(KeyBehaviorChoice choice) =>
        string.Equals(choice.Code, Dialog.SelectedBehavior.Code, StringComparison.Ordinal)
        && choice.IsCustom == Dialog.SelectedBehavior.IsCustom;
}
