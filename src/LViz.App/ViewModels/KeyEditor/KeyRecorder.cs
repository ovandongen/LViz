using Avalonia.Input;
using LViz.Core.Keymap.Catalog;

namespace LViz.App.ViewModels;

/// <summary>
/// Maps Avalonia's layout-independent <see cref="PhysicalKey"/> (the physical
/// key position, independent of the active keyboard layout) to the canonical
/// ZMK keycode names of <see cref="ZmkKeycodeCatalog"/>. The explicit table
/// is the audited spec for the key dialog's record button: unmapped keys
/// (mouse buttons, browser keys, <see cref="PhysicalKey.None"/>, …) return
/// null and are never recorded.
/// </summary>
public static class KeyRecorderMap
{
    private static readonly Dictionary<PhysicalKey, string> ZmkNames = new()
    {
        // Letters
        [PhysicalKey.A] = "A", [PhysicalKey.B] = "B", [PhysicalKey.C] = "C", [PhysicalKey.D] = "D",
        [PhysicalKey.E] = "E", [PhysicalKey.F] = "F", [PhysicalKey.G] = "G", [PhysicalKey.H] = "H",
        [PhysicalKey.I] = "I", [PhysicalKey.J] = "J", [PhysicalKey.K] = "K", [PhysicalKey.L] = "L",
        [PhysicalKey.M] = "M", [PhysicalKey.N] = "N", [PhysicalKey.O] = "O", [PhysicalKey.P] = "P",
        [PhysicalKey.Q] = "Q", [PhysicalKey.R] = "R", [PhysicalKey.S] = "S", [PhysicalKey.T] = "T",
        [PhysicalKey.U] = "U", [PhysicalKey.V] = "V", [PhysicalKey.W] = "W", [PhysicalKey.X] = "X",
        [PhysicalKey.Y] = "Y", [PhysicalKey.Z] = "Z",
        // Digits
        [PhysicalKey.Digit1] = "NUMBER_1", [PhysicalKey.Digit2] = "NUMBER_2", [PhysicalKey.Digit3] = "NUMBER_3",
        [PhysicalKey.Digit4] = "NUMBER_4", [PhysicalKey.Digit5] = "NUMBER_5", [PhysicalKey.Digit6] = "NUMBER_6",
        [PhysicalKey.Digit7] = "NUMBER_7", [PhysicalKey.Digit8] = "NUMBER_8", [PhysicalKey.Digit9] = "NUMBER_9",
        [PhysicalKey.Digit0] = "NUMBER_0",
        // Whitespace / editing
        [PhysicalKey.Enter] = "RETURN", [PhysicalKey.Escape] = "ESCAPE", [PhysicalKey.Backspace] = "BACKSPACE",
        [PhysicalKey.Tab] = "TAB", [PhysicalKey.Space] = "SPACE",
        // Punctuation
        [PhysicalKey.Minus] = "MINUS", [PhysicalKey.Equal] = "EQUAL",
        [PhysicalKey.BracketLeft] = "LEFT_BRACKET", [PhysicalKey.BracketRight] = "RIGHT_BRACKET",
        [PhysicalKey.Backslash] = "BACKSLASH", [PhysicalKey.Semicolon] = "SEMICOLON",
        [PhysicalKey.Quote] = "SINGLE_QUOTE", [PhysicalKey.Backquote] = "GRAVE",
        [PhysicalKey.Comma] = "COMMA", [PhysicalKey.Period] = "PERIOD", [PhysicalKey.Slash] = "SLASH",
        [PhysicalKey.IntlBackslash] = "NON_US_BACKSLASH", [PhysicalKey.IntlRo] = "INTERNATIONAL_1",
        [PhysicalKey.IntlYen] = "INTERNATIONAL_3",
        // Locks and system
        [PhysicalKey.CapsLock] = "CAPSLOCK", [PhysicalKey.ScrollLock] = "SCROLLLOCK",
        [PhysicalKey.NumLock] = "KP_NUMLOCK", [PhysicalKey.PrintScreen] = "PRINTSCREEN",
        [PhysicalKey.Pause] = "PAUSE_BREAK",
        // Function row
        [PhysicalKey.F1] = "F1", [PhysicalKey.F2] = "F2", [PhysicalKey.F3] = "F3", [PhysicalKey.F4] = "F4",
        [PhysicalKey.F5] = "F5", [PhysicalKey.F6] = "F6", [PhysicalKey.F7] = "F7", [PhysicalKey.F8] = "F8",
        [PhysicalKey.F9] = "F9", [PhysicalKey.F10] = "F10", [PhysicalKey.F11] = "F11", [PhysicalKey.F12] = "F12",
        [PhysicalKey.F13] = "F13", [PhysicalKey.F14] = "F14", [PhysicalKey.F15] = "F15", [PhysicalKey.F16] = "F16",
        [PhysicalKey.F17] = "F17", [PhysicalKey.F18] = "F18", [PhysicalKey.F19] = "F19", [PhysicalKey.F20] = "F20",
        [PhysicalKey.F21] = "F21", [PhysicalKey.F22] = "F22", [PhysicalKey.F23] = "F23", [PhysicalKey.F24] = "F24",
        // Navigation
        [PhysicalKey.Insert] = "INSERT", [PhysicalKey.Delete] = "DELETE",
        [PhysicalKey.Home] = "HOME", [PhysicalKey.End] = "END",
        [PhysicalKey.PageUp] = "PAGE_UP", [PhysicalKey.PageDown] = "PAGE_DOWN",
        [PhysicalKey.ArrowUp] = "UP_ARROW", [PhysicalKey.ArrowDown] = "DOWN_ARROW",
        [PhysicalKey.ArrowLeft] = "LEFT_ARROW", [PhysicalKey.ArrowRight] = "RIGHT_ARROW",
        // Numpad
        [PhysicalKey.NumPad0] = "KP_NUMBER_0", [PhysicalKey.NumPad1] = "KP_NUMBER_1",
        [PhysicalKey.NumPad2] = "KP_NUMBER_2", [PhysicalKey.NumPad3] = "KP_NUMBER_3",
        [PhysicalKey.NumPad4] = "KP_NUMBER_4", [PhysicalKey.NumPad5] = "KP_NUMBER_5",
        [PhysicalKey.NumPad6] = "KP_NUMBER_6", [PhysicalKey.NumPad7] = "KP_NUMBER_7",
        [PhysicalKey.NumPad8] = "KP_NUMBER_8", [PhysicalKey.NumPad9] = "KP_NUMBER_9",
        [PhysicalKey.NumPadDivide] = "KP_DIVIDE", [PhysicalKey.NumPadMultiply] = "KP_MULTIPLY",
        [PhysicalKey.NumPadSubtract] = "KP_MINUS", [PhysicalKey.NumPadAdd] = "KP_PLUS",
        [PhysicalKey.NumPadEnter] = "KP_ENTER", [PhysicalKey.NumPadDecimal] = "KP_DOT",
        [PhysicalKey.NumPadEqual] = "KP_EQUAL", [PhysicalKey.NumPadComma] = "KP_COMMA",
        [PhysicalKey.NumPadClear] = "KP_CLEAR",
        [PhysicalKey.NumPadParenLeft] = "KP_LEFT_PARENTHESIS",
        [PhysicalKey.NumPadParenRight] = "KP_RIGHT_PARENTHESIS",
        // Misc
        [PhysicalKey.ContextMenu] = "K_APPLICATION",
        [PhysicalKey.Lang1] = "LANGUAGE_1", [PhysicalKey.Lang2] = "LANGUAGE_2", [PhysicalKey.Lang3] = "LANGUAGE_3",
        [PhysicalKey.Lang4] = "LANGUAGE_4", [PhysicalKey.Lang5] = "LANGUAGE_5",
        // Consumer / media
        [PhysicalKey.MediaPlayPause] = "C_PLAY_PAUSE", [PhysicalKey.MediaTrackNext] = "C_NEXT",
        [PhysicalKey.MediaTrackPrevious] = "C_PREVIOUS", [PhysicalKey.MediaStop] = "C_STOP",
        [PhysicalKey.AudioVolumeUp] = "C_VOLUME_UP", [PhysicalKey.AudioVolumeDown] = "C_VOLUME_DOWN",
        [PhysicalKey.AudioVolumeMute] = "C_MUTE",
        // Modifiers
        [PhysicalKey.ControlLeft] = "LEFT_CONTROL", [PhysicalKey.ShiftLeft] = "LEFT_SHIFT",
        [PhysicalKey.AltLeft] = "LEFT_ALT", [PhysicalKey.MetaLeft] = "LEFT_GUI",
        [PhysicalKey.ControlRight] = "RIGHT_CONTROL", [PhysicalKey.ShiftRight] = "RIGHT_SHIFT",
        [PhysicalKey.AltRight] = "RIGHT_ALT", [PhysicalKey.MetaRight] = "RIGHT_GUI",
    };

    /// <summary>
    /// The canonical ZMK keycode name for <paramref name="key"/>, or null
    /// when the key has no ZMK mapping.
    /// </summary>
    public static string? ToZmk(PhysicalKey key) =>
        ZmkNames.TryGetValue(key, out var name) ? name : null;

    /// <summary>
    /// True for the eight modifier keys (left and right Ctrl, Shift, Alt,
    /// GUI) — the keys that can be recorded solo by pressing and releasing
    /// them on their own.
    /// </summary>
    public static bool IsModifier(PhysicalKey key) =>
        key is PhysicalKey.ControlLeft or PhysicalKey.ShiftLeft or
            PhysicalKey.AltLeft or PhysicalKey.MetaLeft or
            PhysicalKey.ControlRight or PhysicalKey.ShiftRight or
            PhysicalKey.AltRight or PhysicalKey.MetaRight;
}

/// <summary>
/// One recorded key: the ZMK keycode name plus the modifiers that were held
/// (left-side flags only — the dialog's wrap toggles are left-only, so
/// a right-side modifier press records as its left counterpart).
/// </summary>
public sealed record RecordedKey(string ZmkName, ZmkModifiers Modifiers);

/// <summary>
/// Turns key-down/key-up events into the one <see cref="RecordedKey"/> the
/// key dialog's record button wants. Use one instance per recording: once it
/// returns a result it is done (<see cref="IsDone"/>) and swallows everything.
///
/// <list type="bullet">
/// <item>A mapped non-modifier key press records that key with the held
/// modifiers as wrap toggles (Shift+1 → NUMBER_1 + LeftShift).</item>
/// <item>A modifier pressed and released on its own records the modifier
/// keycode itself (LEFT_SHIFT, no modifiers).</item>
/// <item>A chord of two or more modifiers records nothing until all are
/// released; recording then starts fresh.</item>
/// <item>An unmapped key press records nothing but ends a pending solo
/// modifier and keeps the recording alive; once every key is released again,
/// a fresh solo modifier can be recorded.</item>
/// </list>
/// </summary>
public sealed class KeyRecorder
{
    private readonly HashSet<PhysicalKey> _heldModifiers = new();
    private readonly HashSet<PhysicalKey> _heldUnmapped = new();
    private PhysicalKey? _soloCandidate;
    private bool _nonModifierPressed;
    private bool _chordHeld;

    /// <summary>True once a key has been recorded; the recorder then ignores all input.</summary>
    public bool IsDone { get; private set; }

    /// <summary>
    /// Feed a key-down. Returns the recorded key when this press (plus the
    /// held modifiers) is the recording's result; otherwise null while the
    /// recording goes on.
    /// </summary>
    public RecordedKey? KeyDown(PhysicalKey key, KeyModifiers modifiers)
    {
        if (IsDone)
            return null;

        if (KeyRecorderMap.IsModifier(key))
        {
            _heldModifiers.Add(key);
            if (_soloCandidate is { } candidate && candidate != key)
            {
                // A second modifier while one is tracked: a chord, no solo
                // result until every modifier has been released.
                _soloCandidate = null;
                _chordHeld = true;
            }
            else if (_soloCandidate is null && !_nonModifierPressed && !_chordHeld)
            {
                _soloCandidate = key;
            }

            return null;
        }

        var name = KeyRecorderMap.ToZmk(key);
        if (name is null)
        {
            // Unmapped key: recording goes on, but a still-held modifier has
            // lost its solo chance. Kept in the held set so the flag clears
            // again when the key comes back up.
            _soloCandidate = null;
            _nonModifierPressed = true;
            _heldUnmapped.Add(key);
            return null;
        }

        IsDone = true;
        return new RecordedKey(name, ToZmkModifiers(modifiers));
    }

    /// <summary>
    /// Feed a key-up. Returns the solo modifier keycode when
    /// <paramref name="key"/> was pressed and released on its own; otherwise
    /// null.
    /// </summary>
    public RecordedKey? KeyUp(PhysicalKey key)
    {
        if (IsDone)
            return null;

        var wasHeldModifier = _heldModifiers.Remove(key);
        _heldUnmapped.Remove(key);

        if (wasHeldModifier && _soloCandidate == key)
        {
            IsDone = true;
            _soloCandidate = null;
            return new RecordedKey(KeyRecorderMap.ToZmk(key)!, ZmkModifiers.None);
        }

        // Chorded or invalidated: only once every modifier and every unmapped
        // key is up again can a new solo candidate be tracked.
        if (_heldModifiers.Count == 0 && _heldUnmapped.Count == 0)
        {
            _chordHeld = false;
            _nonModifierPressed = false;
        }

        return null;
    }

    private static ZmkModifiers ToZmkModifiers(KeyModifiers modifiers)
    {
        ZmkModifiers flags = ZmkModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Shift))
            flags |= ZmkModifiers.LeftShift;
        if (modifiers.HasFlag(KeyModifiers.Control))
            flags |= ZmkModifiers.LeftCtrl;
        if (modifiers.HasFlag(KeyModifiers.Alt))
            flags |= ZmkModifiers.LeftAlt;
        if (modifiers.HasFlag(KeyModifiers.Meta))
            flags |= ZmkModifiers.LeftGui;
        return flags;
    }
}
