using Xunit;
using LViz.App.Services;
using LViz.Core.Settings;

namespace LViz.Tests;

/// <summary>Booking, resolving and pruning policy of <see cref="KeymapLibrary"/>, without a view-model.</summary>
public sealed class KeymapLibraryTests : IDisposable
{
    private const string GitHubEntry = "github:ovandongen/zmk-config-cosmos82:main:config/cosmos82.keymap";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lviz-library-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySettingsService _settings = new();
    private readonly KeymapLibrary _library;

    public KeymapLibraryTests()
    {
        Directory.CreateDirectory(_dir);
        _library = new KeymapLibrary(_settings, gitHub: null);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Book_SetsLastUsedAndPushesRecent_PerProfile()
    {
        _library.Book("Corne", "/a.keymap");
        _library.Book("Corne", "/b.keymap");
        _library.Book("Glove80", "/g.keymap");

        Assert.Equal("/b.keymap", _library.BookedEntry("Corne"));
        Assert.Equal(new[] { "/b.keymap", "/a.keymap" }, _library.RecentsFor("Corne"));
        Assert.Equal(new[] { "/g.keymap" }, _library.RecentsFor("Glove80"));
    }

    [Fact]
    public void Book_TakeFrom_ClearsOtherProfileOnlyWhenItHeldTheSameEntry()
    {
        _library.Book("Corne", "/a.keymap");
        _library.Book("user:mine", "/a.keymap", takeFromProfileId: "Corne");
        Assert.Null(_library.BookedEntry("Corne"));
        Assert.Equal("/a.keymap", _library.BookedEntry("user:mine"));

        _library.Book("Glove80", "/other.keymap");
        _library.Book("user:mine", "/a.keymap", takeFromProfileId: "Glove80");
        Assert.Equal("/other.keymap", _library.BookedEntry("Glove80"));
    }

    [Fact]
    public void Unbook_OnlyRemovesWhenBookingStillHoldsTheEntry_AndSkipsWriteOtherwise()
    {
        _library.Book("Corne", "/a.keymap");
        var before = _settings.Current;

        _library.Unbook("Corne", "/other.keymap");
        Assert.Same(before, _settings.Current);

        _library.Unbook("Corne", "/a.keymap");
        Assert.Null(_library.BookedEntry("Corne"));
        // Unbooking leaves the recents list alone.
        Assert.Equal(new[] { "/a.keymap" }, _library.RecentsFor("Corne"));
    }

    [Fact]
    public void RemoveRecent_AndClearRecents_SkipTheWriteWhenNothingToDo()
    {
        var before = _settings.Current;
        _library.RemoveRecent("Corne", "/nope.keymap");
        _library.ClearRecents("Corne");
        Assert.Same(before, _settings.Current);

        _library.Book("Corne", "/a.keymap");
        _library.Book("Glove80", "/g.keymap");
        _library.ClearRecents("Corne");
        Assert.Empty(_library.RecentsFor("Corne"));
        Assert.Equal(new[] { "/g.keymap" }, _library.RecentsFor("Glove80"));
    }

    [Fact]
    public void TryGetBooked_ExistingFile_Resolves()
    {
        var path = MakeFile("a.keymap");
        _library.Book("Corne", path);

        Assert.True(_library.TryGetBooked("Corne", out var resolved, out var entry));
        Assert.Equal(path, resolved);
        Assert.Equal(path, entry);
    }

    [Fact]
    public void TryGetBooked_MissingLocalFile_UnbooksIt_ButKeepsRecents()
    {
        var path = Path.Combine(_dir, "gone.keymap");
        _library.Book("Corne", path);

        Assert.False(_library.TryGetBooked("Corne", out _, out var entry));
        Assert.Equal(path, entry);
        Assert.Null(_library.BookedEntry("Corne"));
        Assert.Equal(new[] { path }, _library.RecentsFor("Corne"));
    }

    [Fact]
    public void TryGetBooked_GitHubEntryWithoutCacheCopy_IsKept()
    {
        _library.Book("Corne", GitHubEntry);

        Assert.False(_library.TryGetBooked("Corne", out var path, out var entry));
        Assert.Equal("", path);
        Assert.Equal(GitHubEntry, entry);
        Assert.Equal(GitHubEntry, _library.BookedEntry("Corne"));
    }

    [Fact]
    public void TryGetBooked_NothingBooked_ReturnsEmptyEntry()
    {
        Assert.False(_library.TryGetBooked("Corne", out _, out var entry));
        Assert.Equal("", entry);
    }

    [Fact]
    public void TryResolveEntry_LocalChecksDisk_GitHubNeverReachesTheNetwork()
    {
        var path = MakeFile("b.keymap");
        Assert.True(_library.TryResolveEntry(path, out var resolved));
        Assert.Equal(path, resolved);
        Assert.False(_library.TryResolveEntry(Path.Combine(_dir, "nope.keymap"), out _));
        Assert.False(_library.TryResolveEntry(GitHubEntry, out _));
    }

    [Fact]
    public void FileExists_SeamDoesNotAffectResolvingABookedEntry()
    {
        var path = MakeFile("c.keymap");
        _library.FileExists = _ => false;
        Assert.True(_library.TryResolveEntry(path, out _));
    }

    [Fact]
    public async Task CheckGitHubAsync_LocalEntryOrNoService_IsANoOp()
    {
        Assert.Null(await _library.CheckGitHubAsync("/a.keymap", "Corne", () => ("Corne", null)));
        Assert.Null(await _library.CheckGitHubAsync(GitHubEntry, "Corne", () => ("Corne", null)));
    }
}
