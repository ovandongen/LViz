namespace LViz.Core.Layout;

/// <summary>
/// Resolves a <see cref="IKeyboardProfile"/> by string id
/// (matching <see cref="Settings.UserSettings.Keyboard"/>). Bundled boards are
/// compiled in; user layouts join at startup via <see cref="SetUserProfiles"/>.
/// </summary>
public static class KeyboardProfileRegistry
{
    // Order matters: the key-count auto-switch (FindByKeyCount) accepts the
    // first bundled profile whose key count matches the loaded keymap. Corne
    // stays first so the as-shipped default resolves without scanning.
    private static readonly IKeyboardProfile[] Profiles =
    [
        new CorneProfile(),
        new ADuxProfile(),
        new KyriaProfile(),
        new Lily58Profile(),
        new SofleProfile(),
        new Go60Profile(),
        new Glove80Profile(),
    ];

    // User layouts loaded from UserLayoutStore at startup (App.axaml.cs),
    // ids "user:<id>" so they can never collide with the bundled ids above.
    // SetUserProfiles swaps the whole array; it is never mutated in place.
    private static IKeyboardProfile[] UserProfiles = [];

    /// <summary>Replaces the user layouts offered alongside the bundled boards.</summary>
    public static void SetUserProfiles(IEnumerable<IKeyboardProfile> profiles) =>
        UserProfiles = [.. profiles];

    /// <summary>Bundled boards first, then user layouts — the picker order.</summary>
    public static IReadOnlyList<IKeyboardProfile> All => [.. Profiles, .. UserProfiles];

    public static IKeyboardProfile Resolve(string id) =>
        FindById(id) ?? throw new ArgumentException($"Unknown keyboard id '{id}'", nameof(id));

    public static bool TryResolve(string id, out IKeyboardProfile profile)
    {
        var found = FindById(id);
        profile = found!;
        return found is not null;
    }

    /// <summary>
    /// First profile with exactly <paramref name="keyCount"/> keys, or null.
    /// A user layout wins over a bundled board with the same key count — if
    /// the user installed a layout for a board, that's the one they mean.
    /// </summary>
    public static IKeyboardProfile? FindByKeyCount(int keyCount) =>
        UserProfiles.FirstOrDefault(p => p.KeyCount == keyCount)
        ?? Profiles.FirstOrDefault(p => p.KeyCount == keyCount);

    private static IKeyboardProfile? FindById(string id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
