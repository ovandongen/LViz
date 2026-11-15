namespace LViz.Core.Settings;

/// <summary>
/// Owns the user settings: the value in memory and the file behind it.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// The current settings. No disk access after the first read. Treat as
    /// immutable: change settings only through <see cref="Update"/>.
    /// </summary>
    UserSettings Current { get; }

    /// <summary>
    /// Applies <paramref name="mutate"/> to <see cref="Current"/> under a lock,
    /// stores and writes the result, then raises <see cref="Changed"/>.
    /// Never throws.
    /// </summary>
    void Update(Func<UserSettings, UserSettings> mutate, string subsystem);

    /// <summary>
    /// Raised after a change, with (old, new), on the thread that called
    /// <see cref="Update"/>.
    /// </summary>
    event Action<UserSettings, UserSettings>? Changed;
}
