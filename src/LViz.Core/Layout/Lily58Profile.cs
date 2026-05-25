namespace LViz.Core.Layout;

/// <summary>
/// Lily58 (kata0510): 58 keys, 4 main rows × 12 cols + 2 inner row-4 keys
/// (the encoder slots in ZMK's matrix-transform, rendered as keys) + 8
/// thumb-row keys (3 + 2 rotated 30° 1.5u + 3) per board. Geometry loaded
/// from <c>Resources/lily58.dtsi</c>.
/// </summary>
public sealed class Lily58Profile() : DtsiKeyboardProfile(
    id: "Lily58",
    displayName: "Lily58 (58)",
    keyCount: 58,
    vendorId: ZmkHidIds.VendorId,
    productId: ZmkHidIds.ProductId,
    // Use "Lily58" (not "Lily") so we don't collide with other Lily-prefixed
    // ZMK boards under the shared VID/PID.
    hidNameSubstring: "Lily58",
    dtsiResourceName: "LViz.Core.Resources.lily58.dtsi",
    midlineCentiU: 775,
    // X extent 0..1650 centi-u → 1650 × 0.6 + 30 = 1020 px.
    canvasWidth: 1050,
    // Y extent 0..575 centi-u nominal; rotated 1.5u thumbs swing a bit
    // further around their pivot at (625, 475) / (1025, 475).
    canvasHeight: 440,
    rightmostMatrixXCentiU: 1550,
    thumbLabels: ThumbLabels)
{
    private static readonly IReadOnlyDictionary<int, string> ThumbLabels =
        new Dictionary<int, string>
        {
            [53] = "Left thumb inner (1.5u, rotated 30°)",
            [54] = "Right thumb inner (1.5u, rotated 30°)",
        };
}
