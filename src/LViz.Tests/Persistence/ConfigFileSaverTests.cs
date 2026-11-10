using LViz.Core.Persistence;
using LViz.Core.ZmkConfig;
using LViz.Tests.ZmkConfig;
using Xunit;

namespace LViz.Tests.Persistence;

/// <summary>
/// <see cref="ConfigFileSaver"/>: the wizard's whole change list is checked against
/// the folder first — any mismatch refuses the save without touching a file —
/// then written in order with backups, and a failing write rolls every file back.
/// </summary>
public sealed class ConfigFileSaverTests
{
    [Fact]
    public async Task Save_AllTargetsMatch_WritesEveryFile_AndBacksUpTheExistingOne()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        var backupRoot = TempDir();
        try
        {
            var backups = new KeymapBackupStore(backupRoot);
            var west = Path.Combine(root, "config", "west.yml");
            var westOld = File.ReadAllText(west);

            var result = await ConfigFileSaver.SaveAsync(root, [
                new ZmkConfigFileChange("config/west.yml", westOld, "replaced west"),
                new ZmkConfigFileChange("config/new.conf", null, "CONFIG_NEW_MODULE=y\n"),
            ], backups);

            Assert.Equal(KeymapFileSaveResult.Saved, result);
            Assert.Equal("replaced west", File.ReadAllText(west));
            Assert.Equal("CONFIG_NEW_MODULE=y\n", File.ReadAllText(Path.Combine(root, "config", "new.conf")));

            // The file that existed is snapshotted with its old text; the new one isn't.
            var backup = Assert.Single(backups.List(west));
            Assert.Equal(westOld, File.ReadAllText(backup));
            Assert.Empty(backups.List(Path.Combine(root, "config", "new.conf")));
        }
        finally
        {
            Delete(root);
            Delete(backupRoot);
        }
    }

    [Fact]
    public async Task Save_FileMovedOnDisk_Refuses_WithoutWritingOrBackingUp()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        var backupRoot = TempDir();
        try
        {
            var backups = new KeymapBackupStore(backupRoot);
            var west = Path.Combine(root, "config", "west.yml");
            var westOld = File.ReadAllText(west);

            var result = await ConfigFileSaver.SaveAsync(root, [
                new ZmkConfigFileChange("config/west.yml", "something else", "replaced west"),
                new ZmkConfigFileChange("config/new.conf", null, "CONFIG_NEW_MODULE=y\n"),
            ], backups);

            Assert.Equal(KeymapFileSaveResult.ChangedOnDisk, result);
            // The check covers the whole list: nothing of this save happened at all.
            Assert.Equal(westOld, File.ReadAllText(west));
            Assert.False(File.Exists(Path.Combine(root, "config", "new.conf")));
            Assert.Empty(backups.List(west));
            Assert.Empty(Directory.GetFileSystemEntries(backupRoot));
        }
        finally
        {
            Delete(root);
            Delete(backupRoot);
        }
    }

    [Fact]
    public async Task Save_NewFileAppearedOnDisk_Refuses()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        var backupRoot = TempDir();
        try
        {
            var backups = new KeymapBackupStore(backupRoot);
            var conf = Path.Combine(root, "config", "cosmos82.conf");
            var confOld = File.ReadAllText(conf);

            // The wizard read cosmos82.conf as absent, but it is in the fixture.
            var result = await ConfigFileSaver.SaveAsync(
                root, [new ZmkConfigFileChange("config/cosmos82.conf", null, "CONFIG_NEW_MODULE=y\n")], backups);

            Assert.Equal(KeymapFileSaveResult.ChangedOnDisk, result);
            Assert.Equal(confOld, File.ReadAllText(conf));
            Assert.Empty(Directory.GetFileSystemEntries(backupRoot));
        }
        finally
        {
            Delete(root);
            Delete(backupRoot);
        }
    }

    [Fact]
    public async Task Save_SecondWriteFails_RollsBackTheFirstFile()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        var backupRoot = TempDir();
        try
        {
            // A plain file where the second change wants a folder: its write throws,
            // and the first change — already backed up and written — must be gone again.
            var blocker = Path.Combine(root, "sub");
            File.WriteAllText(blocker, "not a folder");

            var backups = new KeymapBackupStore(backupRoot);
            var west = Path.Combine(root, "config", "west.yml");
            var westOld = File.ReadAllText(west);

            // mkdir over an existing file throws IOException (EEXIST) on Linux/macOS and
            // UnauthorizedAccessException on Windows — all that matters is that the write
            // fails and the rollback runs.
            var ex = await Record.ExceptionAsync(() =>
                ConfigFileSaver.SaveAsync(root, [
                    new ZmkConfigFileChange("config/west.yml", westOld, "replaced west"),
                    new ZmkConfigFileChange("sub/x.conf", null, "CONFIG_X=y\n"),
                ], backups));
            Assert.NotNull(ex);
            Assert.True(ex is IOException or UnauthorizedAccessException, $"Unexpected failure: {ex}");

            Assert.Equal(westOld, File.ReadAllText(west));
            Assert.False(File.Exists(Path.Combine(root, "sub", "x.conf")));
        }
        finally
        {
            Delete(root);
            Delete(backupRoot);
        }
    }

    [Fact]
    public async Task Save_NoChanges_Throws()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        var backupRoot = TempDir();
        try
        {
            var backups = new KeymapBackupStore(backupRoot);

            await Assert.ThrowsAsync<ArgumentException>(
                () => ConfigFileSaver.SaveAsync(root, [], backups));
        }
        finally
        {
            Delete(root);
            Delete(backupRoot);
        }
    }

    [Fact]
    public async Task Save_LaterChangeMismatch_WritesNothing()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        var backupRoot = TempDir();
        try
        {
            var backups = new KeymapBackupStore(backupRoot);
            var west = Path.Combine(root, "config", "west.yml");
            var westOld = File.ReadAllText(west);
            var conf = Path.Combine(root, "config", "cosmos82.conf");

            // The first change is perfectly fine; the second one mismatches. The check
            // covers the whole list before the first write, so west stays untouched.
            var result = await ConfigFileSaver.SaveAsync(root, [
                new ZmkConfigFileChange("config/west.yml", westOld, "replaced west"),
                new ZmkConfigFileChange("config/cosmos82.conf", "not what is on disk", "replaced conf"),
            ], backups);

            Assert.Equal(KeymapFileSaveResult.ChangedOnDisk, result);
            Assert.Equal(westOld, File.ReadAllText(west));
            Assert.DoesNotContain("replaced conf", File.ReadAllText(conf));
            Assert.Empty(Directory.GetFileSystemEntries(backupRoot));
        }
        finally
        {
            Delete(root);
            Delete(backupRoot);
        }
    }

    [Fact]
    public async Task Save_BaselineFileMissing_Refuses()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        var backupRoot = TempDir();
        try
        {
            var backups = new KeymapBackupStore(backupRoot);
            var conf = Path.Combine(root, "config", "cosmos82.conf");
            var confOld = File.ReadAllText(conf);
            File.Delete(conf);

            // A file that vanished since the wizard read it is a conflict too.
            var result = await ConfigFileSaver.SaveAsync(
                root, [new ZmkConfigFileChange("config/cosmos82.conf", confOld, "replaced conf")], backups);

            Assert.Equal(KeymapFileSaveResult.ChangedOnDisk, result);
            Assert.False(File.Exists(conf));
            Assert.Empty(Directory.GetFileSystemEntries(backupRoot));
        }
        finally
        {
            Delete(root);
            Delete(backupRoot);
        }
    }

    [Fact]
    public async Task Save_NewFileInMissingFolder_CreatesTheFolder()
    {
        var root = ZmkConfigFixtures.CopyToTemp("cosmos82");
        var backupRoot = TempDir();
        try
        {
            var backups = new KeymapBackupStore(backupRoot);

            var result = await ConfigFileSaver.SaveAsync(
                root, [new ZmkConfigFileChange("modules/newmodule.conf", null, "CONFIG_NEW_MODULE=y\n")], backups);

            Assert.Equal(KeymapFileSaveResult.Saved, result);
            Assert.Equal(
                "CONFIG_NEW_MODULE=y\n",
                File.ReadAllText(Path.Combine(root, "modules", "newmodule.conf")));
            // Nothing existed to snapshot.
            Assert.Empty(Directory.GetFileSystemEntries(backupRoot));
        }
        finally
        {
            Delete(root);
            Delete(backupRoot);
        }
    }

    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lviz-config-save-backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
