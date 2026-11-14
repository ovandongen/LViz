using System.Text.Json;
using LViz.Core.Diagnostics;
using LViz.Core.Persistence;

namespace LViz.Core.Firmware;

/// <summary>One recorded flash: which commit the half was built from and when.</summary>
/// <param name="CommitSha">The commit the half was last flashed with, lower-cased.</param>
/// <param name="FlashedAt">When the flash was recorded, in UTC.</param>
public sealed record FirmwareFlashRecord(string CommitSha, DateTimeOffset FlashedAt);

/// <summary>
/// Remembers, per zmk-config and per half, the commit the last recorded flash
/// came from — the number the peripheral check (
/// <see cref="FirmwareChangeRule.PeripheralNeedsFlash"/>) compares against.
/// Only flashes of a GitHub build are recorded (they carry a commit SHA);
/// a config with no record simply means "unknown" and leaves the peripheral
/// unticked.
///
/// <para>
/// One small JSON file (<see cref="DefaultPath"/>, override via the
/// constructor) keyed by the config key the live-features hint already uses:
/// <c>github:{owner}/{repo}:{branch}</c> or the full local root path. The
/// store treats it as opaque text, compared ordinally.
/// </para>
///
/// <para>
/// Reading is tolerant by design — a missing file, unparseable JSON, a
/// foreign <c>version</c> or an IO failure all read as "no history" (the
/// latter logged, never thrown) so a broken file can never stop a check;
/// the next <see cref="Record"/> rewrites the file. Writing goes through
/// <see cref="AtomicFile"/>, and its IO errors propagate for the caller to
/// show. One instance per app; a lock covers read-modify-write.
/// </para>
/// </summary>
public sealed class FirmwareFlashHistory(string path, Func<DateTimeOffset>? now = null)
{
    /// <summary>The only file <c>version</c> this build reads; anything else reads as empty.</summary>
    private const int FileVersion = 1;

    private const string LogTag = "Firmware";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();

    private readonly Func<DateTimeOffset> _now = now is not null ? now : static () => DateTimeOffset.UtcNow;

    /// <summary>Where the app keeps the history: <c>ApplicationData/LViz/firmware/flash-history.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LViz", "firmware", "flash-history.json");

    /// <summary>The file this store reads and writes.</summary>
    public string FilePath { get; } = path;

    /// <summary>
    /// The last recorded flash of this half of this config, or null when
    /// there is no record — no file, no entry, or content this build can't
    /// read. Reads the file on every call; it is tiny. Never throws for
    /// content.
    /// </summary>
    public FirmwareFlashRecord? Get(string configKey, FirmwareHalfRole role)
    {
        if (string.IsNullOrWhiteSpace(configKey))
            return null;

        lock (_gate)
        {
            var configs = ReadUnlocked();
            return configs.TryGetValue(configKey, out var halves) &&
                   halves.TryGetValue(RoleName(role), out var record)
                ? record
                : null;
        }
    }

    /// <summary>
    /// Records that this half of this config was flashed from
    /// <paramref name="commitSha"/> (stored lower-cased) at <c>now()</c>
    /// (stored UTC), then rewrites the file. A file this store cannot read
    /// counts as empty and is replaced by what this leaves behind.
    /// </summary>
    /// <exception cref="ArgumentException">The config key or the SHA is blank.</exception>
    /// <exception cref="IOException">The file or its folder could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file or its folder is not writable.</exception>
    public void Record(string configKey, FirmwareHalfRole role, string commitSha)
    {
        if (string.IsNullOrWhiteSpace(configKey))
            throw new ArgumentException("Config key is blank.", nameof(configKey));
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new ArgumentException("Commit SHA is blank.", nameof(commitSha));

        lock (_gate)
        {
            var configs = ReadUnlocked();
            if (!configs.TryGetValue(configKey, out var halves))
                halves = configs[configKey] = new Dictionary<string, FirmwareFlashRecord>(StringComparer.OrdinalIgnoreCase);
            halves[RoleName(role)] = new FirmwareFlashRecord(
                commitSha.ToLowerInvariant(), _now().ToUniversalTime());

            var folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            var json = JsonSerializer.Serialize(
                new StoredHistory { Version = FileVersion, Configs = ToStored(configs) }, JsonOptions);
            AtomicFile.WriteAllText(FilePath, json);
        }
    }

    /// <summary>
    /// The file as a config-keyed model; every failure that only concerns
    /// content (unreadable file, broken JSON, foreign version, half an
    /// entry) yields an empty or pruned model instead of throwing.
    /// Config keys compare ordinally; role names case-insensitively.
    /// </summary>
    private Dictionary<string, Dictionary<string, FirmwareFlashRecord>> ReadUnlocked()
    {
        var configs = new Dictionary<string, Dictionary<string, FirmwareFlashRecord>>(StringComparer.Ordinal);
        if (!File.Exists(FilePath))
            return configs;

        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(LogTag, $"{FilePath}: {ex.Message}");
            return configs;
        }

        StoredHistory? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredHistory>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Warn(LogTag, $"{FilePath}: invalid JSON: {ex.Message}");
            return configs;
        }

        if (stored is null || stored.Version != FileVersion || stored.Configs is null)
            return configs;

        foreach (var (key, halves) in stored.Configs)
        {
            if (string.IsNullOrEmpty(key) || halves is null)
                continue;

            var clean = new Dictionary<string, FirmwareFlashRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var (roleName, flash) in halves)
            {
                if (flash is null || string.IsNullOrWhiteSpace(flash.Commit) || flash.FlashedAt is null)
                    continue;
                if (!TryRoleName(roleName, out var role))
                    continue;
                clean[RoleName(role)] = new FirmwareFlashRecord(flash.Commit, flash.FlashedAt.Value);
            }

            if (clean.Count > 0)
                configs[key] = clean;
        }

        return configs;
    }

    private static Dictionary<string, Dictionary<string, StoredFlash?>?> ToStored(
        Dictionary<string, Dictionary<string, FirmwareFlashRecord>> configs)
    {
        var stored = new Dictionary<string, Dictionary<string, StoredFlash?>?>(StringComparer.Ordinal);
        foreach (var (key, halves) in configs)
        {
            var inner = new Dictionary<string, StoredFlash?>();
            foreach (var half in halves)
            {
                inner[half.Key] = new StoredFlash
                {
                    Commit = half.Value.CommitSha,
                    FlashedAt = half.Value.FlashedAt,
                };
            }

            stored[key] = inner;
        }

        return stored;
    }

    /// <summary>On-disk role names, lower case.</summary>
    private static string RoleName(FirmwareHalfRole role) => role switch
    {
        FirmwareHalfRole.Central => "central",
        FirmwareHalfRole.Peripheral => "peripheral",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown half role."),
    };

    /// <summary>Reads role names tolerantly, like the layout store reads hands.</summary>
    private static bool TryRoleName(string? name, out FirmwareHalfRole role)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case "central":
                role = FirmwareHalfRole.Central;
                return true;
            case "peripheral":
                role = FirmwareHalfRole.Peripheral;
                return true;
            default:
                role = default;
                return false;
        }
    }

    /// <summary>
    /// Wire shape of the history file; property order is the on-disk order.
    /// Kept separate from <see cref="FirmwareFlashRecord"/> so a missing
    /// value stays observable instead of silently defaulting.
    /// </summary>
    private sealed class StoredHistory
    {
        public int Version { get; init; }
        public Dictionary<string, Dictionary<string, StoredFlash?>?>? Configs { get; init; }
    }

    private sealed class StoredFlash
    {
        public string? Commit { get; init; }
        public DateTimeOffset? FlashedAt { get; init; }
    }
}
