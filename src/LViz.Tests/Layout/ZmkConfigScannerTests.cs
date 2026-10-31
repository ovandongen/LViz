using LViz.Core.Layout;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Layout.Import;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary>
/// <see cref="ZmkConfigScanner"/>: walking a zmk-config folder tree, running
/// the three importers over the files it finds, collecting keymaps / keyboard
/// name / shield name, reporting broken files without stopping, and ranking
/// candidates. Each test builds a temp tree from the embedded fixtures and
/// deletes it afterwards.
/// </summary>
public sealed class ZmkConfigScannerTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
    }

    // --- ScanFolder: full trees ---

    [Fact]
    public void ScanFolder_CosmosTree_FindsThreeCandidatesInSourceOrder()
    {
        var root = CreateCosmosTree();
        var dtsi = Path.Combine(root, "boards", "shields", "cosmos82", "cosmos82.dtsi");
        var info = Path.Combine(root, "config", "info.json");

        var scan = ZmkConfigScanner.ScanFolder(root);

        Assert.Equal(root, scan.Root);
        Assert.Equal(
            new (LayoutSource, string)[]
            {
                (LayoutSource.PhysicalLayout, dtsi),
                (LayoutSource.InfoJson, info),
                (LayoutSource.MatrixTransform, dtsi),
            },
            scan.Candidates.Select(c => (c.Layout.Source, c.FilePath)));
        Assert.Equal([82, 82, 82], scan.Candidates.Select(c => c.Layout.Keys.Count));
        Assert.Equal("Cosmos82", scan.KeyboardName);
        Assert.Equal("cosmos82", scan.ShieldName);
        Assert.Equal([Path.Combine(root, "config", "cosmos82.keymap")], scan.KeymapPaths);
        Assert.Empty(scan.Problems);
    }

    [Fact]
    public void ScanFolder_SlabwingTree_PrefersConfigKeymaps()
    {
        var root = CreateSlabwingTree();
        var overlay = Path.Combine(root, "boards", "shields", "slabwing", "slabwing.overlay");
        var info = Path.Combine(root, "config", "info.json");

        var scan = ZmkConfigScanner.ScanFolder(root);

        Assert.Equal(
            new (LayoutSource, string)[]
            {
                (LayoutSource.InfoJson, info),
                (LayoutSource.MatrixTransform, overlay),
            },
            scan.Candidates.Select(c => (c.Layout.Source, c.FilePath)));
        Assert.Equal([56, 56], scan.Candidates.Select(c => c.Layout.Keys.Count));
        // tests/slabwing_matrix_test.keymap exists but config/ has one, so only
        // the config keymap is reported.
        Assert.Equal([Path.Combine(root, "config", "slabwing.keymap")], scan.KeymapPaths);
        Assert.Equal("Slabwing", scan.KeyboardName);
        Assert.Equal("slabwing", scan.ShieldName);
        Assert.Empty(scan.Problems);
    }

    // --- keyboard name ---

    [Fact]
    public void ScanFolder_ConfNameWinsOverKconfig_AndCommentedLinesAreIgnored()
    {
        var root = CreateSlabwingTree();
        // A commented ghost line must not match; the .conf beats the Kconfig.
        WriteFile(root, "config/slabwing.conf", """
            # Ghost line below must be ignored:
            # CONFIG_ZMK_KEYBOARD_NAME="Ghost"
            CONFIG_ZMK_KEYBOARD_NAME="FromConf"
            """);
        WriteFile(root, "boards/shields/slabwing/Kconfig.defconfig", KconfigDefconfig("Slabwing"));

        var scan = ZmkConfigScanner.ScanFolder(root);

        Assert.Equal("FromConf", scan.KeyboardName);
    }

    [Fact]
    public void ScanFolder_CommentedDefaultLineInKconfig_IsIgnored()
    {
        var root = CreateSlabwingTree();
        WriteFile(root, "boards/shields/slabwing/Kconfig.defconfig", """
            if SHIELD_SLABWING_RIGHT
            # Must be less than 16 characters long!
            config ZMK_KEYBOARD_NAME
                # default "Ghost"
                default "Slabwing"

            endif
            """);

        var scan = ZmkConfigScanner.ScanFolder(root);

        Assert.Equal("Slabwing", scan.KeyboardName);
    }

    // --- walk filtering ---

    [Fact]
    public void ScanFolder_DoesNotScanDotBuildOrZephyrDirectories()
    {
        var root = CreateTempRoot();
        var overlay = LayoutFixtures.Read("slabwing.overlay");
        var realOverlay = Path.Combine(root, "boards", "shields", "slabwing", "slabwing.overlay");
        WriteFile(root, "boards/shields/slabwing/slabwing.overlay", overlay);
        WriteFile(root, ".git/hooks/slabwing.dtsi", overlay);
        WriteFile(root, ".github/slabwing.dtsi", overlay);
        WriteFile(root, "build/intermediate/slabwing.overlay", overlay);
        WriteFile(root, "zephyr/include/slabwing.dtsi", overlay);

        var scan = ZmkConfigScanner.ScanFolder(root);

        Assert.Equal([realOverlay], scan.Candidates.Select(c => c.FilePath));
        Assert.Single(scan.Candidates);
        Assert.Empty(scan.Problems);
    }

    // --- broken files ---

    [Fact]
    public void ScanFolder_MalformedDtsi_IsReportedAndScanContinues()
    {
        var root = CreateTempRoot();
        // A 6-field physical attr entry: the physical-layout import refuses it.
        WriteFile(root, "boards/shields/broken/broken.dtsi", """
            / {
                layout0: physical_layout_0 {
                    compatible = "zmk,physical-layout";
                    display-name = "Broken";
                    keys = <&key_physical_attrs 100 100 0 0 0 0>,
                           <&key_physical_attrs 100 100 100 0 0 0 0>;
                };
            };
            """);
        WriteFile(root, "config/info.json", LayoutFixtures.Read("slabwing.info.json"));
        WriteFile(root, "config/broken.keymap", "/* keymap */");

        var scan = ZmkConfigScanner.ScanFolder(root);

        var relative = Path.Combine("boards", "shields", "broken", "broken.dtsi");
        var problem = Assert.Single(scan.Problems);
        Assert.StartsWith(relative + ": ", problem);
        Assert.Contains("physical_layout_0", problem);

        // The healthy file still yields its candidate.
        var candidate = Assert.Single(scan.Candidates);
        Assert.Equal(LayoutSource.InfoJson, candidate.Layout.Source);
        Assert.Equal(56, candidate.Layout.Keys.Count);
    }

    // --- Rank ---

    [Fact]
    public void Rank_MatchingKeyCount_KeepsInfoJsonFirstOnSlabwing()
    {
        var scan = ZmkConfigScanner.ScanFolder(CreateSlabwingTree());

        var ranked = ZmkConfigScanner.Rank(scan.Candidates, 56);

        Assert.Equal(
            [LayoutSource.InfoJson, LayoutSource.MatrixTransform],
            ranked.Select(c => c.Layout.Source));
    }

    [Fact]
    public void Rank_MatchingKeyCount_BoostsGo60AboveCosmosCandidates()
    {
        var root = CreateCosmosTree();
        WriteFile(root, "boards/shields/go60/info.json", LayoutFixtures.Read("go60.info.json"));
        var scan = ZmkConfigScanner.ScanFolder(root);
        Assert.Equal(4, scan.Candidates.Count);

        var ranked = ZmkConfigScanner.Rank(scan.Candidates, 60);

        Assert.Equal(
            new (LayoutSource, int)[]
            {
                (LayoutSource.InfoJson, 60),          // the go60 match, boosted first
                (LayoutSource.PhysicalLayout, 82),
                (LayoutSource.InfoJson, 82),
                (LayoutSource.MatrixTransform, 82),
            },
            ranked.Select(c => (c.Layout.Source, c.Layout.Keys.Count)));
    }

    [Fact]
    public void Rank_NullKeyCount_KeepsSourceOrder()
    {
        var scan = ZmkConfigScanner.ScanFolder(CreateSlabwingTree());

        var ranked = ZmkConfigScanner.Rank(scan.Candidates, null);

        Assert.Equal(scan.Candidates, ranked);
    }

    // --- ScanFile ---

    [Fact]
    public void ScanFile_DtsiUnderBoards_UsesParentConfigContext()
    {
        var root = CreateCosmosTree();
        var dtsi = Path.Combine(root, "boards", "shields", "cosmos82", "cosmos82.dtsi");

        var scan = ZmkConfigScanner.ScanFile(dtsi);

        Assert.Equal(root, scan.Root);
        Assert.Equal(
            new (LayoutSource, string)[]
            {
                (LayoutSource.PhysicalLayout, dtsi),
                (LayoutSource.MatrixTransform, dtsi),
            },
            scan.Candidates.Select(c => (c.Layout.Source, c.FilePath)));
        Assert.Equal("Cosmos82", scan.KeyboardName);
        Assert.Equal([Path.Combine(root, "config", "cosmos82.keymap")], scan.KeymapPaths);
        Assert.Equal("cosmos82", scan.ShieldName);
        Assert.Empty(scan.Problems);
    }

    [Fact]
    public void ScanFile_LooseInfoJson_OutsideBoards_HasNoContext()
    {
        var root = CreateTempRoot();
        var info = Path.Combine(root, "misc", "info.json");
        WriteFile(root, "misc/info.json", LayoutFixtures.Read("slabwing.info.json"));
        // Neighbours that must NOT be picked up: no boards ancestor.
        WriteFile(root, "misc/slabwing.keymap", "/* keymap */");
        WriteFile(root, "misc/slabwing.conf", """CONFIG_ZMK_KEYBOARD_NAME="Loose" """);

        var scan = ZmkConfigScanner.ScanFile(info);

        Assert.Equal(Path.Combine(root, "misc"), scan.Root);
        var candidate = Assert.Single(scan.Candidates);
        Assert.Equal(LayoutSource.InfoJson, candidate.Layout.Source);
        Assert.Equal(56, candidate.Layout.Keys.Count);
        Assert.Equal(Path.Combine(root, "misc", "info.json"), candidate.FilePath);
        Assert.Empty(scan.KeymapPaths);
        Assert.Null(scan.KeyboardName);
        Assert.Null(scan.ShieldName);
        Assert.Empty(scan.Problems);
    }

    [Fact]
    public void ScanFile_UnsupportedExtension_ReportsProblem()
    {
        var root = CreateTempRoot();
        var txt = Path.Combine(root, "misc", "notes.txt");
        WriteFile(root, "misc/notes.txt", "hello");

        var scan = ZmkConfigScanner.ScanFile(txt);

        Assert.Equal(Path.Combine(root, "misc"), scan.Root);
        Assert.Empty(scan.Candidates);
        Assert.Equal("notes.txt: unsupported file type", Assert.Single(scan.Problems));
    }

    // --- saved layouts (*.layout.json from an earlier wizard run) ---

    [Fact]
    public void ScanFolder_SavedLayout_IsFirstCandidate_AndCarriesNameKeysAndHands()
    {
        var root = CreateSlabwingTree();
        var saved = SavedSlabwingLayout();
        UserLayoutStore.Save(saved, Path.Combine(root, "config"));
        var savedPath = Path.Combine(root, "config", "slabwing.layout.json");

        var scan = ZmkConfigScanner.ScanFolder(root);

        var first = scan.Candidates[0];
        Assert.Equal(LayoutSource.UserLayout, first.Layout.Source);
        Assert.Equal(savedPath, first.FilePath);
        Assert.Equal("Slabwing", first.Layout.Name);
        Assert.Equal(56, first.Layout.Keys.Count);
        Assert.Equal(saved.Keys.Select(k => k.Hand), first.Layout.Hands);
        // The old candidates follow in their old order.
        Assert.Equal(
            new (LayoutSource, string)[]
            {
                (LayoutSource.UserLayout, savedPath),
                (LayoutSource.InfoJson, Path.Combine(root, "config", "info.json")),
                (LayoutSource.MatrixTransform,
                    Path.Combine(root, "boards", "shields", "slabwing", "slabwing.overlay")),
            },
            scan.Candidates.Select(c => (c.Layout.Source, c.FilePath)));
        Assert.Empty(scan.Problems);
    }

    [Fact]
    public void ScanFolder_SavedLayout_CarriesItsId()
    {
        var root = CreateSlabwingTree();
        UserLayoutStore.Save(SavedSlabwingLayout(), Path.Combine(root, "config"));

        var scan = ZmkConfigScanner.ScanFolder(root);

        Assert.Equal("slabwing", scan.Candidates[0].Layout.SavedId);
        Assert.All(scan.Candidates.Skip(1), c => Assert.Null(c.Layout.SavedId));
    }

    [Fact]
    public void ScanFolder_GeneratedDtsiNextToItsLayout_IsNoExtraCandidate()
    {
        var root = CreateSlabwingTree();
        var config = Path.Combine(root, "config");
        UserLayoutStore.Save(SavedSlabwingLayout(), config);
        ZmkPhysicalLayoutWriter.WriteNextTo(SavedSlabwingLayout(), config, createIfMissing: true);

        var scan = ZmkConfigScanner.ScanFolder(root);

        Assert.Equal(
            [LayoutSource.UserLayout, LayoutSource.InfoJson, LayoutSource.MatrixTransform],
            scan.Candidates.Select(c => c.Layout.Source));
        Assert.Empty(scan.Problems);
    }

    [Fact]
    public void ScanFolder_GeneratedDtsiWithoutItsLayout_StaysACandidate()
    {
        var root = CreateSlabwingTree();
        var config = Path.Combine(root, "config");
        ZmkPhysicalLayoutWriter.WriteNextTo(SavedSlabwingLayout(), config, createIfMissing: true);

        var scan = ZmkConfigScanner.ScanFolder(root);

        Assert.Contains(scan.Candidates, c =>
            c.Layout.Source == LayoutSource.PhysicalLayout
            && c.FilePath == Path.Combine(config, "slabwing.dtsi"));
    }

    [Fact]
    public void Rank_SavedLayout_StaysFirstWithMatchingKeyCount()
    {
        var root = CreateSlabwingTree();
        UserLayoutStore.Save(SavedSlabwingLayout(), Path.Combine(root, "config"));

        var ranked = ZmkConfigScanner.Rank(ZmkConfigScanner.ScanFolder(root).Candidates, 56);

        Assert.Equal(
            [LayoutSource.UserLayout, LayoutSource.InfoJson, LayoutSource.MatrixTransform],
            ranked.Select(c => c.Layout.Source));
    }

    [Fact]
    public void ScanFolder_BrokenSavedLayout_IsReportedOnce_AndScanContinues()
    {
        var root = CreateSlabwingTree();
        WriteFile(root, "config/broken.layout.json", "{ this is not json");

        var scan = ZmkConfigScanner.ScanFolder(root);

        var problem = Assert.Single(scan.Problems);
        Assert.StartsWith(
            Path.Combine("config", "broken.layout.json") + ": layout file: ", problem);
        // The other candidates are untouched.
        Assert.Equal(
            [LayoutSource.InfoJson, LayoutSource.MatrixTransform],
            scan.Candidates.Select(c => c.Layout.Source));
    }

    [Fact]
    public void ScanFile_LayoutJson_ReturnsThatOneCandidate()
    {
        var root = CreateSlabwingTree();
        UserLayoutStore.Save(SavedSlabwingLayout(), Path.Combine(root, "config"));
        var savedPath = Path.Combine(root, "config", "slabwing.layout.json");

        var scan = ZmkConfigScanner.ScanFile(savedPath);

        var candidate = Assert.Single(scan.Candidates);
        Assert.Equal(LayoutSource.UserLayout, candidate.Layout.Source);
        Assert.Equal(savedPath, candidate.FilePath);
        Assert.Equal("Slabwing", candidate.Layout.Name);
        Assert.Equal(56, candidate.Layout.Keys.Count);
        Assert.Empty(scan.Problems);
    }

    // --- tree builders ---

    private string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lviz-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    /// <summary>boards/shields/cosmos82 + config: dtsi, Kconfig.defconfig, info.json, keymap.</summary>
    private string CreateCosmosTree()
    {
        var root = CreateTempRoot();
        WriteFile(root, "boards/shields/cosmos82/cosmos82.dtsi", LayoutFixtures.Read("cosmos82.dtsi"));
        WriteFile(root, "boards/shields/cosmos82/Kconfig.defconfig", KconfigDefconfig("Cosmos82"));
        WriteFile(root, "config/info.json", LayoutFixtures.Read("cosmos82.info.json"));
        WriteFile(root, "config/cosmos82.keymap", "/* keymap */");
        return root;
    }

    /// <summary>boards/shields/slabwing + config + tests: overlay, info.json, two keymaps.</summary>
    private string CreateSlabwingTree()
    {
        var root = CreateTempRoot();
        WriteFile(root, "boards/shields/slabwing/slabwing.overlay", LayoutFixtures.Read("slabwing.overlay"));
        WriteFile(root, "boards/shields/slabwing/Kconfig.defconfig", KconfigDefconfig("Slabwing"));
        WriteFile(root, "config/info.json", LayoutFixtures.Read("slabwing.info.json"));
        WriteFile(root, "config/slabwing.keymap", "/* keymap */");
        WriteFile(root, "tests/slabwing_matrix_test.keymap", "/* matrix test keymap */");
        return root;
    }

    /// <summary>A wizard-saved 56-key slabwing layout: 28 left keys, 28 right keys.</summary>
    private static UserLayoutFile SavedSlabwingLayout()
    {
        var keys = new List<UserLayoutKey>();
        for (var i = 0; i < 56; i++)
            keys.Add(new UserLayoutKey(i * 100, 0, 100, 100, 0, 0, 0, i < 28 ? Hand.Left : Hand.Right));
        return new UserLayoutFile(
            UserLayoutFile.CurrentSchemaVersion, "slabwing", "Slabwing", "Slabwing", keys);
    }

    private static string KconfigDefconfig(string name) => $"""
        if SHIELD_COSMOS82_RIGHT
        # Must be less than 16 characters long!
        config ZMK_KEYBOARD_NAME
            default "{name}"

        endif
        """;

    private static void WriteFile(string root, string relative, string content)
    {
        string full = root;
        foreach (var segment in relative.Split('/'))
            full = Path.Combine(full, segment);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
