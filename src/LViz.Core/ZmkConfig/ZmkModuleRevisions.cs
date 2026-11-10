namespace LViz.Core.ZmkConfig;

/// <summary>
/// Which revision of <c>zmk-raw-hid</c> and <c>zmk-hid-viz</c> to pin for a
/// config, and whether that pairing is known to work. Built by
/// <see cref="ZmkModuleRevisions.For"/>.
/// </summary>
/// <param name="RawHid">Revision for the <c>zmk-raw-hid</c> project.</param>
/// <param name="HidViz">Revision for the <c>zmk-hid-viz</c> project.</param>
/// <param name="Known">True when firmware tests confirmed this pairing for the config's ZMK.</param>
public sealed record ZmkModuleRevision(string RawHid, string HidViz, bool Known);

/// <summary>
/// The (small) table of which module revisions fit which ZMK. The pairing is
/// only known after Olaf's firmware tests, so today only MoErgo's <c>main</c>
/// is marked known and every row asks for <c>main</c>; a later card fills in
/// the rest of the rows.
/// </summary>
public static class ZmkModuleRevisions
{
    private const string DefaultRevision = "main";

    /// <summary>One matched ZMK and the module revisions it wants.</summary>
    private sealed record Row(string ZmkOwner, string ZmkRevision, string RawHid, string HidViz, bool Known);

    /// <summary>The table: edit only these rows as firmware tests come in.</summary>
    private static readonly Row[] Table =
    [
        // The MoErgo fork's main branch is the pairing tested in hardware.
        new("moergo-sc", "main", DefaultRevision, DefaultRevision, true),
    ];

    /// <summary>
    /// The module revisions for a config's ZMK owner/revision; unlisted ZMKs
    /// get <c>main</c>/<c>main</c> marked unknown.
    /// </summary>
    public static ZmkModuleRevision For(string? zmkOwner, string? zmkRevision)
    {
        foreach (var row in Table)
            if (string.Equals(zmkOwner, row.ZmkOwner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(zmkRevision, row.ZmkRevision, StringComparison.OrdinalIgnoreCase))
                return new ZmkModuleRevision(row.RawHid, row.HidViz, row.Known);

        return new ZmkModuleRevision(DefaultRevision, DefaultRevision, false);
    }
}
