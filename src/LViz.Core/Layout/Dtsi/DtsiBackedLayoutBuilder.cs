namespace LViz.Core.Layout.Dtsi;

/// <summary>
/// Shared numeric conversion from ZMK physical key entries (centi-units,
/// centi-degrees) to the <see cref="KeyPosition"/> list each profile exposes:
/// position and size × <c>pxPerCentiU</c> plus margins, rotation / 100, pivot
/// only when rotation ≠ 0. Shipped dtsi boards parameterize the same loop via
/// <see cref="Build"/>, and a user layout feeds its keys straight into
/// <see cref="BuildFromKeys"/> instead of copy-pasting it.
/// </summary>
internal static class DtsiBackedLayoutBuilder
{
    /// <summary>
    /// Build the <see cref="KeyPosition"/> list for a profile.
    /// </summary>
    /// <param name="resourceName">Embedded resource name of the dtsi file (e.g. <c>"LViz.Core.Resources.go60.dtsi"</c>).</param>
    /// <param name="keyCount">Number of bindings the profile exposes; must match the dtsi entry count.</param>
    /// <param name="midlineCentiU">X cutoff in centi-units that separates left-hand keys from right-hand keys.</param>
    /// <param name="describeKey">
    /// Per-profile tooltip describer. Receives binding index, the raw dtsi
    /// entry (centi-units, centi-degrees), and the already-computed hand.
    /// </param>
    /// <param name="pxPerCentiU">Pixel multiplier. Both shipping boards use 0.6 (1u = 60 px).</param>
    /// <param name="marginX">Left-edge inset in pixels.</param>
    /// <param name="marginY">Top-edge inset in pixels.</param>
    public static IReadOnlyList<KeyPosition> Build(
        string resourceName,
        int keyCount,
        int midlineCentiU,
        Func<int, DtsiKeyPhysical, Hand, string> describeKey,
        double pxPerCentiU = 0.6,
        double marginX = 30,
        double marginY = 60)
    {
        var dtsi = DtsiEmbeddedResource.Load(resourceName).Keys;
        if (dtsi.Count != keyCount)
            throw new InvalidOperationException(
                $"{resourceName} defines {dtsi.Count} keys but the profile declares {keyCount}.");
        return BuildFromKeys(
            dtsi,
            (_, e) => e.X < midlineCentiU ? Hand.Left : Hand.Right,
            describeKey,
            pxPerCentiU,
            marginX,
            marginY);
    }

    /// <summary>
    /// Convert already-known key entries — no embedded resource involved.
    /// Hand and description are caller-supplied because a user layout carries
    /// an explicit per-key hand (no midline to infer from) and has no dtsi
    /// row/column semantics to describe.
    /// </summary>
    /// <param name="keys">Keys in binding order, in ZMK units.</param>
    /// <param name="handOf">Hand for one key, given its index and entry.</param>
    /// <param name="describeKey">Per-key tooltip text; receives index, entry, and the computed hand.</param>
    /// <param name="pxPerCentiU">Pixel multiplier. Both shipping boards use 0.6 (1u = 60 px).</param>
    /// <param name="marginX">Left-edge inset in pixels.</param>
    /// <param name="marginY">Top-edge inset in pixels.</param>
    internal static IReadOnlyList<KeyPosition> BuildFromKeys(
        IReadOnlyList<DtsiKeyPhysical> keys,
        Func<int, DtsiKeyPhysical, Hand> handOf,
        Func<int, DtsiKeyPhysical, Hand, string> describeKey,
        double pxPerCentiU = 0.6,
        double marginX = 30,
        double marginY = 60)
    {
        var list = new List<KeyPosition>(keys.Count);
        for (int index = 0; index < keys.Count; index++)
        {
            var entry = keys[index];
            var hand = handOf(index, entry);
            double? rotOx = null, rotOy = null;
            if (entry.Rotation != 0)
            {
                rotOx = entry.Rx * pxPerCentiU + marginX;
                rotOy = entry.Ry * pxPerCentiU + marginY;
            }
            list.Add(new KeyPosition(
                Index: index,
                X: entry.X * pxPerCentiU + marginX,
                Y: entry.Y * pxPerCentiU + marginY,
                Width: entry.Width * pxPerCentiU,
                Height: entry.Height * pxPerCentiU,
                RotationDegrees: entry.Rotation / 100.0,
                Description: describeKey(index, entry, hand),
                Hand: hand,
                RotationOriginX: rotOx,
                RotationOriginY: rotOy));
        }
        return list;
    }
}
