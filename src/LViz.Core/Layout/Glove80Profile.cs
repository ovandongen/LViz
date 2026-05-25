namespace LViz.Core.Layout;

/// <summary>
/// Moergo Glove80: 80 keys, 6 columns per hand, two 3-key rotated thumb
/// clusters per hand (row A 52–57, row B 69–74). All left-thumb keys
/// share rotation pivot (450, 925) centi-u; all right-thumb keys share
/// (1350, 925) — well below the lowest matrix row, so thumbs fan out
/// around an anatomical pivot rather than each rotating about itself.
/// Geometry loaded from <c>Resources/glove80.dtsi</c>, ported from
/// upstream moergo-layer-viz (<c>ovandongen/moergo-layer-viz @ HEAD</c>).
/// </summary>
public sealed class Glove80Profile() : DtsiKeyboardProfile(
    id: "Glove80",
    displayName: "Moergo Glove80",
    keyCount: 80,
    vendorId: ZmkHidIds.VendorId,
    productId: ZmkHidIds.ProductId,
    hidNameSubstring: "Glove80",
    dtsiResourceName: "LViz.Core.Resources.glove80.dtsi",
    midlineCentiU: 850,
    // Half-key (30 px) horizontal margin around the centi-u extent 0..1700.
    // Vertical canvas has extra slack so the thumbs swinging down around their
    // shared pivot don't overflow.
    canvasWidth: 1140,
    canvasHeight: 600,
    rightmostMatrixXCentiU: 1700,
    thumbLabels: ThumbLabels)
{
    private static readonly IReadOnlyDictionary<int, string> ThumbLabels =
        new Dictionary<int, string>
        {
            [52] = "Left thumb A1 (outer)",
            [53] = "Left thumb A2",
            [54] = "Left thumb A3 (inner)",
            [55] = "Right thumb A1 (inner)",
            [56] = "Right thumb A2",
            [57] = "Right thumb A3 (outer)",
            [69] = "Left thumb B1 (outer)",
            [70] = "Left thumb B2",
            [71] = "Left thumb B3 (inner)",
            [72] = "Right thumb B1 (inner)",
            [73] = "Right thumb B2",
            [74] = "Right thumb B3 (outer)",
        };
}
