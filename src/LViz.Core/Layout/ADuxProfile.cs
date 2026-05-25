namespace LViz.Core.Layout;

/// <summary>
/// a_dux (tomsxor / The_Royal): 34-key minimal split, 3×5 columns per hand +
/// 2 thumbs per hand. No rotated keys — the thumb cluster sits just below
/// the bottom matrix row, parallel to it. Geometry loaded from
/// <c>Resources/a_dux.dtsi</c>, adapted from upstream ZMK's
/// <c>app/boards/shields/a_dux/a_dux-layouts.dtsi</c>.
/// </summary>
public sealed class ADuxProfile() : DtsiKeyboardProfile(
    id: "a_dux",
    displayName: "a_dux (34)",
    keyCount: 34,
    vendorId: ZmkHidIds.VendorId,
    productId: ZmkHidIds.ProductId,
    hidNameSubstring: "a_dux",
    dtsiResourceName: "LViz.Core.Resources.a_dux.dtsi",
    midlineCentiU: 600,
    // X extent 0..1300 centi-u → 1300 × 0.6 + 30 margin = 810 px; round up.
    // Y extent 0..500 centi-u → 500 × 0.6 + 60 margin = 360 px; round up.
    canvasWidth: 840,
    canvasHeight: 380,
    rightmostMatrixXCentiU: 1200);
