namespace LViz.Core.Layout;

/// <summary>
/// Sofle (josefadamcik): 60 binding slots, 6 columns per hand × 5 rows
/// (where row 4's two inner positions are the encoder slots in ZMK's
/// matrix-transform — they're visualised as plain keys here). 5-key thumb
/// row per hand with the middle pair rotated 12°/24°. Geometry loaded from
/// <c>Resources/sofle.dtsi</c>.
/// </summary>
public sealed class SofleProfile() : DtsiKeyboardProfile(
    id: "Sofle",
    displayName: "Sofle (60)",
    keyCount: 60,
    vendorId: ZmkHidIds.VendorId,
    productId: ZmkHidIds.ProductId,
    hidNameSubstring: "Sofle",
    dtsiResourceName: "LViz.Core.Resources.sofle.dtsi",
    midlineCentiU: 700,
    // X extent 0..1500 centi-u → 1500 × 0.6 + 30 = 930 px; round up.
    // Y extent stretches to ~537 centi-u including the 1.5u rotated thumbs.
    canvasWidth: 960,
    canvasHeight: 430,
    rightmostMatrixXCentiU: 1400,
    thumbLabels: ThumbLabels)
{
    private static readonly IReadOnlyDictionary<int, string> ThumbLabels =
        new Dictionary<int, string>
        {
            [53] = "Left thumb middle (rotated 12°)",
            [54] = "Left thumb inner (1.5u, rotated 24°)",
            [55] = "Right thumb inner (1.5u, rotated 24°)",
            [56] = "Right thumb middle (rotated 12°)",
        };
}
