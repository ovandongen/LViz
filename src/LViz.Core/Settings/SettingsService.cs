using System.Text.Json;
using LViz.Core.Diagnostics;
using LViz.Core.Persistence;

namespace LViz.Core.Settings;

/// <summary>
/// Persists user settings as JSON in the application data directory.
/// </summary>
public class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;

    public SettingsService()
        : this(GetDefaultPath())
    {
    }

    /// <summary>Constructor with explicit path, for testing.</summary>
    public SettingsService(string filePath)
    {
        _filePath = filePath;
    }

    public UserSettings Load()
    {
        if (!File.Exists(_filePath))
            return new UserSettings();

        string json;
        try
        {
            json = File.ReadAllText(_filePath);
        }
        catch (IOException ex)
        {
            DiagnosticLog.Warn("Settings", $"Read failed at {_filePath}: {ex.Message}; using defaults");
            return new UserSettings();
        }
        catch (UnauthorizedAccessException ex)
        {
            DiagnosticLog.Warn("Settings", $"Read denied at {_filePath}: {ex.Message}; using defaults");
            return new UserSettings();
        }

        UserSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Error("Settings", $"Parse failed at {_filePath}: {ex.Message}; using defaults");
            return new UserSettings();
        }

        if (parsed is null)
        {
            DiagnosticLog.Warn("Settings", $"Deserialized null at {_filePath}; using defaults");
            return new UserSettings();
        }

        if (parsed.SchemaVersion > UserSettings.CurrentSchemaVersion)
        {
            var backup = $"{_filePath}.v{parsed.SchemaVersion}.bak";
            try
            {
                File.Copy(_filePath, backup, overwrite: true);
                DiagnosticLog.Warn("Settings",
                    $"Settings file version {parsed.SchemaVersion} > supported {UserSettings.CurrentSchemaVersion}; backed up to {Path.GetFileName(backup)} and loading defaults");
            }
            catch (IOException ex)
            {
                DiagnosticLog.Error("Settings", $"Backup of newer settings file failed: {ex.Message}");
            }
            return new UserSettings();
        }

        if (parsed.SchemaVersion < UserSettings.CurrentSchemaVersion)
        {
            var migrated = MigrateSettings(parsed);
            try
            {
                Save(migrated);
            }
            catch (IOException ex)
            {
                DiagnosticLog.Warn("Settings", $"Persisting migrated settings failed: {ex.Message}");
            }
            return migrated;
        }

        return parsed;
    }

    /// <summary>
    /// Applies forward migrations from <c>parsed.SchemaVersion</c> up to
    /// <see cref="UserSettings.CurrentSchemaVersion"/>. Currently a no-op.
    /// </summary>
    private static UserSettings MigrateSettings(UserSettings parsed) =>
        parsed with { SchemaVersion = UserSettings.CurrentSchemaVersion };

    public void Save(UserSettings settings)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicFile.WriteAllText(_filePath, json);
    }

    private static string GetDefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "LViz", "settings.json");
    }
}
