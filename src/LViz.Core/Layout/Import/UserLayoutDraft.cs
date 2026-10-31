using System.Globalization;
using System.Text;
using LViz.Core.Layout.Dtsi;

namespace LViz.Core.Layout.Import;

/// <summary>
/// Turns the layout the layout wizard picked into a draft
/// <see cref="UserLayoutFile"/>: keys copied from an import with their hands
/// (<see cref="KeysFrom"/> — saved hands when the import carries them,
/// otherwise an automatic left/right split), a store-legal unique id derived
/// from a display name, normalised positions (<see cref="Create"/> shifts every
/// key so the rotated bounding box starts at (0, 0)), and the name suggestion
/// pre-filled from the scan. The automatic split is a midline through the
/// largest horizontal gap in the middle band of the board — a draggable split
/// line replaces it in a later version, which is why it lives behind its own
/// function. Pure functions over records; saving stays
/// <see cref="UserLayoutStore.Save"/>'s job.
/// </summary>
public static class UserLayoutDraft
{
    /// <summary>Id used when a name normalises to nothing usable.</summary>
    private const string FallbackId = "board";

    /// <summary>
    /// Smallest gap (centi-u) between neighbouring key centres the split
    /// trusts; anything narrower is key spacing, not a board split.
    /// </summary>
    private const double MinSplitGapCentiU = 25;

    /// <summary>
    /// Horizontal band, as fractions of the key-centre span, in which the split
    /// gap's midpoint must lie — thumb-cluster gaps at the board edges are
    /// ignored in favour of the actual halves.
    /// </summary>
    private const double BandStart = 0.3;
    private const double BandEnd = 0.7;

    /// <summary>
    /// File-system-safe layout id in the shape
    /// <see cref="UserLayoutStore.Save"/> insists on
    /// (<c>[a-z0-9][a-z0-9_-]*</c>): accents fold away
    /// (<c>Café Board</c> → <c>cafe-board</c>), every run of other characters
    /// collapses into a single <c>-</c>, and both ends are dash-trimmed. A name
    /// with nothing usable in it becomes <c>board</c>. When the result is
    /// already taken (ordinal comparison against
    /// <paramref name="existingIds"/>), <c>-2</c>, <c>-3</c>, … is appended
    /// until it is free.
    /// </summary>
    public static string MakeId(string name, IEnumerable<string> existingIds)
    {
        var slug = Slugify(name);

        var existing = new HashSet<string>(existingIds, StringComparer.Ordinal);
        if (!existing.Contains(slug))
            return slug;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{slug}-{suffix}";
            if (!existing.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// X (centi-u) of the midline that splits <paramref name="keys"/> into
    /// hands: the midpoint of the largest gap between neighbouring key-centre
    /// X values whose midpoint lies within the middle band of the board, or
    /// the centre of the span when no such gap reaches
    /// <see cref="MinSplitGapCentiU"/>. Key centres are taken after rotation
    /// about each key's own pivot, so thumbs count where they visually sit.
    /// An empty key list yields 0.
    /// </summary>
    public static int FindMidlineCentiU(IReadOnlyList<DtsiKeyPhysical> keys)
    {
        if (keys.Count == 0)
            return 0;

        var centres = keys.Select(key => UserLayoutGeometry.RotatedCenter(key).X).ToArray();
        Array.Sort(centres);

        var lo = centres[0];
        var width = centres[^1] - lo;
        var bandLo = lo + BandStart * width;
        var bandHi = lo + BandEnd * width;

        double? largestGap = null;
        double largestGapMidpoint = 0;
        for (var i = 0; i + 1 < centres.Length; i++)
        {
            var gap = centres[i + 1] - centres[i];
            var midpoint = centres[i] + gap / 2.0;
            if (midpoint < bandLo || midpoint > bandHi)
                continue;
            if (largestGap is null || gap > largestGap.Value)
            {
                largestGap = gap;
                largestGapMidpoint = midpoint;
            }
        }

        var midline = largestGap >= MinSplitGapCentiU
            ? largestGapMidpoint
            : lo + width / 2.0;
        return (int)Math.Round(midline);
    }

    /// <summary>
    /// <see cref="UserLayoutKey"/>s for an import, geometry unchanged: W/H/X/Y/R
    /// copied as-is. The hand comes from <see cref="ImportedLayout.Hands"/>
    /// when it is present and has one entry per key (a saved user layout asked
    /// for them); otherwise every key is re-handed by the midline rule —
    /// left when its rotated centre X falls left of
    /// <see cref="FindMidlineCentiU"/>, right otherwise.
    /// </summary>
    public static IReadOnlyList<UserLayoutKey> KeysFrom(ImportedLayout layout)
    {
        var savedHands = layout.Hands is not null && layout.Hands.Count == layout.Keys.Count
            ? layout.Hands
            : null;
        var midline = savedHands is null ? FindMidlineCentiU(layout.Keys) : 0;

        var keys = new List<UserLayoutKey>(layout.Keys.Count);
        for (var i = 0; i < layout.Keys.Count; i++)
        {
            var key = layout.Keys[i];
            keys.Add(new UserLayoutKey(
                key.X,
                key.Y,
                key.Width,
                key.Height,
                key.Rotation,
                key.Rx,
                key.Ry,
                savedHands?[i]
                    ?? (UserLayoutGeometry.RotatedCenter(key).X < midline ? Hand.Left : Hand.Right)));
        }
        return keys;
    }

    /// <summary>
    /// Draft layout file for the wizard to save: id from
    /// <see cref="MakeId"/>, trimmed name, trimmed <paramref name="hidName"/>
    /// (null when blank), current schema version, and
    /// <see cref="UserLayoutGeometry.Normalize"/> applied to
    /// <paramref name="keys"/> — every key shifted by the same offset so the
    /// rotated bounding box starts at (0, 0), hands kept as given.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is blank — every layout needs a display name.
    /// </exception>
    public static UserLayoutFile Create(
        IReadOnlyList<UserLayoutKey> keys,
        string name,
        string? hidName,
        IEnumerable<string> existingIds)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Layout name is blank.", nameof(name));

        var trimmedHidName = hidName?.Trim();
        return new UserLayoutFile(
            UserLayoutFile.CurrentSchemaVersion,
            MakeId(name, existingIds),
            name.Trim(),
            string.IsNullOrEmpty(trimmedHidName) ? null : trimmedHidName,
            UserLayoutGeometry.Normalize(keys));
    }

    /// <summary>
    /// Display name for the wizard's name field: the first non-blank of the
    /// firmware keyboard name, the shield folder, the config folder — trimmed.
    /// </summary>
    public static string SuggestName(string? keyboardName, string? shieldName, string folderName)
    {
        if (!string.IsNullOrWhiteSpace(keyboardName))
            return keyboardName.Trim();
        if (!string.IsNullOrWhiteSpace(shieldName))
            return shieldName.Trim();
        return folderName.Trim();
    }

    /// <summary>
    /// Decompose, drop the accents, and collapse everything that is not
    /// <c>a-z0-9</c> into single dashes (see <see cref="MakeId"/>).
    /// </summary>
    private static string Slugify(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder(decomposed.Length);
        var lastWasDash = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            var lowered = char.ToLowerInvariant(c);
            if (lowered is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(lowered);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                slug.Append('-');
                lastWasDash = true;
            }
        }

        var result = slug.ToString().Trim('-');
        return result.Length == 0 ? FallbackId : result;
    }
}
