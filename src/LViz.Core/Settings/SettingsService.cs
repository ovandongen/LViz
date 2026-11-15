using System.Text.Json;
using LViz.Core.Diagnostics;
using LViz.Core.Persistence;

namespace LViz.Core.Settings;

/// <summary>
/// Keeps user settings in memory and persists them as JSON in the application
/// data directory. The file is read once, on the first access; every change
/// goes through <see cref="Update"/>.
/// </summary>
public class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private volatile UserSettings? _current;

    public SettingsService()
        : this(GetDefaultPath())
    {
    }

    /// <summary>Constructor with explicit path, for testing.</summary>
    public SettingsService(string filePath)
    {
        _filePath = filePath;
    }

    public event Action<UserSettings, UserSettings>? Changed;

    public UserSettings Current
    {
        get
        {
            var cached = _current;
            if (cached is not null)
                return cached;

            lock (_gate)
            {
                cached = _current;
                if (cached is null)
                {
                    cached = Read();
                    _current = cached;
                }
                return cached;
            }
        }
    }

    public void Update(Func<UserSettings, UserSettings> mutate, string subsystem)
    {
        UserSettings previous;
        UserSettings next;

        try
        {
            lock (_gate)
            {
                previous = Current;

                try
                {
                    next = mutate(previous);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warn(subsystem, $"Persist settings failed: {ex.Message}");
                    return;
                }

                if (next is null || ReferenceEquals(next, previous))
                    return;

                // The value is stored before the write so a failed write costs
                // only persistence: the change lives for the rest of the session.
                _current = next;
                try
                {
                    Write(next);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warn(subsystem, $"Persist settings failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // Update promises it never throws, and the first read of a damaged
            // settings file can fail with something Read() does not turn into
            // defaults (a denied backup, a denied migrated write).
            DiagnosticLog.Warn(subsystem, $"Persist settings failed: {ex.Message}");
            return;
        }

        RaiseChanged(previous, next);
    }

    /// <summary>
    /// Runs every handler even when one throws, so a broken subscriber cannot
    /// keep the others from learning about the change. Outside the lock, so a
    /// handler is free to read or update the settings.
    /// </summary>
    private void RaiseChanged(UserSettings previous, UserSettings next)
    {
        var handlers = Changed;
        if (handlers is null)
            return;

        foreach (var entry in handlers.GetInvocationList())
        {
            try
            {
                ((Action<UserSettings, UserSettings>)entry)(previous, next);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn("Settings", $"Changed handler failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reads the file and interprets it: defaults for a missing, unreadable,
    /// unparseable or newer-schema file, and the migrated value for an older one.
    /// </summary>
    private UserSettings Read()
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
                Write(migrated);
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
    /// <see cref="UserSettings.CurrentSchemaVersion"/>.
    /// </summary>
    private static UserSettings MigrateSettings(UserSettings parsed)
    {
        // v1 → v2: RecentKeymapPaths was introduced; seed each profile's list
        // with its single last-used LayoutJsonPaths entry. A profile that
        // already carries a recents list keeps it (makes the step idempotent).
        var recent = new Dictionary<string, List<string>>(parsed.RecentKeymapPaths);
        foreach (var (profileId, path) in parsed.LayoutJsonPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || recent.ContainsKey(profileId))
                continue;
            recent[profileId] = new List<string> { path };
        }

        return parsed with
        {
            SchemaVersion = UserSettings.CurrentSchemaVersion,
            RecentKeymapPaths = recent,
        };
    }

    private void Write(UserSettings settings)
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
