using LViz.Core.ZmkConfig;

namespace LViz.Core.Persistence;

/// <summary>
/// Saves the module setup wizard's changes into a zmk-config folder on disk,
/// all-or-nothing. First every target is checked with the same conflict rule as
/// <see cref="KeymapFileSaver"/> — an existing file must still hold the text the
/// wizard read (ordinal) and a file the wizard saw missing must still be absent;
/// the wizard has no overwrite, on a conflict it reloads and checks again. If
/// anything mismatches, nothing is written and no backup is taken. Otherwise the
/// files go in order, each existing one first snapshotted through
/// <see cref="KeymapBackupStore"/> and written through <see cref="AtomicFile"/>,
/// with <see cref="FileRollback"/> over the whole batch: a failing write puts
/// every file back as it was (folders created on the way may stay).
/// IO errors propagate — the caller shows them.
/// </summary>
public static class ConfigFileSaver
{
    /// <summary>
    /// Checks every change against the disk, then writes them all.
    /// </summary>
    /// <returns><see cref="KeymapFileSaveResult.ChangedOnDisk"/> when any target
    /// no longer matches its baseline — with no file touched and no backup
    /// taken; <see cref="KeymapFileSaveResult.Saved"/> once all texts are on disk.</returns>
    /// <exception cref="ArgumentException">There are no changes to save.</exception>
    public static async Task<KeymapFileSaveResult> SaveAsync(
        string root,
        IReadOnlyList<ZmkConfigFileChange> changes,
        KeymapBackupStore backups,
        CancellationToken ct = default)
    {
        if (changes.Count == 0)
            throw new ArgumentException("There are no changes to save.", nameof(changes));

        var targets = changes
            .Select(change => Path.Combine(root, Path.Combine(change.RelativePath.Split('/'))))
            .ToList();

        // Check everything before writing anything: a conflict may not leave a
        // half-saved config behind, so no write starts until all files match.
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var exists = File.Exists(targets[i]);
            if (change.OldText is null)
            {
                // The wizard read no file here; one appearing since is a conflict.
                if (exists)
                    return KeymapFileSaveResult.ChangedOnDisk;
            }
            else
            {
                if (!exists)
                    return KeymapFileSaveResult.ChangedOnDisk;

                var current = await File.ReadAllTextAsync(targets[i], ct);
                if (!string.Equals(current, change.OldText, StringComparison.Ordinal))
                    return KeymapFileSaveResult.ChangedOnDisk;
            }
        }

        var rollback = FileRollback.Capture(targets);
        try
        {
            for (var i = 0; i < changes.Count; i++)
            {
                var target = targets[i];
                if (File.Exists(target))
                    backups.Backup(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await AtomicFile.WriteAllTextAsync(target, changes[i].NewText, ct);
            }
        }
        catch
        {
            rollback.Restore();
            throw;
        }

        return KeymapFileSaveResult.Saved;
    }
}
