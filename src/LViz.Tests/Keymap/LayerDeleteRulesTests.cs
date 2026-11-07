using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using static LViz.Core.Keymap.Editing.LayerStructureEdit;
using Xunit;

namespace LViz.Tests.Keymap;

public class LayerDeleteRulesTests
{
    private static KeyboardConfig Parse(string fixture) =>
        ZmkKeymapLoader.LoadFromText(KeymapFixtures.Read(fixture), "rules-test");

    [Fact]
    public void Check_OutOfRange_Throws()
    {
        var config = Parse("cosmos82.keymap");

        Assert.Throws<ArgumentOutOfRangeException>(() => LayerDeleteRules.Check(config, [], -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LayerDeleteRules.Check(config, [], config.Layers.Count));
    }

    [Fact]
    public void Check_ClearedCount_IsTheRemappers()
    {
        var config = Parse("slabwing.keymap");
        // the cleared-binding count is the remapper's, whatever the combos say
        var check = LayerDeleteRules.Check(config, [], 1);
        Assert.Equal(LayerReferenceRemapper.CountClearedBindings(config, 1), check.ClearedBindings);
    }

    [Fact]
    public void Check_ComboBlocksBeforeTextUses_InGivenOrder()
    {
        var config = Parse("slabwing.keymap");
        var uses = new LayerTextUse[]
        {
            new(2, LayerDeleteBlockerKind.Other, "n", 10),
            new(2, LayerDeleteBlockerKind.DefineUsage, "LAYER_X", 20),
            new(1, LayerDeleteBlockerKind.Other, "other-layer", 30),
        };

        var check = LayerDeleteRules.Check(config, uses, 2);

        Assert.Equal(LayerDeleteBlockerKind.Combo, check.Blockers[0].Kind);
        var tail = check.Blockers.Skip(check.Blockers.Count - 2).ToList();
        Assert.Equal(new[] { LayerDeleteBlockerKind.Other, LayerDeleteBlockerKind.DefineUsage },
            tail.Select(b => b.Kind));
        Assert.DoesNotContain(check.Blockers, b => b.Name == "other-layer");
    }

    [Fact]
    public void Check_SingleLayer_IsLastLayerBlocker()
    {
        var config = Parse("minimal.keymap");
        Assert.Single(config.Layers);

        var check = LayerDeleteRules.Check(config, [], 0);

        Assert.Equal(LayerDeleteBlockerKind.LastLayer, check.Blockers[0].Kind);
        Assert.Equal(config.Layers[0].Name, check.Blockers[0].Name);
    }

    [Fact]
    public void ClearedEdits_MatchExpectedModel_AndSkipDeletedLayer()
    {
        var config = Parse("cosmos82.keymap");
        var expected = LayerReferenceRemapper.Apply(config, new Delete(1));

        var edits = LayerDeleteRules.ClearedEdits(config, expected, 1);

        Assert.Equal(LayerReferenceRemapper.CountClearedBindings(config, 1), edits.Count);
        Assert.DoesNotContain(edits, e => e.LayerIndex == 1);
        Assert.All(edits, e => Assert.True(e.Binding.SameAs(
            expected.Layers[e.LayerIndex < 1 ? e.LayerIndex : e.LayerIndex - 1].Bindings[e.KeyIndex])));
    }

    [Fact]
    public void DescribeBlockers_NamesEveryBlocker()
    {
        var config = Parse("cosmos82.keymap");
        var text = LayerDeleteRules.DescribeBlockers(config, 1,
        [
            new(LayerDeleteBlockerKind.Combo, "c1", 0),
            new(LayerDeleteBlockerKind.DefineUsage, "LAYER_Sys", 7),
        ]);

        Assert.Contains("combo 'c1'", text, StringComparison.Ordinal);
        Assert.Contains("define 'LAYER_Sys'", text, StringComparison.Ordinal);
        Assert.Contains("line 7", text, StringComparison.Ordinal);
    }

    /// <summary>The invariant: CheckDelete refuses exactly the deletes Apply refuses.</summary>
    [Theory]
    [InlineData("slabwing.keymap")]
    [InlineData("cosmos82.keymap")]
    [InlineData("glove80.keymap")]
    [InlineData("go60.keymap")]
    [InlineData("glove80-editor.keymap")]
    [InlineData("corne.keymap")]
    [InlineData("with_combos_macros.keymap")]
    [InlineData("minimal.keymap")]
    public void CheckDelete_RefusesExactlyWhatApplyRefuses(string fixture)
    {
        var source = KeymapFixtures.Read(fixture);
        int count = ZmkKeymapLoader.LoadFromText(source, "rules-test").Layers.Count;

        for (int index = 0; index < count; index++)
        {
            var check = KeymapLayerWriter.CheckDelete(source, index);
            if (check.CanDelete)
            {
                KeymapLayerWriter.Apply(source, new Delete(index));
            }
            else
            {
                var ex = Assert.Throws<InvalidOperationException>(
                    () => KeymapLayerWriter.Apply(source, new Delete(index)));
                Assert.Contains("cannot be deleted", ex.Message, StringComparison.Ordinal);
            }
        }
    }
}
