namespace LViz.Core.Layout;

/// <summary>
/// The USB HID identifiers ZMK keyboards register under. MoErgo's firmware
/// (Glove80, Go60) and many custom builds use the shared 0x16C0/0x27DB pair;
/// upstream ZMK's default is 0x1D50/0x615E (Cosmos, Slabwing, and any board
/// that doesn't set its own). Many boards share each pair, so telling two ZMK
/// devices apart also takes the product name, see
/// <see cref="IKeyboardProfile.MatchesHidDevice"/>.
/// </summary>
internal static class ZmkHidIds
{
    public const int VendorId = 0x16C0;
    public const int ProductId = 0x27DB;

    public const int UpstreamVendorId = 0x1D50;
    public const int UpstreamProductId = 0x615E;

    /// <summary>True for either ZMK identifier pair.</summary>
    public static bool Matches(int vendorId, int productId) =>
        (vendorId, productId) is (VendorId, ProductId) or (UpstreamVendorId, UpstreamProductId);
}
