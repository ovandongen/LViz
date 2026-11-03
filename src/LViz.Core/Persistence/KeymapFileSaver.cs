namespace LViz.Core.Persistence;

/// <summary>Outcome of <see cref="KeymapFileSaver.SaveAsync"/>.</summary>
public enum KeymapFileSaveResult
{
    /// <summary>The new text is on disk; the previous content was snapshotted when there was one.</summary>
    Saved,

    /// <summary>The file on disk is no longer what the editor loaded; nothing was written.</summary>
    ChangedOnDisk,
}

/// <summary>
/// Saves a keymap that was opened from a local file. Same conflict rule as the
/// GitHub save: when the file on disk no longer matches the text the editor
/// loaded, the save is refused and the caller offers reload or overwrite.
/// Otherwise the file as it stands is snapshotted via <see cref="KeymapBackupStore"/>
/// first and the new text goes out through <see cref="AtomicFile"/>.
/// IO errors propagate — the caller shows them.
/// </summary>
public static class KeymapFileSaver
{
    public static async Task<KeymapFileSaveResult> SaveAsync(
        string path,
        string baselineText,
        string newText,
        bool overwrite,
        KeymapBackupStore backups,
        CancellationToken ct = default)
    {
        var exists = File.Exists(path);

        if (!overwrite)
        {
            // A vanished file is a conflict too: the editor's baseline describes a
            // file nobody has looked at since, so nothing here may be assumed.
            if (!exists)
                return KeymapFileSaveResult.ChangedOnDisk;

            var current = await File.ReadAllTextAsync(path, ct);
            if (!string.Equals(current, baselineText, StringComparison.Ordinal))
                return KeymapFileSaveResult.ChangedOnDisk;
        }

        if (exists)
            backups.Backup(path);

        await AtomicFile.WriteAllTextAsync(path, newText, ct);
        return KeymapFileSaveResult.Saved;
    }
}
