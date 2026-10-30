namespace LViz.Core.Layout;

/// <summary>
/// One physical key of a user layout, stored in <b>ZMK units</b>: centi-u
/// (1u = 100) for <see cref="X"/>/<see cref="Y"/>/<see cref="W"/>/<see cref="H"/>/
/// <see cref="Rx"/>/<see cref="Ry"/> and centi-degrees for <see cref="R"/> —
/// exactly the shape of a ZMK <c>&amp;key_physical_attrs</c> entry, so importing a
/// dtsi and writing it back out is lossless. Canvas-unit conversion happens when
/// the layout becomes a profile, not here.
/// </summary>
/// <param name="X">Upper-left X in centi-u.</param>
/// <param name="Y">Upper-left Y in centi-u.</param>
/// <param name="W">Width in centi-u. Must be positive.</param>
/// <param name="H">Height in centi-u. Must be positive.</param>
/// <param name="R">Rotation in centi-degrees (9000 = 90°).</param>
/// <param name="Rx">Rotation pivot X in centi-u. Same frame as <see cref="X"/>.</param>
/// <param name="Ry">Rotation pivot Y in centi-u. Same frame as <see cref="Y"/>.</param>
/// <param name="Hand">
/// Which half of a split board the key sits on. Carried per key because a user
/// layout has no compiled profile to infer it from.
/// </param>
public sealed record UserLayoutKey(int X, int Y, int W, int H, int R, int Rx, int Ry, Hand Hand);

/// <summary>
/// On-disk model of a user-made board layout: one <c>&lt;id&gt;.layout.json</c>
/// file in the <c>layouts</c> folder next to <c>settings.json</c>, written by the
/// layout wizard and read back by <see cref="UserLayoutStore"/>.
/// </summary>
/// <param name="SchemaVersion">On-disk schema version; must be
/// <see cref="CurrentSchemaVersion"/> for the file to load.</param>
/// <param name="Id">Stable, file-system-safe id: <c>[a-z0-9][a-z0-9_-]*</c>. Must
/// equal the file-name stem; a file whose name differs is skipped on load.</param>
/// <param name="Name">Display name shown to the user.</param>
/// <param name="HidName">Name substring matching the board's USB product name, or
/// null when the layout has no keyboard attached to it yet.</param>
/// <param name="Keys">Every key on the board, in the order the wizard laid them
/// out.</param>
/// <remarks>
/// Like <see cref="Models.KeyBinding"/>, record equality compares
/// <see cref="Keys"/> by reference, not by contents — two files parsed from
/// identical JSON are <i>not</i> <c>Equal</c>. Compare the id, then the keys as a
/// sequence.
/// </remarks>
public sealed record UserLayoutFile(
    int SchemaVersion,
    string Id,
    string Name,
    string? HidName,
    IReadOnlyList<UserLayoutKey> Keys)
{
    /// <summary>
    /// Schema version this build writes, and the only one it reads. A file with
    /// any other version is skipped rather than guessed at.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
