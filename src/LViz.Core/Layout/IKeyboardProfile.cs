namespace LViz.Core.Layout;

/// <summary>
/// A keyboard-specific physical layout: canvas dimensions and the absolute
/// positions of every key, indexed so they can be joined against
/// <see cref="Models.Layer.Bindings"/>.
/// </summary>
public interface IKeyboardProfile
{
    /// <summary>Stable id — "GO60", "Glove80", etc. — matches <see cref="Settings.UserSettings.Keyboard"/>.</summary>
    string Id { get; }

    /// <summary>Display name for the UI.</summary>
    string DisplayName { get; }

    /// <summary>Total number of physical keys (60 for GO60, 80 for Glove80).</summary>
    int KeyCount { get; }

    /// <summary>Canvas width for the full board render.</summary>
    double CanvasWidth { get; }

    /// <summary>Canvas height for the full board render.</summary>
    double CanvasHeight { get; }

    /// <summary>
    /// Every key on the board. Order is irrelevant — <see cref="KeyPosition.Index"/>
    /// is authoritative and must match the JSON binding order.
    /// </summary>
    IReadOnlyList<KeyPosition> Keys { get; }

    /// <summary>
    /// True when the given USB HID identifiers describe a device of *this*
    /// keyboard family. Used by the Raw HID layer source to ignore reports
    /// from a different connected board (e.g. Go60 plugged in alongside a
    /// Glove80 — without this filter, layer reports would cross-contaminate).
    /// <para>
    /// Both Moergo boards register under the shared pid.codes ZMK identifier
    /// (VID 0x16C0 / PID 0x27DB), so the implementation also has to compare
    /// the product name. Use a *case-insensitive prefix* match — the suffix
    /// differs by transport (USB exposes "Go60 Left" because the cable side
    /// is the host; BLE exposes "Go60" because only the central is visible)
    /// and could change between firmware revisions.
    /// </para>
    /// </summary>
    bool MatchesHidDevice(int vendorId, int productId, string? productName);

    /// <summary>
    /// True when the product name alone identifies this keyboard family.
    /// Used by transports that can't read VID/PID (notably Windows BLE GATT,
    /// which exposes only the advertised device name). Same case-insensitive
    /// substring match as the name portion of <see cref="MatchesHidDevice"/>.
    /// </summary>
    bool NameMatches(string? productName);
}
