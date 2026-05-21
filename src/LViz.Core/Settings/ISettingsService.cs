namespace LViz.Core.Settings;

/// <summary>
/// Loads and saves user settings.
/// </summary>
public interface ISettingsService
{
    /// <summary>Loads settings from disk. Returns defaults if file is missing or corrupt.</summary>
    UserSettings Load();

    /// <summary>Saves settings to disk.</summary>
    void Save(UserSettings settings);
}
