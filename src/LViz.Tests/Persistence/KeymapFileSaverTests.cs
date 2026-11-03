using System.Text;
using LViz.Core.Persistence;
using Xunit;

namespace LViz.Tests.Persistence;

/// <summary>
/// <see cref="KeymapFileSaver"/>: a file that still matches what the editor loaded is
/// snapshotted and overwritten; one that moved on disk is left alone until the caller
/// says overwrite. A brand-new file has nothing to snapshot.
/// </summary>
public sealed class KeymapFileSaverTests : IDisposable
{
    private static readonly DateTimeOffset Clock = new(2026, 3, 4, 5, 6, 7, 890, TimeSpan.Zero);

    private readonly string _dir;
    private readonly string _backupRoot;
    private readonly KeymapBackupStore _backups;

    public KeymapFileSaverTests()
    {
        _dir = TempDir("lviz-keymap-save");
        _backupRoot = TempDir("lviz-keymap-save-backups");
        _backups = new KeymapBackupStore(_backupRoot, () => Clock);
    }

    public void Dispose()
    {
        Delete(_dir);
        Delete(_backupRoot);
    }

    [Fact]
    public async Task Save_MatchingBaseline_WritesNewText_AndBacksUpTheOldOne()
    {
        var path = OnDisk("corne.keymap", "old text");

        var result = await KeymapFileSaver.SaveAsync(path, "old text", "new text", overwrite: false, _backups);

        Assert.Equal(KeymapFileSaveResult.Saved, result);
        Assert.Equal("new text", File.ReadAllText(path));
        var backup = Assert.Single(_backups.List(path));
        Assert.Equal("old text", File.ReadAllText(backup));
    }

    [Fact]
    public async Task Save_FileEditedOnDisk_Refuses_WithoutWritingOrBackingUp()
    {
        var path = OnDisk("corne.keymap", "edited on disk");

        var result = await KeymapFileSaver.SaveAsync(path, "what the editor loaded", "new text", overwrite: false, _backups);

        Assert.Equal(KeymapFileSaveResult.ChangedOnDisk, result);
        Assert.Equal("edited on disk", File.ReadAllText(path));
        Assert.Empty(_backups.List(path));
        Assert.Empty(Directory.GetFileSystemEntries(_backupRoot));
    }

    [Fact]
    public async Task Save_Overwrite_WritesEvenAfterAnExternalEdit_AndBacksUpDiskText()
    {
        var path = OnDisk("corne.keymap", "edited on disk");

        var result = await KeymapFileSaver.SaveAsync(path, "what the editor loaded", "new text", overwrite: true, _backups);

        Assert.Equal(KeymapFileSaveResult.Saved, result);
        Assert.Equal("new text", File.ReadAllText(path));
        Assert.Equal("edited on disk", File.ReadAllText(Assert.Single(_backups.List(path))));
    }

    [Fact]
    public async Task Save_MissingFile_Refuses_AndOverwriteCreatesItWithoutBackup()
    {
        var path = Path.Combine(_dir, "fresh.keymap");

        var refused = await KeymapFileSaver.SaveAsync(path, "baseline", "new text", overwrite: false, _backups);

        Assert.Equal(KeymapFileSaveResult.ChangedOnDisk, refused);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFileSystemEntries(_backupRoot));

        var saved = await KeymapFileSaver.SaveAsync(path, "baseline", "new text", overwrite: true, _backups);

        Assert.Equal(KeymapFileSaveResult.Saved, saved);
        Assert.Equal("new text", File.ReadAllText(path));
        Assert.Empty(_backups.List(path));
        Assert.Empty(Directory.GetFileSystemEntries(_backupRoot));
    }

    [Fact]
    public async Task Save_WritesUtf8WithoutABom()
    {
        const string newText = "// toetsen — layered ✓\n&{/} { };";
        var path = OnDisk("corne.keymap", "base");
        var expected = Encoding.UTF8.GetBytes(newText);

        var result = await KeymapFileSaver.SaveAsync(path, "base", newText, overwrite: false, _backups);

        Assert.Equal(KeymapFileSaveResult.Saved, result);
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Save_CancelledToken_ThrowsBeforeWriting()
    {
        var path = OnDisk("corne.keymap", "base");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => KeymapFileSaver.SaveAsync(
                path, "base", "new text", overwrite: true, _backups, new CancellationToken(canceled: true)));

        Assert.Equal("base", File.ReadAllText(path));
    }

    [Fact]
    public async Task Save_UnwritableTarget_Throws_WithoutBackup()
    {
        // The directory for the keymap does not exist: AtomicFile surfaces the IO
        // error to the caller, which is what shows it. Nothing is backed up, because
        // there was no file to back up.
        var path = Path.Combine(_dir, "missing-folder", "corne.keymap");

        await Assert.ThrowsAnyAsync<IOException>(
            () => KeymapFileSaver.SaveAsync(path, "baseline", "new text", overwrite: true, _backups));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFileSystemEntries(_backupRoot));
    }

    private string OnDisk(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string TempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
