using System.Globalization;
using LViz.Core.Keymap.Catalog;
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
/// One selectable sector of the ring. <see cref="Target"/> is what
/// <see cref="KeyRingViewModel.Choose"/> takes and <see cref="Id"/> its stable
/// key (for hover and animation state, never parsed); <see cref="Label"/> and
/// <see cref="Sub"/> are the two lines the sector draws (a
/// <see cref="LabelIsCode"/>/<see cref="SubIsCode"/> line in the mono code
/// font, the other in the UI font), <see cref="Icon"/> an optional FontAwesome
/// class. <see cref="IsCurrent"/> marks the sector holding the current value,
/// <see cref="IsOn"/> one switched on, <see cref="IsEnabled"/> false for a
/// sector that is shown but not pickable.
/// </summary>
public sealed record KeyRingSegment(
    KeyRingTarget Target, string Label, string? Sub, bool LabelIsCode, bool SubIsCode,
    string? Icon, bool IsCurrent, bool IsOn = false, bool IsEnabled = true)
{
    /// <summary>The target's stable key.</summary>
    public string Id => Target.Key;
}

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
/// One row of the list panel: <see cref="Target"/> is what
/// <see cref="KeyRingViewModel.Choose"/> takes (<see cref="Id"/> is its stable
/// key), <see cref="Label"/> and
/// <see cref="Sub"/> are the two lines (a <see cref="LabelIsCode"/> line in
/// the mono code font), <see cref="IsSelected"/> marks the row holding the
/// current value and <see cref="IsHighlighted"/> the row the keyboard cursor
/// is on — the view draws the highlight from the row, so it travels with the
/// rows a rebuild produces.
/// </summary>
public sealed record KeyRingPanelRow(
    KeyRingTarget Target, string Label, string? Sub, bool LabelIsCode, bool IsSelected,
    bool IsHighlighted = false)
{
    /// <summary>The target's stable key.</summary>
    public string Id => Target.Key;
}

/// <summary>
/// What choosing a ring sector or panel row does. A closed set: one case per
/// kind of sector, so the view-model dispatches by type. <see cref="Key"/> is
/// a stable string derived from the target, for dictionaries keyed by sector
/// (hover and animation state) and for tests; nothing parses it back.
/// </summary>
public abstract record KeyRingTarget
{
    private KeyRingTarget() { }

    /// <summary>Stable key of this target, unique among the sectors shown at once.</summary>
    public abstract string Key { get; }

    /// <summary>Whether choosing this sets part of the binding (a behavior,
    /// layer, command, profile or keycode), as opposed to browsing a keycode
    /// group or flipping a modifier toggle. A double-click on such a sector
    /// saves once the binding is complete.</summary>
    public virtual bool PicksAValue => true;

    /// <summary>A behavior category of the main ring.</summary>
    public sealed record Category(ZmkBehaviorGroup Group) : KeyRingTarget
    {
        public override string Key => "cat:" + Group;
    }

    /// <summary>A behavior of a band or of the behaviors panel (Custom included).</summary>
    public sealed record Behavior(KeyBehaviorChoice Choice) : KeyRingTarget
    {
        public override string Key => "beh:" + (Choice.IsCustom ? "custom" : Choice.Code);
    }

    /// <summary>A layer of a layer parameter.</summary>
    public sealed record Layer(int Index) : KeyRingTarget
    {
        public override string Key => "layer:" + Index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A command; one that takes a profile number is pinned to its
    /// profile band instead of being chosen.</summary>
    public sealed record Command(CommandChoice Choice) : KeyRingTarget
    {
        public override string Key => "cmd:" + Choice.Command.Name;
    }

    /// <summary>A command together with its profile number.</summary>
    public sealed record Profile(CommandChoice Owner, ProfileChoice Choice) : KeyRingTarget
    {
        public override string Key =>
            "prof:" + Owner.Command.Name + ":" + Choice.Number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One of the keycode groups: browses it, sets nothing.</summary>
    public sealed record KeyGroup(KeyRingKeyGroup Group) : KeyRingTarget
    {
        public override string Key => "keys:" + Group;
        public override bool PicksAValue => false;
    }

    /// <summary>One modifier wrap toggle: flips it, sets nothing.</summary>
    public sealed record Modifier(ZmkModifiers Flag) : KeyRingTarget
    {
        public override string Key => "mod:" + Flag;
        public override bool PicksAValue => false;
    }

    /// <summary>A listed keycode.</summary>
    public sealed record Keycode(ZmkKeycode Value) : KeyRingTarget
    {
        public override string Key => "keycode:" + Value.Name;
    }
}
