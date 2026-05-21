namespace LViz.Core.Layout;

/// <summary>
/// Corne (foostan) 6-column variant: 42 keys, 6 columns per hand, three thumb
/// keys per hand (middle two rotated). Geometry loaded from
/// <c>Resources/corne.dtsi</c>, adapted from upstream ZMK's
/// <c>app/dts/layouts/foostan/corne/6column.dtsi</c>.
/// </summary>
public sealed class CorneProfile() : DtsiKeyboardProfile(
    id: "Corne",
    displayName: "Corne (6-col)",
    keyCount: 42,
    vendorId: ZmkHidIds.VendorId,
    productId: ZmkHidIds.ProductId,
    hidNameSubstring: "Corne",
    dtsiResourceName: "LViz.Core.Resources.corne.dtsi",
    midlineCentiU: 700,
    // Centi-u extent is 0..1400 (cols 0..13); 0.6 px/centi-u + 30 px margin
    // → ~870 px. Round up for breathing room.
    canvasWidth: 900,
    // Y extent reaches ~430 centi-u including the rotated 1.5u thumbs.
    canvasHeight: 360,
    rightmostMatrixXCentiU: 1300,
    thumbLabels: ThumbLabels)
{
    private static readonly IReadOnlyDictionary<int, string> ThumbLabels =
        new Dictionary<int, string>
        {
            [37] = "Left thumb middle",
            [38] = "Left thumb inner",
            [39] = "Right thumb inner",
            [40] = "Right thumb middle",
        };
}
