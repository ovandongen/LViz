namespace LViz.Core.Layout;

/// <summary>
/// Shared USB HID identifiers for ZMK-firmware keyboards. Both Moergo boards
/// (and most other ZMK builds) register under the pid.codes "shared" VID/PID
/// pair — distinguishing two ZMK devices requires comparing the product name
/// on top, see <see cref="IKeyboardProfile.MatchesHidDevice"/>.
/// </summary>
internal static class ZmkHidIds
{
    public const int VendorId = 0x16C0;
    public const int ProductId = 0x27DB;
}
