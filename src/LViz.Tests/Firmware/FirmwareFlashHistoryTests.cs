using System.Globalization;
using System.Text.Json;
using LViz.Core.Firmware;
using Xunit;

namespace LViz.Tests.Firmware;

/// <summary>
/// The <c>firmware/flash-history.json</c> file: the last flashed commit per
/// config and per half, lower-cased SHAs stored in UTC, config keys compared
/// ordinally, and a read that treats every bit of broken content — missing
/// file, junk text, foreign version — as "no history" instead of throwing.
/// </summary>
public sealed class FirmwareFlashHistoryTests : IDisposable
{
    /// <summary>What the store's <c>now</c> returns: a CET-stamped instant.</summary>
    private static readonly DateTimeOffset FixedCet =
        new(2026, 11, 11, 13, 34, 56, 789, TimeSpan.FromHours(1));

    /// <summary>The same instant as the store must have written it: UTC.</summary>
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 11, 11, 12, 34, 56, 789, TimeSpan.Zero);

    private const string GitHubKey = "github:ovandongen/zmk-config-cosmos82:main";
    private const string LocalKey = "/home/user/zmk-config";

    private readonly string _dir;
    private readonly string _path;

    public FirmwareFlashHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"lviz-flash-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "flash-history.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private FirmwareFlashHistory History() => new(_path, () => FixedCet);

    // --- reading without a file or a record ---

    [Fact]
    public void Get_MissingFile_Null() =>
        Assert.Null(History().Get(GitHubKey, FirmwareHalfRole.Peripheral));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Get_BlankConfigKey_Null(string? configKey) =>
        Assert.Null(History().Get(configKey!, FirmwareHalfRole.Central));

    // --- recording and reading back ---

    [Fact]
    public void Record_ThenGet_SameInstance_LowerCasesShaAndStoresNowAsUtc()
    {
        var history = History();
        history.Record(GitHubKey, FirmwareHalfRole.Central, "AB12CD34EF56");

        var record = history.Get(GitHubKey, FirmwareHalfRole.Central);

        Assert.NotNull(record);
        Assert.Equal("ab12cd34ef56", record.CommitSha);
        Assert.Equal(FixedUtc, record.FlashedAt);
        Assert.Equal(_path, history.FilePath);
    }

    [Fact]
    public void Record_ThenGet_FreshInstance_SeesTheRecord()
    {
        var start = DateTimeOffset.UtcNow;
        // Default `now` on the writing store too: the stamp must be the real clock.
        new FirmwareFlashHistory(_path).Record(LocalKey, FirmwareHalfRole.Peripheral, "AB12CD34EF56");

        // No `now` here: the fresh instance must read what the first wrote.
        var record = new FirmwareFlashHistory(_path).Get(LocalKey, FirmwareHalfRole.Peripheral);

        Assert.NotNull(record);
        Assert.Equal("ab12cd34ef56", record.CommitSha);
        Assert.InRange(record.FlashedAt, start, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Record_CentralAndPeripheral_KeptApart()
    {
        var history = History();
        history.Record(GitHubKey, FirmwareHalfRole.Central, "aaa111");
        Assert.Null(history.Get(GitHubKey, FirmwareHalfRole.Peripheral));

        history.Record(GitHubKey, FirmwareHalfRole.Peripheral, "BBB222");

        var central = history.Get(GitHubKey, FirmwareHalfRole.Central);
        var peripheral = history.Get(GitHubKey, FirmwareHalfRole.Peripheral);
        Assert.NotNull(central);
        Assert.NotNull(peripheral);
        Assert.Equal("aaa111", central.CommitSha);
        Assert.Equal("bbb222", peripheral.CommitSha);
        Assert.Equal(FixedUtc, peripheral.FlashedAt);
    }

    [Fact]
    public void Record_TwoConfigKeys_KeptApart()
    {
        var history = History();
        history.Record(GitHubKey, FirmwareHalfRole.Central, "aaa111");
        history.Record(LocalKey, FirmwareHalfRole.Central, "bbb222");

        Assert.Equal("aaa111", history.Get(GitHubKey, FirmwareHalfRole.Central)?.CommitSha);
        Assert.Equal("bbb222", history.Get(LocalKey, FirmwareHalfRole.Central)?.CommitSha);
        Assert.Null(history.Get("/somewhere/else", FirmwareHalfRole.Central));
    }

    [Fact]
    public void Record_ConfigKeys_ComparedOrdinally()
    {
        History().Record(GitHubKey, FirmwareHalfRole.Central, "aaa111");

        Assert.Null(History().Get("github:Ovandongen/zmk-config-cosmos82:main", FirmwareHalfRole.Central));
    }

    [Fact]
    public void Record_TwiceForSameHalf_ReplacesTheFirstEntry()
    {
        var history = History();
        history.Record(GitHubKey, FirmwareHalfRole.Central, "aaa111");
        history.Record(GitHubKey, FirmwareHalfRole.Central, "BBB222");

        var record = history.Get(GitHubKey, FirmwareHalfRole.Central);
        Assert.NotNull(record);
        Assert.Equal("bbb222", record.CommitSha);

        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        var halves = doc.RootElement.GetProperty("configs").GetProperty(GitHubKey);
        Assert.Single(halves.EnumerateObject()); // replaced in place, not appended
        Assert.Equal("bbb222", halves.GetProperty("central").GetProperty("commit").GetString());
    }

    // --- broken content reads as empty, never throws ---

    [Fact]
    public void NotJsonFile_GetNull_ThenRecord_ReplacesTheFile()
    {
        File.WriteAllText(_path, "not json");
        var history = History();

        Assert.Null(history.Get(GitHubKey, FirmwareHalfRole.Central));

        history.Record(GitHubKey, FirmwareHalfRole.Central, "abc123");
        Assert.Equal("abc123", history.Get(GitHubKey, FirmwareHalfRole.Central)?.CommitSha);
    }

    [Fact]
    public void Get_OtherVersion_Null()
    {
        File.WriteAllText(
            _path,
            """{"version": 2, "configs": {"github:ovandongen/zmk-config-cosmos82:main": {"central": {"commit": "abc123", "flashedAt": "2026-11-11T12:00:00+00:00"}}}}""");

        Assert.Null(History().Get(GitHubKey, FirmwareHalfRole.Central));
    }

    [Fact]
    public void Record_AfterOtherVersion_RewritesTheFileAtVersion1()
    {
        File.WriteAllText(_path, """{"version": 2, "configs": {}}""");
        var history = History();

        history.Record(GitHubKey, FirmwareHalfRole.Central, "abc123");

        Assert.NotNull(history.Get(GitHubKey, FirmwareHalfRole.Central));
        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
    }

    // --- the file itself ---

    [Fact]
    public void WrittenFile_IndentedV1Json_WithTheKeyTextExactlyAsGiven()
    {
        History().Record(GitHubKey, FirmwareHalfRole.Peripheral, "DeadBeef123");

        var text = File.ReadAllText(_path);
        Assert.Contains("  \"version\": 1", text); // indented

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());

        // The config key survives verbatim — case, colons and slashes.
        var halves = root.GetProperty("configs").GetProperty(GitHubKey);
        var peripheral = halves.GetProperty("peripheral");
        Assert.Equal("deadbeef123", peripheral.GetProperty("commit").GetString());
        Assert.Equal(
            FixedUtc,
            DateTimeOffset.Parse(peripheral.GetProperty("flashedAt").GetString()!, CultureInfo.InvariantCulture));
        Assert.False(halves.TryGetProperty("central", out _));
    }

    [Fact]
    public void Record_CreatesMissingFolder()
    {
        var path = Path.Combine(_dir, "firmware", "deeper", "flash-history.json");
        var history = new FirmwareFlashHistory(path, () => FixedCet);

        history.Record(GitHubKey, FirmwareHalfRole.Central, "abc123");

        Assert.True(File.Exists(path));
        var record = history.Get(GitHubKey, FirmwareHalfRole.Central);
        Assert.NotNull(record);
        Assert.Equal("abc123", record.CommitSha);
    }

    // --- argument and IO behaviour ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_BlankSha_ThrowsArgumentException(string? commitSha) =>
        Assert.Throws<ArgumentException>(() =>
            History().Record(GitHubKey, FirmwareHalfRole.Central, commitSha!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_BlankConfigKey_ThrowsArgumentException(string? configKey) =>
        Assert.Throws<ArgumentException>(() =>
            History().Record(configKey!, FirmwareHalfRole.Central, "abc123"));

    [Fact]
    public void Record_PathIsAFolder_PropagatesTheIoError()
    {
        // The caller shows this error, so it must not be swallowed.
        // macOS/Linux throw IOException here, Windows UnauthorizedAccessException.
        Directory.CreateDirectory(_path);

        var error = Record.Exception(() => History().Record(GitHubKey, FirmwareHalfRole.Central, "abc123"));

        Assert.True(
            error is IOException or UnauthorizedAccessException,
            $"expected an IO error, got {error?.GetType().Name ?? "none"}");
    }

    [Fact]
    public void DefaultPath_IsTheLvizFirmwareFolderFile() =>
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LViz", "firmware", "flash-history.json"),
            FirmwareFlashHistory.DefaultPath);
}
