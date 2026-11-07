using LViz.Core.Keymap.Editing;
using LViz.Core.Models;
using static LViz.Core.Keymap.Editing.LayerStructureEdit;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// <see cref="KeymapCommitMessage.Build"/> over hand-built plans: the
/// <c>LViz: …</c> summary counts each change kind in a fixed order with
/// correct singular/plural, the detail lines run structure → renames →
/// combos → keys, and a long list is cut at
/// <see cref="KeymapCommitMessage.MaxDetailLines"/> with an "… and N more"
/// tail. English only, by decision — these strings are asserted verbatim.
/// </summary>
public class KeymapCommitMessageTests
{
    private const string Arrow = "\u2192"; // →
    private const string Ellipsis = "\u2026"; // …

    private static readonly KeyBinding KpQ = new("&kp", ["Q"]);
    private static readonly KeyBinding KpW = new("&kp", ["W"]);
    private static readonly KeyBinding KpEscape = new("&kp", ["ESCAPE"]);
    private static readonly KeyBinding KpEsc = new("&kp", ["ESC"]);
    private static readonly KeyBinding KpF12 = new("&kp", ["F12"]);
    private static readonly KeyBinding KpF11 = new("&kp", ["F11"]);
    private static readonly KeyBinding Mclk = new("&mkp", ["MCLK"]);

    private static KeymapSavePlan Plan(
        IReadOnlyList<LayerStructureEdit>? structure = null,
        IReadOnlyList<KeymapBindingChange>? bindings = null,
        IReadOnlyList<KeymapLayerRenameChange>? renames = null) =>
        new("", structure ?? [], bindings ?? [], renames ?? []);

    private static KeymapBindingChange Change(int key, KeyBinding oldBinding, KeyBinding newBinding) =>
        new(0, "Base", key, oldBinding, newBinding);

    private static ZmkCombo ComboNode(string? node, KeyBinding binding, params int[] keys) =>
        new("Combo", "", keys, [-1], binding) { NodeName = node };

    private static KeymapComboChange ComboAdded(ZmkCombo combo) =>
        new(new ComboEdit.Add(combo), null, combo);

    private static KeymapComboChange ComboChanged(ZmkCombo oldCombo, ZmkCombo newCombo) =>
        new(new ComboEdit.Change(oldCombo.NodeName!, newCombo), oldCombo, newCombo);

    private static KeymapComboChange ComboDeleted(ZmkCombo combo) =>
        new(new ComboEdit.Delete(combo.NodeName!), combo, null);

    [Fact]
    public void Build_EmptyPlan_SaysNoChanges()
    {
        Assert.Equal("LViz: no changes", KeymapCommitMessage.Build(Plan()));
    }

    [Fact]
    public void Build_OneBindingChange_SingularSummaryAndOneDetailLine()
    {
        var plan = Plan(bindings: [Change(11, KpQ, KeyBinding.Transparent)]);

        Assert.Equal(
            $"LViz: edit 1 key\n\nBase key 11: &kp Q {Arrow} &trans",
            KeymapCommitMessage.Build(plan));
    }

    [Fact]
    public void Build_BindingsAndRename_RenameLineComesBeforeBindingLines()
    {
        var plan = Plan(
            bindings: [Change(10, KpEscape, KeyBinding.Transparent), Change(11, KpQ, KpW)],
            renames: [new KeymapLayerRenameChange(1, "Symbols", "Sym")]);

        Assert.Equal(
            "LViz: edit 2 keys, rename 1 layer\n\n"
            + $"Rename layer 1: \"Symbols\" {Arrow} \"Sym\"\n"
            + $"Base key 10: &kp ESCAPE {Arrow} &trans\n"
            + $"Base key 11: &kp Q {Arrow} &kp W",
            KeymapCommitMessage.Build(plan));
    }

    [Fact]
    public void Build_AddLayer_DetailsTheNewLayer()
    {
        var plan = Plan(structure: [new Add(7, "Test", [])]);

        Assert.Equal(
            "LViz: add 1 layer\n\nAdd layer \"Test\" at 7",
            KeymapCommitMessage.Build(plan));
    }

    [Fact]
    public void Build_DeleteAndMove_CountedInOrderWithTheirDetailLines()
    {
        var plan = Plan(structure: [new Delete(2), new Move(3, 1)]);

        Assert.Equal(
            "LViz: delete 1 layer, move 1 layer\n\nDelete layer 2\nMove layer 3 to 1",
            KeymapCommitMessage.Build(plan));
    }

    [Fact]
    public void Build_LongList_CutAtTheLimitWithAnEllipsisTail()
    {
        var bindings = Enumerable.Range(0, 25)
            .Select(key => Change(key, KpQ, KeyBinding.Transparent))
            .ToList();

        var lines = KeymapCommitMessage.Build(Plan(bindings: bindings)).Split('\n');

        Assert.Equal(23, lines.Length); // summary + blank + 20 details + tail
        Assert.Equal("LViz: edit 25 keys", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal($"Base key 0: &kp Q {Arrow} &trans", lines[2]);
        Assert.Equal($"Base key 19: &kp Q {Arrow} &trans", lines[^2]);
        Assert.Equal($"{Ellipsis} and 5 more", lines[^1]);
    }

    [Fact]
    public void Build_ComboAdd_CountsAndDetailsIt()
    {
        var plan = Plan() with { ComboChanges = [ComboAdded(ComboNode("combo_esc", KpEsc, 10, 11))] };

        Assert.Equal(
            "LViz: add 1 combo\n\nAdd combo combo_esc (10+11): &kp ESC",
            KeymapCommitMessage.Build(plan));
    }

    [Fact]
    public void Build_KeyEditAndComboDeletes_ComboLinesComeBeforeKeyLines()
    {
        var plan = Plan(bindings: [Change(11, KpQ, KeyBinding.Transparent)]) with
        {
            ComboChanges =
            [
                ComboDeleted(ComboNode("f12", KpF12, 0, 1)),
                ComboDeleted(ComboNode("mouse-mid", Mclk, 46, 47)),
            ],
        };

        Assert.Equal(
            "LViz: edit 1 key, delete 2 combos\n\n"
            + "Delete combo f12 (0+1)\n"
            + "Delete combo mouse-mid (46+47)\n"
            + $"Base key 11: &kp Q {Arrow} &trans",
            KeymapCommitMessage.Build(plan));
    }

    [Fact]
    public void Build_ComboBindingChange_DetailsBothBindings()
    {
        var oldCombo = ComboNode("f12", KpF12, 0, 1);
        var plan = Plan() with
        {
            ComboChanges = [ComboChanged(oldCombo, oldCombo with { Binding = KpF11 })],
        };

        Assert.Equal(
            $"LViz: edit 1 combo\n\nEdit combo f12 (0+1): &kp F12 {Arrow} &kp F11",
            KeymapCommitMessage.Build(plan));
    }

    [Fact]
    public void Build_ComboRenameOnly_DetailsBothNamesAndOneBinding()
    {
        var oldCombo = ComboNode("f12", KpF12, 0, 1);
        var plan = Plan() with
        {
            ComboChanges = [ComboChanged(oldCombo, oldCombo with { NodeName = "combo_f12" })],
        };

        Assert.Equal(
            $"LViz: edit 1 combo\n\nEdit combo f12 {Arrow} combo_f12 (0+1): &kp F12",
            KeymapCommitMessage.Build(plan));
    }

    [Fact]
    public void Build_LongListWithComboLines_StillCutsAtTheLimit()
    {
        var combos = Enumerable.Range(0, 15)
            .Select(i => ComboAdded(ComboNode($"combo_{i}", KpEsc, i, i + 1)))
            .ToList();
        var bindings = Enumerable.Range(0, 10)
            .Select(key => Change(key, KpQ, KeyBinding.Transparent))
            .ToList();

        var lines = KeymapCommitMessage.Build(
            Plan(bindings: bindings) with { ComboChanges = combos }).Split('\n');

        Assert.Equal(23, lines.Length); // summary + blank + 20 details + tail
        Assert.Equal("LViz: edit 10 keys, add 15 combos", lines[0]);
        Assert.Equal("Add combo combo_0 (0+1): &kp ESC", lines[2]);
        Assert.Equal("Add combo combo_14 (14+15): &kp ESC", lines[16]);
        Assert.Equal($"Base key 0: &kp Q {Arrow} &trans", lines[17]);
        Assert.Equal($"Base key 4: &kp Q {Arrow} &trans", lines[^2]);
        Assert.Equal($"{Ellipsis} and 5 more", lines[^1]);
    }

    [Fact]
    public void Build_NullPlan_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => KeymapCommitMessage.Build(null!));
    }
}
