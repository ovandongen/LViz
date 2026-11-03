using System.Security.Cryptography;
using System.Text;
using LViz.Core.Persistence;
using Xunit;

namespace LViz.Tests.Persistence;

/// <summary>
/// <see cref="KeymapBackupStore"/>: one hash-named folder per keymap file, time-stamped
/// snapshots, <see cref="KeymapBackupStore.Keep"/> of them at most, newest first.
/// </summary>
public sealed class KeymapBackupStoreTests : IDisposable
{
    private static readonly DateTimeOffset Clock = new(2026, 3, 4, 5, 6, 7, 890, TimeSpan.Zero);
    private const string Stamp = "20260304-050607-890";

    private readonly string _root;
    private readonly string _src;

    public KeymapBackupStoreTests()
    {
        _root = TempDir("lviz-keymap-backups");
        _src = TempDir("lviz-keymap-src");
    }

    public void Dispose()
    {
        Delete(_root);
        Delete(_src);
    }

    [Fact]
    public void DefaultRoot_SitsUnderTheAppDataFolder()
    {
        Assert.EndsWith(Path.Combine("LViz", "keymap-backups"), KeymapBackupStore.DefaultRoot);
    }

    [Fact]
    public void Backup_CopiesBytesIntoHashFolder_AndWritesSource()
    {
        var keymap = Keymap("corne.keymap", "{ layers; };");
        var store = new KeymapBackupStore(_root, () => Clock);

        var backup = store.Backup(keymap);

        Assert.Equal(Path.Combine(Folder(keymap), Stamp + ".keymap"), backup);
        Assert.Equal(File.ReadAllBytes(keymap), File.ReadAllBytes(backup));
        Assert.Equal(
            new[] { Stamp + ".keymap", "source.txt" },
            Directory.GetFiles(Folder(keymap)).Select(Name).Order(StringComparer.Ordinal));
        Assert.Equal(Path.GetFullPath(keymap), File.ReadAllText(Path.Combine(Folder(keymap), "source.txt")));
        Assert.Equal(new[] { backup }, store.List(keymap));
    }

    [Fact]
    public void Backup_TwiceInSameMillisecond_AppendsCounters()
    {
        var keymap = Keymap("corne.keymap", "one");
        var store = new KeymapBackupStore(_root, () => Clock);

        var first = store.Backup(keymap);
        var second = store.Backup(keymap);
        var third = store.Backup(keymap);

        Assert.Equal(Stamp + ".keymap", Name(first));
        Assert.Equal(Stamp + "-2.keymap", Name(second));
        Assert.Equal(Stamp + "-3.keymap", Name(third));
        Assert.Equal(3, Directory.GetFiles(Folder(keymap), "*.keymap").Length);
    }

    [Fact]
    public void Backup_PastTheLimit_DropsTheOldest_AndListsNewestFirst()
    {
        var keymap = Keymap("corne.keymap", "v0");
        var step = 0;
        var store = new KeymapBackupStore(_root, () => Clock.AddMilliseconds(step++));

        var written = new List<string>();
        for (var i = 0; i < 22; i++)
        {
            File.WriteAllText(keymap, $"v{i}");
            written.Add(Name(store.Backup(keymap)));
        }

        var onDisk = Directory.GetFiles(Folder(keymap), "*.keymap").Select(Name).ToList();
        Assert.Equal(KeymapBackupStore.Keep, onDisk.Count);
        Assert.DoesNotContain(written[0], onDisk);
        Assert.DoesNotContain(written[1], onDisk);
        Assert.Contains(written[2], onDisk);

        Assert.Equal(written.Skip(2).Reverse().ToList(), store.List(keymap).Select(Name).ToList());
        Assert.Equal("v21", File.ReadAllText(store.List(keymap)[0]));
    }

    [Fact]
    public void List_SkipsFilesThatAreNotSnapshots()
    {
        var keymap = Keymap("corne.keymap", "one");
        var store = new KeymapBackupStore(_root, () => Clock);
        var backup = store.Backup(keymap);
        // A crashed AtomicFile write leaves `<snapshot>.keymap.tmp` in the folder
        // (see the store: it filters by extension by hand, not with a `*.keymap`
        // pattern, which on Win32 also matches through 8.3 short names).
        File.WriteAllText(backup + ".tmp", "half a snapshot");
        File.WriteAllText(Path.Combine(Folder(keymap), "notes.bak"), "junk");

        Assert.Equal(new[] { backup }, store.List(keymap));
    }

    [Fact]
    public void Backup_EachKeymapFile_GetsItsOwnFolder()
    {
        var left = Keymap("left.keymap", "L");
        var right = Keymap("right.keymap", "R");
        var store = new KeymapBackupStore(_root, () => Clock);

        store.Backup(left);
        store.Backup(right);

        Assert.Equal(2, Directory.GetDirectories(_root).Length);
        Assert.Equal("L", File.ReadAllText(store.List(left).Single()));
        Assert.Equal("R", File.ReadAllText(store.List(right).Single()));
    }

    [Fact]
    public void Backup_MissingSource_Throws_AndCreatesNothing()
    {
        var store = new KeymapBackupStore(_root, () => Clock);

        Assert.Throws<FileNotFoundException>(() => store.Backup(Path.Combine(_src, "gone.keymap")));

        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void List_KeymapNeverBackedUp_IsEmpty()
    {
        var store = new KeymapBackupStore(_root, () => Clock);

        Assert.Empty(store.List(Keymap("never.keymap", "x")));
    }

    [Fact]
    public void Backup_FromAnOffsetClock_SnapsToUtc()
    {
        var keymap = Keymap("corne.keymap", "one");
        var store = new KeymapBackupStore(_root, () => new DateTimeOffset(2026, 3, 4, 7, 6, 7, 890, TimeSpan.FromHours(2)));

        Assert.Equal(Stamp + ".keymap", Name(store.Backup(keymap)));
    }

    private string Keymap(string name, string content)
    {
        var path = Path.Combine(_src, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string Folder(string keymapPath) => Path.Combine(_root, Key(keymapPath));

    /// <summary>The folder a keymap's snapshots live in, recomputed the same way the store does it.</summary>
    private static string Key(string keymapPath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(keymapPath))))[..16];

    private static string Name(string path) => Path.GetFileName(path);

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
