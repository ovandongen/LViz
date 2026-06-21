namespace LViz.Core.Layout;

/// <summary>
/// Single source of truth for "which boards are Moergo". Moergo boards are the
/// only ones that accept the Layout Editor's <c>.json</c> export (in addition to
/// <c>.keymap</c>); every other board is <c>.keymap</c>-only. Used by the JSON
/// loader (to validate the <c>keyboard</c> field) and by the load dispatcher (to
/// gate JSON on the active profile).
/// </summary>
public static class MoergoBoards
{
    /// <summary><see cref="Glove80Profile"/>'s <c>Id</c>.</summary>
    public const string Glove80ProfileId = "Glove80";

    /// <summary><see cref="Go60Profile"/>'s <c>Id</c> (note the upper-case).</summary>
    public const string Go60ProfileId = "GO60";

    /// <summary>True when <paramref name="profileId"/> is a Moergo board profile.</summary>
    public static bool IsMoergoProfileId(string profileId) =>
        profileId is Glove80ProfileId or Go60ProfileId;

    /// <summary>
    /// True when <paramref name="keyboard"/> is a <c>keyboard</c> field value the
    /// Moergo Layout Editor emits (<c>glove80</c> / <c>go60</c>).
    /// </summary>
    public static bool IsMoergoKeyboardField(string? keyboard) =>
        keyboard is not null
        && (keyboard.Equals("glove80", StringComparison.OrdinalIgnoreCase)
            || keyboard.Equals("go60", StringComparison.OrdinalIgnoreCase));
}
