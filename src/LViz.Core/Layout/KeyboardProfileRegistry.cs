namespace LViz.Core.Layout;

/// <summary>
/// Resolves a <see cref="IKeyboardProfile"/> by string id
/// (matching <see cref="Settings.UserSettings.Keyboard"/>). Bundled boards are
/// compiled in; user layouts join at startup via <see cref="SetUserProfiles"/>.
/// </summary>
public static class KeyboardProfileRegistry
{
    // Order matters: when no plugged-in keyboard breaks the tie, the key-count
    // auto-switch (FindByKeyCount) accepts the first bundled profile whose key
    // count matches the loaded keymap. Corne stays first so the as-shipped
    // default resolves without scanning.
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
    /// The profile with exactly <paramref name="keyCount"/> keys, or null.
    /// Several boards can share a key count (Sofle and GO60 both have 60), so
    /// the first one whose HID name matches one of
    /// <paramref name="attachedHidNames"/> (the product names of the keyboards
    /// plugged in) wins. Without such a match, a user layout wins over a
    /// bundled board — if the user installed a layout for a board, that's the
    /// one they mean — and bundled boards go in registry order.
    /// </summary>
    public static IKeyboardProfile? FindByKeyCount(
        int keyCount, IReadOnlyCollection<string>? attachedHidNames = null)
    {
        IKeyboardProfile[] candidates =
            [.. UserProfiles.Where(p => p.KeyCount == keyCount), .. Profiles.Where(p => p.KeyCount == keyCount)];
        if (attachedHidNames is { Count: > 0 })
        {
            var attached = candidates.FirstOrDefault(p => attachedHidNames.Any(p.NameMatches));
            if (attached is not null)
                return attached;
        }
        return candidates.FirstOrDefault();
    }

    private static IKeyboardProfile? FindById(string id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
