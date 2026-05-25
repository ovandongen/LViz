namespace LViz.Core.Layout;

/// <summary>
/// Kyria (splitkb) rev3, 6-column variant: 50 keys, 6 columns per hand,
/// 4-key rotated thumb cluster on row 3 + 5-key rotated thumb cluster on
/// row 4 (per hand). All thumb keys rotate about a per-hand pivot at
/// y=792 centi-u (well below the lowest matrix row) — the cluster fans out
/// around an anatomical pivot rather than each key rotating about itself.
/// Geometry loaded from <c>Resources/kyria.dtsi</c>.
/// </summary>
public sealed class KyriaProfile() : DtsiKeyboardProfile(
    id: "Kyria",
    displayName: "Kyria (50, 6-col)",
    keyCount: 50,
    vendorId: ZmkHidIds.VendorId,
    productId: ZmkHidIds.ProductId,
    hidNameSubstring: "Kyria",
    dtsiResourceName: "LViz.Core.Resources.kyria.dtsi",
    midlineCentiU: 800,
    // X extent 0..1700 centi-u → 1700 × 0.6 + 30 = 1050 px. Round up + slack
    // for the thumb cluster's wide rotation arc.
    canvasWidth: 1080,
    // Y extent 0..425 nominal, but rotated thumb keys around pivot (400, 792)
    // can swing past the unrotated rect — give the canvas headroom for
    // RotatedBounds.
    canvasHeight: 460,
    rightmostMatrixXCentiU: 1600,
    thumbLabels: ThumbLabels)
{
    private static readonly IReadOnlyDictionary<int, string> ThumbLabels =
        new Dictionary<int, string>
        {
            [30] = "Left thumb (row 3, outer rotated)",
            [31] = "Left thumb (row 3, inner rotated)",
            [32] = "Right thumb (row 3, inner rotated)",
            [33] = "Right thumb (row 3, outer rotated)",
            [42] = "Left thumb (row 4, 1)",
            [43] = "Left thumb (row 4, 2)",
            [44] = "Left thumb (row 4, 3)",
            [45] = "Right thumb (row 4, 3)",
            [46] = "Right thumb (row 4, 2)",
            [47] = "Right thumb (row 4, 1)",
        };
}
