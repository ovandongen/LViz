using System.IO.Compression;
using System.Text;
using LViz.Core.Firmware;
using Xunit;

namespace LViz.Tests.Firmware;

/// <summary>
/// The commit-keyed firmware cache: a firmware artifact zip flattened into
/// <c>{root}/{owner}/{repo}/{sha}/</c> behind a <c>.complete</c> marker, the
/// refusals that must leave neither a folder nor a <c>.partial</c> sibling,
/// and the per-repo prune to the newest commits by marker time.
/// </summary>
public sealed class FirmwareCacheTests : IDisposable
{
    private const string Owner = "ovandongen";
    private const string Repo = "zmk-config-cosmos82";

    /// <summary>The marker file the cache writes into a complete folder.</summary>
    private const string Marker = ".complete";

    private readonly string _root;

    public FirmwareCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lviz-firmware-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>A distinct valid 40-hex SHA per index.</summary>
    private static string Sha(int i) => new string('a', 39) + i.ToString("x");

    /// <summary>An in-memory artifact zip with the given entry names.</summary>
    private static MemoryStream Zip(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var target = archive.CreateEntry(name).Open();
                target.Write(Encoding.ASCII.GetBytes(content));
            }
        }

        stream.Position = 0;
        return stream;
    }

    // --- storing and reading back ---

    [Fact]
    public void Store_TwoUf2AndReadme_KeepsTheUf2SortedAndComplete_TryGetMirrors()
    {
        var cache = new FirmwareCache(_root);
        IReadOnlyList<string> paths;
        using (var zip = Zip(("a-zmk.uf2", "AAA"), ("b-zmk.uf2", "BBB"), ("README.txt", "text")))
            paths = cache.Store(Owner, Repo, Sha(1), zip);

        var folder = cache.FolderFor(Owner, Repo, Sha(1));
        Assert.Equal(
            new[] { Path.Combine(folder, "a-zmk.uf2"), Path.Combine(folder, "b-zmk.uf2") },
            paths);
        Assert.All(paths, path => Assert.True(File.Exists(path)));
        Assert.False(File.Exists(Path.Combine(folder, "README.txt")));
        Assert.True(File.Exists(Path.Combine(folder, Marker)));
        Assert.False(Directory.Exists(folder + ".partial"));

        Assert.Equal(paths, cache.TryGet(Owner, Repo, Sha(1)));
    }

    [Fact]
    public void Store_FlattensFoldersInEntryNames()
    {
        var cache = new FirmwareCache(_root);
        IReadOnlyList<string> paths;
        using (var zip = Zip(("sub/a-zmk.uf2", "AAA")))
            paths = cache.Store(Owner, Repo, Sha(2), zip);

        var folder = cache.FolderFor(Owner, Repo, Sha(2));
        Assert.Equal(new[] { Path.Combine(folder, "a-zmk.uf2") }, paths);
        Assert.True(File.Exists(Path.Combine(folder, "a-zmk.uf2")));
    }

    [Fact]
    public void Store_SameShaTwice_ReplacesTheFolder()
    {
        var cache = new FirmwareCache(_root);
        using (var zip = Zip(("a-zmk.uf2", "AAA"), ("old-zmk.uf2", "OLD")))
            cache.Store(Owner, Repo, Sha(3), zip);

        IReadOnlyList<string> paths;
        using (var zip = Zip(("a-zmk.uf2", "BBB")))
            paths = cache.Store(Owner, Repo, Sha(3), zip);

        Assert.Single(paths);
        Assert.Equal("BBB", File.ReadAllText(paths[0]));
        var folder = cache.FolderFor(Owner, Repo, Sha(3));
        Assert.False(File.Exists(Path.Combine(folder, "old-zmk.uf2")));
    }

    // --- refusals leave nothing behind ---

    [Fact]
    public void Store_TwoEntriesWithSameFileName_Throws_LeavesNoFolderAndNoPartial()
    {
        var cache = new FirmwareCache(_root);
        using var zip = Zip(("x/a.uf2", "AAA"), ("y/a.uf2", "BBB"));

        Assert.Throws<InvalidDataException>(() => cache.Store(Owner, Repo, Sha(4), zip));

        Assert.False(Directory.Exists(Path.Combine(_root, Owner, Repo)));
        var folder = cache.FolderFor(Owner, Repo, Sha(4));
        Assert.False(Directory.Exists(folder));
        Assert.False(Directory.Exists(folder + ".partial"));
    }

    [Fact]
    public void Store_NoUf2_Throws()
    {
        var cache = new FirmwareCache(_root);
        using var zip = Zip(("README.txt", "text"));

        var error = Assert.Throws<InvalidDataException>(() => cache.Store(Owner, Repo, Sha(5), zip));
        Assert.Equal("the artifact has no .uf2 files", error.Message);
        Assert.False(Directory.Exists(cache.FolderFor(Owner, Repo, Sha(5))));
    }

    [Fact]
    public void Store_EntryOverSixteenMebibytes_Throws_LeavesNothingBehind()
    {
        var cache = new FirmwareCache(_root);
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var target = archive.CreateEntry("big-zmk.uf2").Open();
            target.Write(new byte[16 * 1024 * 1024 + 1]); // declared size trips the check
        }

        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => cache.Store(Owner, Repo, Sha(6), stream));

        var folder = cache.FolderFor(Owner, Repo, Sha(6));
        Assert.False(Directory.Exists(folder));
        Assert.False(Directory.Exists(folder + ".partial"));
    }

    // --- TryGet only reads complete folders ---

    [Fact]
    public void TryGet_FolderWithoutMarker_Null()
    {
        var cache = new FirmwareCache(_root);
        var folder = cache.FolderFor(Owner, Repo, Sha(7));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a-zmk.uf2"), "AAA");

        Assert.Null(cache.TryGet(Owner, Repo, Sha(7)));
    }

    [Fact]
    public void TryGet_UnknownSha_Null() =>
        Assert.Null(new FirmwareCache(_root).TryGet(Owner, Repo, Sha(8)));

    // --- pruning ---

    [Fact]
    public void Store_SixCommits_KeepsTheFiveNewestAndDropsTheOldest()
    {
        var cache = new FirmwareCache(_root);
        var shas = Enumerable.Range(1, 6).Select(Sha).ToArray();
        var oldest = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 5; i++)
        {
            using var zip = Zip(("a-zmk.uf2", "A"));
            cache.Store(Owner, Repo, shas[i], zip);
            // Pin the marker times in the past, strictly increasing, so the
            // last store's own "now" marker is the newest of the six.
            File.SetLastWriteTimeUtc(
                Path.Combine(cache.FolderFor(Owner, Repo, shas[i]), Marker),
                oldest.AddHours(i));
        }

        using (var zip = Zip(("a-zmk.uf2", "A")))
            cache.Store(Owner, Repo, shas[5], zip);

        var left = Directory.GetDirectories(Path.Combine(_root, Owner, Repo));
        Assert.Equal(FirmwareCache.KeepPerRepo, left.Length);
        Assert.False(Directory.Exists(cache.FolderFor(Owner, Repo, shas[0])));
        for (var i = 1; i < 6; i++)
            Assert.True(Directory.Exists(cache.FolderFor(Owner, Repo, shas[i])));
    }

    // --- DeleteBuilds ---

    [Fact]
    public void DeleteBuilds_RemovesEveryOwnerFolder_KeepsTopLevelFiles()
    {
        var cache = new FirmwareCache(_root);
        using (var zip = Zip(("a-zmk.uf2", "AAA")))
            cache.Store(Owner, Repo, Sha(1), zip);
        using (var zip = Zip(("b-zmk.uf2", "BBB")))
            cache.Store("zzeneg", "zmk-config", Sha(2), zip);
        File.WriteAllText(Path.Combine(_root, "flash-history.json"), "[]");

        cache.DeleteBuilds();

        Assert.False(Directory.Exists(Path.Combine(_root, Owner)));
        Assert.False(Directory.Exists(Path.Combine(_root, "zzeneg")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(_root, "flash-history.json")));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void DeleteBuilds_MissingRoot_DoesNotThrow()
    {
        var missing = Path.Combine(_root, "never-created");

        new FirmwareCache(missing).DeleteBuilds();

        Assert.False(Directory.Exists(missing));
    }

    // --- arguments and the default root ---

    [Theory]
    [InlineData("../x", "r")]
    [InlineData("o", "..")]
    [InlineData("has space", "r")]
    public void FolderFor_InvalidOwnerOrRepo_Throws(string owner, string repo) =>
        Assert.Throws<ArgumentException>(() => new FirmwareCache(_root).FolderFor(owner, repo, Sha(1)));

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaag")] // 40 chars, not hex
    public void FolderFor_InvalidSha_Throws(string sha) =>
        Assert.Throws<ArgumentException>(() => new FirmwareCache(_root).FolderFor("o", "r", sha));

    [Fact]
    public void FolderFor_LowerCasesTheSha() =>
        Assert.Equal(
            Path.Combine(_root, "o", "r", new string('a', 39) + "1"),
            new FirmwareCache(_root).FolderFor("o", "r", new string('A', 39) + "1"));

    [Fact]
    public void DefaultRoot_IsTheLvizFirmwareFolder() =>
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LViz", "firmware"),
            FirmwareCache.DefaultRoot);
}
