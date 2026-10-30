using LViz.Core.Layout;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Layout.Import;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary>
/// Tests for <see cref="UserLayoutDraft"/> (part 2 of the layout wizard):
/// store-legal unique ids, the automatic left/right midline (largest gap in
/// the middle band, rotated centres, fallback to the board centre), the draft
/// <see cref="UserLayoutFile"/> the wizard saves, and the name suggestion.
/// Split expectations on the real fixtures were computed with the rule
/// implemented in <see cref="UserLayoutDraft.FindMidlineCentiU"/>.
/// </summary>
public sealed class UserLayoutDraftTests : IDisposable
{
    private readonly string _dir;

    public UserLayoutDraftTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"lviz-draft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ── MakeId ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("My Slabwing!", "my-slabwing")]
    [InlineData("Cosmos 82", "cosmos-82")]
    [InlineData("Cosmos82", "cosmos82")]
    [InlineData("Café Board", "cafe-board")]
    public void MakeId_SlugsNames(string name, string expected)
    {
        Assert.Equal(expected, UserLayoutDraft.MakeId(name, []));
    }

    [Fact]
    public void MakeId_AppendsNumberSuffixUntilFree()
    {
        Assert.Equal("my-slabwing-2", UserLayoutDraft.MakeId("My Slabwing!", ["my-slabwing"]));
        Assert.Equal("my-slabwing-3",
            UserLayoutDraft.MakeId("My Slabwing!", ["my-slabwing", "my-slabwing-2"]));
    }

    [Fact]
    public void MakeId_FallsBackToBoardWhenNothingUsableRemains()
    {
        Assert.Equal("board", UserLayoutDraft.MakeId("!!!", []));
        // the fallback collides like any other id
        Assert.Equal("board-2", UserLayoutDraft.MakeId("!!!", ["board"]));
    }

    [Theory]
    [InlineData("My Slabwing!")]
    [InlineData("Cosmos 82")]
    [InlineData("Café Board")]
    [InlineData("!!!")]
    [InlineData("-- ??? --")]
    [InlineData("  ")]
    public void MakeId_ResultAlwaysMatchesTheStoreIdPattern(string name)
    {
        Assert.Matches("^[a-z0-9][a-z0-9_-]*$", UserLayoutDraft.MakeId(name, []));
    }

    // ── the split on real boards ────────────────────────────────────────

    [Fact]
    public void SlabwingInfoJson_SplitsTwentyEightTwentyEight()
    {
        AssertSplit(Info("slabwing.info.json"), 28, 28);
    }

    [Fact]
    public void Cosmos82InfoJson_SplitsFortyOneFortyOne()
    {
        AssertSplit(Info("cosmos82.info.json"), 41, 41);
    }

    [Fact]
    public void Go60InfoJson_SplitsThirtyThirty()
    {
        AssertSplit(Info("go60.info.json"), 30, 30);
    }

    [Fact]
    public void SlabwingMatrixTransform_SplitsTwentyEightTwentyEight()
    {
        AssertSplit(Transform("slabwing.overlay"), 28, 28);
    }

    [Fact]
    public void Cosmos82MatrixTransform_SplitsFortyOneFortyOne()
    {
        AssertSplit(Transform("cosmos82.dtsi"), 41, 41);
    }

    [Theory]
    [InlineData("slabwing.info.json")]
    [InlineData("cosmos82.info.json")]
    [InlineData("go60.info.json")]
    public void Split_KeepsEveryLeftCentreLeftOfEveryRightCentre(string fixture)
    {
        var file = UserLayoutDraft.Create(Info(fixture), "Test Board", null, []);

        var left = file.Keys.Where(k => k.Hand == Hand.Left).Select(CenterX);
        var right = file.Keys.Where(k => k.Hand == Hand.Right).Select(CenterX);

        Assert.True(left.Max() < right.Min(),
            $"leftmost right centre {right.Min()} is not right of rightmost left centre {left.Max()}");
    }

    // ── midline rule details ────────────────────────────────────────────

    [Fact]
    public void FindMidline_NoGapWideEnough_FallsBackToSpanCentre()
    {
        // 21 keys of width 10 at X = 0, 10, …, 200: every gap is 10 < 25,
        // so the midline is the centre of the key centres 5…205.
        var keys = Enumerable.Range(0, 21).Select(i => Key(i * 10, w: 10)).ToList();

        Assert.Equal(105, UserLayoutDraft.FindMidlineCentiU(keys));
    }

    [Fact]
    public void FindMidline_LargestGapOutsideTheMiddleBand_IsIgnored()
    {
        // Centres 50…450 and 950. The 450→950 gap (midpoint 700) lies outside
        // the band [320, 680], so the split sits at the 350→450 gap instead.
        var keys = new List<DtsiKeyPhysical>
        {
            Key(0), Key(100), Key(200), Key(300), Key(400), Key(900),
        };

        Assert.Equal(400, UserLayoutDraft.FindMidlineCentiU(keys));
    }

    [Fact]
    public void Create_SplitFollowsTheRotatedCentreNotTheUnrotatedOne()
    {
        var keys = new List<DtsiKeyPhysical>
        {
            Key(0), Key(100), Key(200),
            Key(700), Key(800), Key(900),
            // 180° about (500, 50): unrotated centre (350, 50) sits at (650, 50).
            Key(300, rotation: 18000, rx: 500, ry: 50),
        };

        // The rotated centre (650) opens a 250→650 gap with midpoint 450,
        // inside the band — that gap, not the unrotated position, splits.
        Assert.Equal(450, UserLayoutDraft.FindMidlineCentiU(keys));

        var file = UserLayoutDraft.Create(Imported(keys), "Rotated Demo", null, []);

        Assert.Equal(Hand.Right, file.Keys[6].Hand);
        Assert.Equal(
            new[] { Hand.Left, Hand.Left, Hand.Left, Hand.Right, Hand.Right, Hand.Right },
            file.Keys.Take(6).Select(k => k.Hand));
    }

    [Fact]
    public void Create_KeyExactlyOnTheMidline_IsRight()
    {
        // Centres 50, 150, 250: no gap midpoint lies in the band, so the
        // midline is the span centre 150 — the key sitting exactly on it goes
        // right (left is strictly below).
        var file = UserLayoutDraft.Create(Imported([Key(0), Key(100), Key(200)]), "Board", null, []);

        Assert.Equal(new[] { Hand.Left, Hand.Right, Hand.Right }, file.Keys.Select(k => k.Hand));
    }

    // ── Create ──────────────────────────────────────────────────────────

    [Fact]
    public void Create_CopiesKeysUnchangedAndSetsIdNameHidName()
    {
        var layout = Info("slabwing.info.json");

        var file = UserLayoutDraft.Create(layout, "  My Slabwing!  ", "  Slabwing  ", ["my-slabwing"]);

        Assert.Equal("my-slabwing-2", file.Id);
        Assert.Equal("My Slabwing!", file.Name);
        Assert.Equal("Slabwing", file.HidName);
        Assert.Equal(UserLayoutFile.CurrentSchemaVersion, file.SchemaVersion);

        Assert.Equal(layout.Keys.Count, file.Keys.Count);
        for (var i = 0; i < layout.Keys.Count; i++)
        {
            var src = layout.Keys[i];
            var dst = file.Keys[i];
            Assert.Equal(
                (src.X, src.Y, src.Width, src.Height, src.Rotation, src.Rx, src.Ry),
                (dst.X, dst.Y, dst.W, dst.H, dst.R, dst.Rx, dst.Ry));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankHidName_BecomesNull(string? hidName)
    {
        var file = UserLayoutDraft.Create(Info("go60.info.json"), "Go60", hidName, []);

        Assert.Null(file.HidName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(
            () => UserLayoutDraft.Create(Info("go60.info.json"), name, null, []));
    }

    [Fact]
    public void Create_ResultSavesAndLoadsBack()
    {
        var keys = new List<DtsiKeyPhysical>
        {
            Key(0), Key(300, rotation: 18000, rx: 500, ry: 50), Key(700),
        };
        var file = UserLayoutDraft.Create(Imported(keys), "Rotated Demo", "Demo HID", []);

        UserLayoutStore.Save(file, _dir);

        var loaded = Assert.Single(UserLayoutStore.LoadAll(_dir));
        Assert.Equal(file.Id, loaded.Id);
        Assert.Equal(file.Name, loaded.Name);
        Assert.Equal(file.HidName, loaded.HidName);
        Assert.Equal(file.SchemaVersion, loaded.SchemaVersion);
        // UserLayoutFile equality compares Keys by reference; the key records
        // themselves compare structurally, so compare the sequences.
        Assert.Equal(file.Keys, loaded.Keys);
    }

    // ── SuggestName ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Slabwing", "slabwing_shield", "my-config", "Slabwing")]
    [InlineData("  Slabwing  ", "slabwing_shield", "my-config", "Slabwing")]
    [InlineData(null, "  slabwing_shield  ", "my-config", "slabwing_shield")]
    [InlineData(null, "   ", "My Config", "My Config")]
    public void SuggestName_PrefersKeyboardThenShieldThenFolder(
        string? keyboardName, string? shieldName, string folderName, string expected)
    {
        Assert.Equal(expected, UserLayoutDraft.SuggestName(keyboardName, shieldName, folderName));
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static ImportedLayout Info(string fixture) =>
        Assert.Single(InfoJsonImporter.Import(LayoutFixtures.Read(fixture)));

    private static ImportedLayout Transform(string fixture) =>
        Assert.Single(MatrixTransformImporter.Import(LayoutFixtures.Read(fixture)));

    private static ImportedLayout Imported(IReadOnlyList<DtsiKeyPhysical> keys) =>
        new("inline", LayoutSource.MatrixTransform, keys, []);

    /// <summary>A 100-high key at <paramref name="x"/>, optionally rotated about (rx, ry).</summary>
    private static DtsiKeyPhysical Key(int x, int w = 100, int rotation = 0, int rx = 0, int ry = 0) =>
        new(w, 100, x, 0, rotation, rx, ry);

    /// <summary>Rotated centre X of a draft key — mirrors the draft's own rule.</summary>
    private static double CenterX(UserLayoutKey k)
    {
        var cx = k.X + k.W / 2.0;
        if (k.R == 0)
            return cx;

        var radians = k.R / 100.0 * Math.PI / 180.0;
        var dx = cx - k.Rx;
        var dy = k.Y + k.H / 2.0 - k.Ry;
        return k.Rx + dx * Math.Cos(radians) - dy * Math.Sin(radians);
    }

    private static void AssertSplit(ImportedLayout layout, int left, int right)
    {
        var file = UserLayoutDraft.Create(layout, "Test Board", null, []);

        Assert.Equal(left + right, file.Keys.Count);
        Assert.Equal(left, file.Keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(right, file.Keys.Count(k => k.Hand == Hand.Right));
    }
}
