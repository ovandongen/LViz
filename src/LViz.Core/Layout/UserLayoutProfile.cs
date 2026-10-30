using LViz.Core.Layout.Dtsi;

namespace LViz.Core.Layout;

/// <summary>
/// <see cref="IKeyboardProfile"/> generated from a user-made
/// <see cref="UserLayoutFile"/> so imported layouts render exactly like the
/// bundled dtsi boards: same pixel conversion (0.6 px per centi-u), same
/// margins, same pivot semantics — the hand just comes from the file instead
/// of a midline cutoff.
/// <para>
/// Imported coordinates may be negative (rotated thumbs swing above/left of
/// the origin, and dtsi exports carry <c>y: -0.0</c>-style rows), so the
/// constructor translates the whole board until the rotated bounding box
/// starts at the default insets, and sizes the canvas from the box.
/// </para>
/// </summary>
public sealed class UserLayoutProfile : IKeyboardProfile
{
    /// <summary>
    /// Normalised bounds minimum, equal to <see cref="DtsiBackedLayoutBuilder"/>'s
    /// default margins — the point of the translation is only to move the board
    /// onto them, not to re-space it. Also reused as the right/bottom canvas
    /// gutter, mirroring the fixed-canvas bundled boards.
    /// </summary>
    private const double InsetX = 30;
    private const double InsetY = 60;

    public UserLayoutProfile(UserLayoutFile file)
    {
        Source = file;
        Id = "user:" + file.Id;
        DisplayName = $"{file.Name} ({file.Keys.Count})";
        KeyCount = file.Keys.Count;

        var dtsiKeys = new List<DtsiKeyPhysical>(file.Keys.Count);
        foreach (var key in file.Keys)
            dtsiKeys.Add(new DtsiKeyPhysical(key.W, key.H, key.X, key.Y, key.R, key.Rx, key.Ry));

        var built = DtsiBackedLayoutBuilder.BuildFromKeys(
            dtsiKeys,
            handOf: (index, _) => file.Keys[index].Hand,
            describeKey: (index, _, _) => $"Key {index}");

        var (minX, minY, _, _) = built.RotatedBounds();
        double dx = InsetX - minX, dy = InsetY - minY;
        // Lifted arithmetic keeps a null pivot null; a set pivot translates with
        // its key so the rotation geometry (pivot-to-corner offsets) is intact.
        Keys = [.. built.Select(k => k with
        {
            X = k.X + dx,
            Y = k.Y + dy,
            RotationOriginX = k.RotationOriginX + dx,
            RotationOriginY = k.RotationOriginY + dy,
        })];

        var (_, _, maxX, maxY) = Keys.RotatedBounds();
        CanvasWidth = Math.Ceiling(maxX + InsetX);
        CanvasHeight = Math.Ceiling(maxY + InsetX);
    }

    /// <summary>The layout this profile was built from, unchanged.</summary>
    public UserLayoutFile Source { get; }

    public string Id { get; }
    public string DisplayName { get; }
    public int KeyCount { get; }
    public double CanvasWidth { get; }
    public double CanvasHeight { get; }
    public IReadOnlyList<KeyPosition> Keys { get; }

    public bool MatchesHidDevice(int vendorId, int productId, string? productName) =>
        vendorId == ZmkHidIds.VendorId
        && productId == ZmkHidIds.ProductId
        && NameMatches(productName);

    /// <summary>
    /// Case-insensitive substring match against <see cref="UserLayoutFile.HidName"/>,
    /// falling back to the display name while no HID hint is set (a freshly
    /// drafted layout has none, and its name is the best handle available).
    /// </summary>
    public bool NameMatches(string? productName)
    {
        if (productName is null)
            return false;
        var needle = string.IsNullOrWhiteSpace(Source.HidName) ? Source.Name : Source.HidName;
        return productName.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
