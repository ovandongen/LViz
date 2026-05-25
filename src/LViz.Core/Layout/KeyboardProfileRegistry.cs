namespace LViz.Core.Layout;

/// <summary>
/// Resolves a <see cref="IKeyboardProfile"/> by string id
/// (matching <see cref="Settings.UserSettings.Keyboard"/>).
/// </summary>
public static class KeyboardProfileRegistry
{
    // Order matters: AutoSwitchEngine's key-count fallback accepts the first
    // profile whose key count matches the loaded keymap. Corne stays first
    // so the as-shipped default resolves without scanning.
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

    public static IReadOnlyList<IKeyboardProfile> All => Profiles;

    public static IKeyboardProfile Resolve(string id) =>
        Profiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown keyboard id '{id}'", nameof(id));

    public static bool TryResolve(string id, out IKeyboardProfile profile)
    {
        var found = Profiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        profile = found!;
        return found is not null;
    }
}
