using LViz.Core.Keymap.Editing;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// <see cref="ComboRules"/> — node-name validity, auto-naming and the
/// error/warning check the combo dialog and the combo writer share.
/// </summary>
public class ComboRulesTests
{
    private static ZmkCombo Combo(
        string? node = "combo_x",
        string name = "X",
        int[]? positions = null,
        int[]? layers = null) =>
        new(name, "", positions ?? new[] { 0, 1 }, layers ?? new[] { -1 },
            new KeyBinding("&kp", new[] { "A" }))
        { NodeName = node };

    // --- IsValidNodeName ---

    [Theory]
    [InlineData("combo_esc")]
    [InlineData("mouse-mid")]
    [InlineData("f12")]
    [InlineData("a")]
    [InlineData("x.y+z,1")]
    public void IsValidNodeName_LegalNames_True(string name) =>
        Assert.True(ComboRules.IsValidNodeName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1combo")]
    [InlineData("_x")]
    [InlineData("combo esc")]
    [InlineData("combo/x")]
    public void IsValidNodeName_IllegalNames_False(string? name) =>
        Assert.False(ComboRules.IsValidNodeName(name));

    [Fact]
    public void IsValidNodeName_ThirtyOneChars_True() =>
        Assert.True(ComboRules.IsValidNodeName(new string('a', ComboRules.MaxNodeNameLength)));

    [Fact]
    public void IsValidNodeName_ThirtyTwoChars_False() =>
        Assert.False(ComboRules.IsValidNodeName(new string('a', ComboRules.MaxNodeNameLength + 1)));

    // --- AutoName ---

    [Fact]
    public void AutoName_KpKeycode_ComboPlusKeycode() =>
        Assert.Equal("combo_F12",
            ComboRules.AutoName(new KeyBinding("&kp", new[] { "F12" }), Array.Empty<string>()));

    [Fact]
    public void AutoName_BehaviorWithoutParams_ComboPlusBehavior() =>
        Assert.Equal("combo_bootloader",
            ComboRules.AutoName(new KeyBinding("&bootloader", Array.Empty<string>()), Array.Empty<string>()));

    [Fact]
    public void AutoName_BehaviorWithParams_ComboPlusBehaviorPlusParams() =>
        Assert.Equal("combo_mo_2",
            ComboRules.AutoName(new KeyBinding("&mo", new[] { "2" }), Array.Empty<string>()));

    [Fact]
    public void AutoName_KpWithFlattenedMods_JoinsAllParams() =>
        Assert.Equal("combo_LC_C",
            ComboRules.AutoName(new KeyBinding("&kp", new[] { "LC", "C" }), Array.Empty<string>()));

    [Fact]
    public void AutoName_NullBinding_PlainCombo() =>
        Assert.Equal("combo", ComboRules.AutoName(null, Array.Empty<string>()));

    [Fact]
    public void AutoName_NameTaken_AppendsTwo() =>
        Assert.Equal("combo_F12_2",
            ComboRules.AutoName(new KeyBinding("&kp", new[] { "F12" }), new[] { "combo_F12" }));

    [Fact]
    public void AutoName_NamesTaken_AppendsFirstFreeNumber() =>
        Assert.Equal("combo_F12_3",
            ComboRules.AutoName(new KeyBinding("&kp", new[] { "F12" }), new[] { "combo_F12", "combo_F12_2" }));

    [Fact]
    public void AutoName_LongName_CutToMaxLength_AndSuffixVariantFits()
    {
        // "combo_" + 34 chars = a 40-character name before the cut.
        var binding = new KeyBinding("&kp", new[] { "ABCDEFGHIJKLMNOPQRSTUVWXYZ01234567" });

        var name = ComboRules.AutoName(binding, Array.Empty<string>());
        Assert.True(name.Length <= ComboRules.MaxNodeNameLength);

        var unique = ComboRules.AutoName(binding, new[] { name });
        Assert.True(unique.Length <= ComboRules.MaxNodeNameLength);
        Assert.NotEqual(name, unique);
    }

    // --- Check ---

    [Fact]
    public void Check_OneKey_TooFewKeys()
    {
        var problems = ComboRules.Check(Combo(positions: new[] { 4 }), Array.Empty<ZmkCombo>());
        Assert.Equal(ComboProblemKind.TooFewKeys, Assert.Single(problems).Kind);
    }

    [Fact]
    public void Check_InvalidNodeName_ReportsInvalidName()
    {
        var problems = ComboRules.Check(Combo(node: "1x"), Array.Empty<ZmkCombo>());
        Assert.Contains(problems, p => p.Kind == ComboProblemKind.InvalidName);
    }

    [Fact]
    public void Check_NameUsedByOther_DuplicateNameWithOtherName()
    {
        var other = Combo(node: "dup", positions: new[] { 7, 8 });

        var problems = ComboRules.Check(Combo(node: "dup"), new[] { other });

        Assert.Contains(problems, p =>
            p.Kind == ComboProblemKind.DuplicateName && p.Other == "dup");
    }

    [Fact]
    public void Check_SameKeysOnSharedLayer_SameKeysAsOther()
    {
        var other = Combo(node: "combo_other", positions: new[] { 1, 0 }, layers: new[] { -1 });

        var problems = ComboRules.Check(
            Combo(node: "combo_this", positions: new[] { 0, 1 }, layers: new[] { 2 }),
            new[] { other });

        Assert.Contains(problems, p =>
            p.Kind == ComboProblemKind.SameKeysAsOther && p.Other == "combo_other");
    }

    [Fact]
    public void Check_SameKeysOnDisjointLayers_NotSameKeysAsOther()
    {
        var other = Combo(node: "combo_other", positions: new[] { 0, 1 }, layers: new[] { 1 });

        var problems = ComboRules.Check(
            Combo(node: "combo_this", positions: new[] { 0, 1 }, layers: new[] { 2 }),
            new[] { other });

        Assert.DoesNotContain(problems, p => p.Kind == ComboProblemKind.SameKeysAsOther);
    }

    [Fact]
    public void Check_FiveKeys_TooManyKeysWarningWithCount()
    {
        var problems = ComboRules.Check(
            Combo(positions: new[] { 0, 1, 2, 3, 4 }), Array.Empty<ZmkCombo>());

        var problem = Assert.Single(problems);
        Assert.Equal(ComboProblemKind.TooManyKeys, problem.Kind);
        Assert.Equal(5, problem.Count);
        Assert.False(problem.IsError);
    }

    [Fact]
    public void Check_KeyInSixCombos_KeyInManyCombosWithKeyAndCount()
    {
        var others = Enumerable.Range(0, 5)
            .Select(i => Combo(node: $"combo_other_{i}", positions: new[] { 3, 50 + i }))
            .ToList();

        var problems = ComboRules.Check(Combo(node: "combo_this", positions: new[] { 3, 4 }), others);

        Assert.Contains(problems, p => p.Kind == ComboProblemKind.KeyInManyCombos
            && p.KeyIndex == 3 && p.Count == 6);
    }

    [Fact]
    public void Check_KeyInFiveCombos_NoWarning()
    {
        var others = Enumerable.Range(0, 4)
            .Select(i => Combo(node: $"combo_other_{i}", positions: new[] { 3, 50 + i }))
            .ToList();

        var problems = ComboRules.Check(Combo(node: "combo_this", positions: new[] { 3, 4 }), others);

        Assert.DoesNotContain(problems, p => p.Kind == ComboProblemKind.KeyInManyCombos);
    }

    [Fact]
    public void Check_ValidComboNoOthers_NoProblems() =>
        Assert.Empty(ComboRules.Check(Combo(), Array.Empty<ZmkCombo>()));

    // --- ComboProblem.IsError ---

    [Theory]
    [InlineData(ComboProblemKind.TooFewKeys, true)]
    [InlineData(ComboProblemKind.InvalidName, true)]
    [InlineData(ComboProblemKind.DuplicateName, true)]
    [InlineData(ComboProblemKind.SameKeysAsOther, false)]
    [InlineData(ComboProblemKind.TooManyKeys, false)]
    [InlineData(ComboProblemKind.KeyInManyCombos, false)]
    public void IsError_TrueExactlyForErrorKinds(ComboProblemKind kind, bool expected) =>
        Assert.Equal(expected, new ComboProblem(kind).IsError);
}
