using System.Text.Json;
using LViz.Core.Layout;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary>
/// The <c>&lt;id&gt;.layout.json</c> file format and the store that reads and
/// writes it: field-for-field round-tripping, camelCase + lower-case hands on
/// disk, file-name selection and ordering, and skipping (never crashing on) a
/// broken file next to a good one.
/// </summary>
public sealed class UserLayoutStoreTests : IDisposable
{
    private readonly string _dir;

    public UserLayoutStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"lviz-layouts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // --- round-tripping ---

    [Fact]
    public void Save_ThenLoadAll_RoundTripsEveryField()
    {
        var saved = new UserLayoutFile(
            UserLayoutFile.CurrentSchemaVersion,
            "slabwing",
            "Slabwing",
            null,
            [
                new UserLayoutKey(-1400, -20, 100, 140, -1500, -50, -60, Hand.Left),
                new UserLayoutKey(1400, 200, 175, 100, 2250, 1500, 900, Hand.Right),
            ]);
        var withHidName = saved with { Id = "cosmos", Name = "Cosmos", HidName = "Cosmos 82" };

        UserLayoutStore.Save(saved, _dir);
        UserLayoutStore.Save(withHidName, _dir);

        var loaded = UserLayoutStore.LoadAll(_dir);

        Assert.Equal(2, loaded.Count);

        var actual = loaded.Single(l => l.Id == "slabwing");
        Assert.Equal(UserLayoutFile.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.Equal("Slabwing", actual.Name);
        Assert.Null(actual.HidName);
        // UserLayoutFile equality compares Keys by reference, so compare the keys
        // as a sequence instead of asserting on the records.
        Assert.Equal(saved.Keys, actual.Keys);

        Assert.Equal("Cosmos 82", loaded.Single(l => l.Id == "cosmos").HidName);
    }

    [Fact]
    public void Save_WritesCamelCaseNamesAndLowerCaseHands()
    {
        UserLayoutStore.Save(
            new UserLayoutFile(
                UserLayoutFile.CurrentSchemaVersion, "slabwing", "Slabwing", "Slabwing",
                [
                    new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Left),
                    new UserLayoutKey(100, 0, 100, 100, 0, 0, 0, Hand.Right),
                ]),
            _dir);

        var json = File.ReadAllText(Path.Combine(_dir, "slabwing.layout.json"));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            new[] { "schemaVersion", "id", "name", "hidName", "keys" },
            doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            new[] { "x", "y", "w", "h", "r", "rx", "ry", "hand" },
            doc.RootElement.GetProperty("keys")[0].EnumerateObject().Select(p => p.Name));

        Assert.Contains("\"left\"", json);
        Assert.Contains("\"right\"", json);
        Assert.DoesNotContain("\"Left\"", json);
        Assert.DoesNotContain("\"Right\"", json);
        Assert.DoesNotContain("\"SchemaVersion\"", json);
        Assert.DoesNotContain("\"HidName\"", json);

        // Indented, and the file name stem is the id.
        var lines = json.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Contains(lines, l => l.StartsWith("  \"id\": \"slabwing\","));
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        var nested = Path.Combine(_dir, "layouts", "deep");

        UserLayoutStore.Save(ValidLayout("slabwing", "Slabwing"), nested);

        Assert.True(File.Exists(Path.Combine(nested, "slabwing.layout.json")));
    }

    [Fact]
    public void Save_SameIdTwice_OverwritesTheExistingFile()
    {
        UserLayoutStore.Save(ValidLayout("slabwing", "First"), _dir);
        UserLayoutStore.Save(ValidLayout("slabwing", "Second"), _dir);

        var loaded = UserLayoutStore.LoadAll(_dir);

        Assert.Equal("Second", Assert.Single(loaded).Name);
    }

    [Fact]
    public void Save_IdThatCouldNeverBeLoaded_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => UserLayoutStore.Save(ValidLayout("Slab Wing", "Slabwing"), _dir));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    // --- loading ---

    [Fact]
    public void LoadAll_MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(UserLayoutStore.LoadAll(Path.Combine(_dir, "no-such-folder")));
    }

    [Fact]
    public void LoadAll_OrdersByOrdinalFileName()
    {
        Write("lower.layout.json", FileJson("lower", "Lower"));
        Write("Upper.layout.json", FileJson("upper", "Upper"));

        var loaded = UserLayoutStore.LoadAll(_dir);

        // Ordinal, not culture- or case-insensitive: 'U' (85) sorts before 'l' (108).
        Assert.Equal(new[] { "upper", "lower" }, loaded.Select(l => l.Id));
    }

    [Fact]
    public void LoadAll_RenamedCopyKeepingTheOriginalId_IsSkipped()
    {
        // A hand-made experiment copy: "slabwing-v2" sorts before "slabwing." and
        // would otherwise shadow the original.
        Write("slabwing.layout.json", FileJson("slabwing", "Original"));
        Write("slabwing-v2.layout.json", FileJson("slabwing", "Copy"));

        var loaded = UserLayoutStore.LoadAll(_dir);

        Assert.Equal("Original", Assert.Single(loaded).Name);
    }

    [Fact]
    public void LoadAll_FileNameMatchesIdCaseInsensitively()
    {
        Write("Slabwing.layout.json", FileJson("slabwing", "Slabwing"));

        Assert.Equal("slabwing", Assert.Single(UserLayoutStore.LoadAll(_dir)).Id);
    }

    [Fact]
    public void LoadAll_IgnoresFilesThatAreNotLayoutFiles()
    {
        // Valid layout content under names that must not be read: what is under
        // test is the file-name filter, so a filter that also matched these would
        // load them and fail the test.
        Write("notes.json", FileJson("notes", "Notes"));
        Write("x.layout.json.bak", FileJson("backup", "Backup"));
        Write("a.layout.json.tmp", FileJson("temp", "Temp"));

        Assert.Empty(UserLayoutStore.LoadAll(_dir));
    }

    [Fact]
    public void LoadAll_ReadsTheDocumentedFileShape()
    {
        // Hand-written JSON, exactly as documented for the format — guards the
        // reader against drifting from what Save happens to write.
        Write("slabwing.layout.json",
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            "  \"id\": \"slabwing\",\n" +
            "  \"name\": \"Slabwing\",\n" +
            "  \"hidName\": \"Slabwing\",\n" +
            "  \"keys\": [ { \"x\": 0, \"y\": 0, \"w\": 100, \"h\": 100, \"r\": 0, \"rx\": 0, \"ry\": 0, \"hand\": \"left\" } ]\n" +
            "}\n");

        var loaded = Assert.Single(UserLayoutStore.LoadAll(_dir));

        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal("slabwing", loaded.Id);
        Assert.Equal("Slabwing", loaded.Name);
        Assert.Equal("Slabwing", loaded.HidName);
        Assert.Equal(new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Left), Assert.Single(loaded.Keys));
    }

    public static TheoryData<string, string> BrokenFiles => new()
    {
        { "malformed", "{ this is not json" },
        { "array-instead-of-object", "[]" },
        { "null-document", "null" },
        { "wrong-schema-version-type", Json(("schemaVersion", "1"), ("id", "broken"), ("name", "Broken"), ("keys", Keys())) },
        { "newer-schema-version", Json(("schemaVersion", 2), ("id", "broken"), ("name", "Broken"), ("keys", Keys())) },
        { "older-schema-version", Json(("schemaVersion", 0), ("id", "broken"), ("name", "Broken"), ("keys", Keys())) },
        { "missing-schema-version", Json(("id", "broken"), ("name", "Broken"), ("keys", Keys())) },
        { "uppercase-id", Json(("schemaVersion", 1), ("id", "Broken"), ("name", "Broken"), ("keys", Keys())) },
        { "id-starting-with-dash", Json(("schemaVersion", 1), ("id", "-broken"), ("name", "Broken"), ("keys", Keys())) },
        { "missing-id", Json(("schemaVersion", 1), ("name", "Broken"), ("keys", Keys())) },
        { "blank-name", Json(("schemaVersion", 1), ("id", "broken"), ("name", "   "), ("keys", Keys())) },
        { "missing-name", Json(("schemaVersion", 1), ("id", "broken"), ("keys", Keys())) },
        { "missing-keys", Json(("schemaVersion", 1), ("id", "broken"), ("name", "Broken")) },
        { "null-keys", Json(("schemaVersion", 1), ("id", "broken"), ("name", "Broken"), ("keys", null)) },
        { "empty-keys", Json(("schemaVersion", 1), ("id", "broken"), ("name", "Broken"), ("keys", NoKeys)) },
        { "null-key-entry", Json(("schemaVersion", 1), ("id", "broken"), ("name", "Broken"), ("keys", new Dictionary<string, object?>?[] { null })) },
        { "zero-width-key", Json(("schemaVersion", 1), ("id", "broken"), ("name", "Broken"), ("keys", new[] { Key(w: 0) })) },
        { "negative-height-key", Json(("schemaVersion", 1), ("id", "broken"), ("name", "Broken"), ("keys", new[] { Key(h: -100) })) },
        { "missing-hand", Json(("schemaVersion", 1), ("id", "broken"), ("name", "Broken"), ("keys", new[] { Key(withHand: false) })) },
        { "null-hand", Json(("schemaVersion", 1), ("id", "broken"), ("name", "Broken"), ("keys", new[] { Key(hand: null) })) },
        { "unknown-hand", Json(("schemaVersion", 1), ("id", "broken"), ("name", "Broken"), ("keys", new[] { Key(hand: "middle") })) },
        { "id-not-matching-file-name", Json(("schemaVersion", 1), ("id", "other"), ("name", "Broken"), ("keys", Keys())) },
    };

    [Theory]
    [MemberData(nameof(BrokenFiles))]
    public void LoadAll_SkipsBrokenFile_AndStillLoadsItsNeighbour(string caseName, string content)
    {
        // Named after the fixtures' id, so only the broken field rejects the file.
        _ = caseName;
        Write("broken.layout.json", content);
        UserLayoutStore.Save(ValidLayout("neighbour", "Neighbour"), _dir);

        var loaded = UserLayoutStore.LoadAll(_dir);

        Assert.Equal(new[] { "neighbour" }, loaded.Select(l => l.Id));
    }

    // --- folder layout ---

    [Fact]
    public void DefaultDirectory_IsLayoutsFolderNextToSettings()
    {
        Assert.Equal("layouts", Path.GetFileName(UserLayoutStore.DefaultDirectory));
        Assert.Equal("LViz", Path.GetFileName(Path.GetDirectoryName(UserLayoutStore.DefaultDirectory)));
    }

    // --- JSON fixtures ---
    // Built as field-ordered dictionaries and serialized, so each case can break
    // or omit exactly one field without hand-writing JSON text. Insertion order is
    // emitted order, which matches the documented on-disk order.

    /// <summary>A key object; any field can be broken and <c>hand</c> can be left out.</summary>
    private static Dictionary<string, object?> Key(
        int w = 100, int h = 100, string? hand = "left", bool withHand = true)
    {
        var key = new Dictionary<string, object?>
        {
            ["x"] = 0,
            ["y"] = 0,
            ["w"] = w,
            ["h"] = h,
            ["r"] = 0,
            ["rx"] = 0,
            ["ry"] = 0,
        };
        if (withHand)
            key["hand"] = hand;
        return key;
    }

    private static Dictionary<string, object?>[] Keys() => [Key()];

    private static readonly Dictionary<string, object?>[] NoKeys = [];

    /// <summary>Splices the given fields into a JSON object; omit a field by not passing it.</summary>
    private static string Json(params (string Name, object? Value)[] fields)
    {
        var obj = new Dictionary<string, object?>(fields.Length);
        foreach (var (name, value) in fields)
            obj[name] = value;
        return JsonSerializer.Serialize(obj);
    }

    private static string FileJson(string id, string name) =>
        Json(("schemaVersion", 1), ("id", id), ("name", name), ("hidName", name), ("keys", Keys()));

    private static UserLayoutFile ValidLayout(string id, string name) => new(
        UserLayoutFile.CurrentSchemaVersion, id, name, name,
        [new UserLayoutKey(0, 0, 100, 100, 0, 0, 0, Hand.Left)]);

    private void Write(string fileName, string content) =>
        File.WriteAllText(Path.Combine(_dir, fileName), content);
}
