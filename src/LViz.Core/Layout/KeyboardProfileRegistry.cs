namespace LViz.Core.Layout;

/// <summary>
/// Resolves a <see cref="IKeyboardProfile"/> by string id
/// (matching <see cref="Settings.UserSettings.Keyboard"/>). Bundled boards are
/// compiled in (<see cref="Bundled"/>); user layouts join through the
/// constructor or <see cref="SetUserProfiles"/>. The app has one instance,
/// built by the container (AppServices) with the layouts on disk; the
/// view-models receive it.
/// </summary>
public sealed class KeyboardProfileRegistry
{
    // Order matters: when no plugged-in keyboard breaks the tie, the key-count
    // auto-switch (FindByKeyCount) accepts the first bundled profile whose key
    // count matches the loaded keymap. Corne stays first so the as-shipped
    // default resolves without scanning.
    public static IReadOnlyList<IKeyboardProfile> Bundled { get; } =
    [
        new CorneProfile(),
        new ADuxProfile(),
        new KyriaProfile(),
        new Lily58Profile(),
        new SofleProfile(),
        new Go60Profile(),
        new Glove80Profile(),
    ];

    // User layouts from UserLayoutStore (loaded where the registry is built,
    // in AppServices), ids "user:<id>" so they can never collide with the
    // bundled ids above. SetUserProfiles swaps the whole array; it is never
    // mutated in place, so a reader on another thread sees the old or the
    // new list.
    private IKeyboardProfile[] _userProfiles = [];

    /// <summary>Only the bundled boards.</summary>
    public KeyboardProfileRegistry()
    {
    }

    /// <summary>The bundled boards plus these user layouts.</summary>
    public KeyboardProfileRegistry(IEnumerable<IKeyboardProfile> userProfiles) =>
        _userProfiles = [.. userProfiles];

    /// <summary>Replaces the user layouts offered alongside the bundled boards.</summary>
    public void SetUserProfiles(IEnumerable<IKeyboardProfile> profiles) =>
        _userProfiles = [.. profiles];

    /// <summary>Bundled boards first, then user layouts — the picker order.</summary>
    public IReadOnlyList<IKeyboardProfile> All => [.. Bundled, .. _userProfiles];

    public IKeyboardProfile Resolve(string id) =>
        FindById(id) ?? throw new ArgumentException($"Unknown keyboard id '{id}'", nameof(id));

    public bool TryResolve(string id, out IKeyboardProfile profile)
    {
        var found = FindById(id);
        profile = found!;
        return found is not null;
    }

    /// <summary>The profile with this id, or the Corne when the id is unknown,
    /// null or empty: the board the app starts on.</summary>
    public IKeyboardProfile ResolveOrDefault(string? id) =>
        !string.IsNullOrEmpty(id) && TryResolve(id, out var profile) ? profile : new CorneProfile();

    /// <summary>
    /// The profile with exactly <paramref name="keyCount"/> keys, or null.
    /// Several boards can share a key count (Sofle and GO60 both have 60), so
    /// the first one whose HID name matches one of
    /// <paramref name="attachedHidNames"/> (the product names of the keyboards
    /// plugged in) wins. Without such a match, a user layout wins over a
    /// bundled board — if the user installed a layout for a board, that's the
    /// one they mean — and bundled boards go in registry order.
    /// </summary>
    public IKeyboardProfile? FindByKeyCount(
        int keyCount, IReadOnlyCollection<string>? attachedHidNames = null)
    {
        IKeyboardProfile[] candidates =
            [.. _userProfiles.Where(p => p.KeyCount == keyCount), .. Bundled.Where(p => p.KeyCount == keyCount)];
        if (attachedHidNames is { Count: > 0 })
        {
            var attached = candidates.FirstOrDefault(p => attachedHidNames.Any(p.NameMatches));
            if (attached is not null)
                return attached;
        }
        return candidates.FirstOrDefault();
    }

    private IKeyboardProfile? FindById(string id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
