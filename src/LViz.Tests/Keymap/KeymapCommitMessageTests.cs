using LViz.Core.Keymap.Editing;
using LViz.Core.Models;
using static LViz.Core.Keymap.Editing.LayerStructureEdit;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// <see cref="KeymapCommitMessage.Build"/> over hand-built plans: the
/// <c>LViz: …</c> summary counts each change kind in a fixed order with
/// correct singular/plural, the detail lines run structure → renames → keys,
/// and a long list is cut at <see cref="KeymapCommitMessage.MaxDetailLines"/>
/// with an "… and N more" tail. English only, by decision — these strings are
/// asserted verbatim.
/// </summary>
public class KeymapCommitMessageTests
{
    private const string Arrow = "\u2192"; // →
    private const string Ellipsis = "\u2026"; // …

    private static readonly KeyBinding KpQ = new("&kp", ["Q"]);
    private static readonly KeyBinding KpW = new("&kp", ["W"]);
    private static readonly KeyBinding KpEscape = new("&kp", ["ESCAPE"]);

    private static KeymapSavePlan Plan(
        IReadOnlyList<LayerStructureEdit>? structure = null,
        IReadOnlyList<KeymapBindingChange>? bindings = null,
        IReadOnlyList<KeymapLayerRenameChange>? renames = null) =>
        new("", structure ?? [], bindings ?? [], renames ?? []);

    private static KeymapBindingChange Change(int key, KeyBinding oldBinding, KeyBinding newBinding) =>
        new(0, "Base", key, oldBinding, newBinding);

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
    public void Build_NullPlan_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => KeymapCommitMessage.Build(null!));
    }
}
