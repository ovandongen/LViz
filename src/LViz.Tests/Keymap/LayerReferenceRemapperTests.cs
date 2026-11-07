using LViz.Core.Keymap;
using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using static LViz.Core.Keymap.Editing.LayerStructureEdit;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// The parsed-model half of layer add/delete/move: the layer list changes and
/// every layer reference (layer bindings, hold-taps over layer behaviors,
/// combos, macros) follows. The <c>.keymap</c> writer (cards 2–5) checks its
/// output against this behaviour.
/// </summary>
public class LayerReferenceRemapperTests
{
    private static KeyBinding B(string behavior, params string[] parameters) =>
        new(behavior, parameters);

    /// <summary>KeyBinding record equality ignores Params contents — compare by hand.</summary>
    private static bool SameBinding(KeyBinding a, KeyBinding b) =>
        a.Behavior == b.Behavior && a.Params.SequenceEqual(b.Params);

    /// <summary>
    /// Four layers × three keys. Layer 0 is <c>&amp;mo 1</c>, <c>&amp;lt 3 SPACE</c>,
    /// <c>&amp;kp A</c>; the others are transparent unless overridden.
    /// </summary>
    private static KeyboardConfig FourLayers(
        IReadOnlyList<KeyBinding>? layer2 = null,
        IReadOnlyList<ZmkCombo>? combos = null,
        IReadOnlyList<HoldTap>? holdTaps = null,
        IReadOnlyList<ZmkMacro>? macros = null) =>
        new(
            "test",
            [
                new Layer(0, "Alpha", [B("&mo", "1"), B("&lt", "3", "SPACE"), B("&kp", "A")]),
                new Layer(1, "Beta", [B("&trans"), B("&trans"), B("&trans")]),
                new Layer(2, "Gamma", layer2 ?? [B("&trans"), B("&trans"), B("&trans")]),
                new Layer(3, "Delta", [B("&trans"), B("&trans"), B("&trans")]),
            ],
            macros ?? [],
            holdTaps ?? [],
            combos ?? []);

    private static KeyboardConfig Load(string fixture, string id) =>
        ZmkKeymapLoader.LoadFromText(KeymapFixtures.Read(fixture), id);

    private static int? TargetOf(KeyboardConfig config, KeyBinding binding) =>
        LayerBindingResolver.ResolveTargetLayer(
            binding, config.HoldTaps.FirstOrDefault(h => h.Name == binding.Behavior));

    // ---------- IndexMap ----------

    [Fact]
    public void IndexMap_TheFourExamples()
    {
        Assert.Equal(new int?[] { 3, 0, 1, 2 }, LayerReferenceRemapper.IndexMap(4, new Move(0, 3)));
        Assert.Equal(new int?[] { 0, 2, 3, 1 }, LayerReferenceRemapper.IndexMap(4, new Move(3, 1)));
        // Add yields layerCount entries — the new layer has no old index.
        Assert.Equal(new int?[] { 0, 2, 3 }, LayerReferenceRemapper.IndexMap(3, new Add(1, "New", [])));
        Assert.Equal(new int?[] { 0, null, 1, 2 }, LayerReferenceRemapper.IndexMap(4, new Delete(1)));
    }

    [Fact]
    public void IndexMap_OutOfRangeIndices_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayerReferenceRemapper.IndexMap(4, new Move(4, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayerReferenceRemapper.IndexMap(4, new Move(0, 4)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LayerReferenceRemapper.IndexMap(3, new Add(4, "New", [])));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayerReferenceRemapper.IndexMap(4, new Delete(-1)));
    }

    // ---------- Move ----------

    [Fact]
    public void Move_3_1_RenumbersLayerZeroAndAllIndices()
    {
        var config = FourLayers();
        var result = LayerReferenceRemapper.Apply(config, new Move(3, 1));

        Assert.Equal(new[] { "Alpha", "Delta", "Beta", "Gamma" }, result.Layers.Select(l => l.Name));
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.Layers.Select(l => l.Index));

        var layer0 = result.Layers[0].Bindings;
        Assert.Equal("&mo", layer0[0].Behavior);
        Assert.Equal(new[] { "2" }, layer0[0].Params);          // old 1 → new 2
        Assert.Equal("&lt", layer0[1].Behavior);
        Assert.Equal(new[] { "1", "SPACE" }, layer0[1].Params); // old 3 → new 1
        // A binding without a layer reference keeps its instance.
        Assert.True(SameBinding(B("&kp", "A"), layer0[2]));
        Assert.Same(config.Layers[0].Bindings[2], layer0[2]);
    }

    [Fact]
    public void Move_SameIndex_ReturnsSameInstance()
    {
        var config = FourLayers();
        Assert.Same(config, LayerReferenceRemapper.Apply(config, new Move(1, 1)));
    }

    // ---------- Add ----------

    [Fact]
    public void Add_1_NewLayerUsesOldIndexSpace_AndEverythingShifts()
    {
        var config = FourLayers();
        var result = LayerReferenceRemapper.Apply(
            config, new Add(1, "New", [B("&mo", "2"), B("&trans"), B("&trans")]));

        Assert.Equal(
            new[] { "Alpha", "New", "Beta", "Gamma", "Delta" },
            result.Layers.Select(l => l.Name));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, result.Layers.Select(l => l.Index));

        var added = result.Layers[1];
        Assert.Equal("New", added.Name);
        Assert.Equal(1, added.Index);
        Assert.Equal("&mo", added.Bindings[0].Behavior);
        Assert.Equal(new[] { "3" }, added.Bindings[0].Params); // old 2 → new 3

        var layer0 = result.Layers[0].Bindings;
        Assert.Equal(new[] { "2" }, layer0[0].Params); // old 1 → new 2 (Beta shifted by the insert)
        Assert.Equal(new[] { "4", "SPACE" }, layer0[1].Params); // old 3 → new 4
        Assert.Same(config.Layers[0].Bindings[2], layer0[2]);   // &kp A keeps its instance
    }

    // ---------- Delete ----------

    [Fact]
    public void Delete_3_ClearsLtReference_AndCountClearedSeesItBeforehand()
    {
        var config = FourLayers();
        Assert.Equal(1, LayerReferenceRemapper.CountClearedBindings(config, 3));

        var result = LayerReferenceRemapper.Apply(config, new Delete(3));

        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, result.Layers.Select(l => l.Name));
        Assert.Equal(new[] { 0, 1, 2 }, result.Layers.Select(l => l.Index));

        var layer0 = result.Layers[0].Bindings;
        Assert.Equal("&kp", layer0[1].Behavior); // &lt 3 SPACE → &kp SPACE
        Assert.Equal(new[] { "SPACE" }, layer0[1].Params);
        Assert.Same(config.Layers[0].Bindings[0], layer0[0]); // &mo 1 unchanged
    }

    [Fact]
    public void Delete_3_ToggleElsewhere_BecomesNone()
    {
        var config = FourLayers(layer2: [B("&tog", "3"), B("&trans"), B("&trans")]);
        Assert.Equal(2, LayerReferenceRemapper.CountClearedBindings(config, 3)); // &lt + &tog

        var result = LayerReferenceRemapper.Apply(config, new Delete(3));

        var gamma = result.Layers[2]; // deleting the last layer keeps 0…2 in place
        Assert.Equal("Gamma", gamma.Name);
        Assert.Equal("&none", gamma.Bindings[0].Behavior);
        Assert.Empty(gamma.Bindings[0].Params);
    }

    [Fact]
    public void Delete_ClearedHoldTapOverMo_BecomesNone()
    {
        var magicTap = new HoldTap("&magic", "&mo", "&kp", 1, 1);
        var config = FourLayers(
            layer2: [B("&magic", "3", "0"), B("&trans"), B("&trans")],
            holdTaps: [magicTap]);
        Assert.Equal(2, LayerReferenceRemapper.CountClearedBindings(config, 3)); // &lt + &magic

        var result = LayerReferenceRemapper.Apply(config, new Delete(3));

        Assert.Equal("&none", result.Layers[2].Bindings[0].Behavior);
    }

    [Fact]
    public void Delete_KeepsDecorationsOnClearedBindings()
    {
        var decorated = B("&mo", "3") with { DecorationLabel = "Jump", DecorationIcon = "fa-rocket" };
        var config = new KeyboardConfig(
            "test",
            [
                new Layer(0, "Alpha", [decorated]),
                new Layer(1, "Beta", [B("&trans")]),
                new Layer(2, "Gamma", [B("&trans")]),
                new Layer(3, "Delta", [B("&trans")]),
            ],
            [], [], []);

        var result = LayerReferenceRemapper.Apply(config, new Delete(3));

        var cleared = result.Layers[0].Bindings[0];
        Assert.Equal("&none", cleared.Behavior);
        Assert.Equal("Jump", cleared.DecorationLabel);
        Assert.Equal("fa-rocket", cleared.DecorationIcon);
    }

    [Fact]
    public void Delete_LastLayer_ThrowsInvalidOperation()
    {
        var single = new KeyboardConfig("test", [new Layer(0, "Only", [B("&trans")])], [], [], []);
        Assert.Throws<InvalidOperationException>(
            () => LayerReferenceRemapper.Apply(single, new Delete(0)));
    }

    // ---------- Combos and macros ----------

    [Fact]
    public void Delete_ComboScopedToDeletedLayer_ThrowsNamingCombo()
    {
        var combo = new ZmkCombo("esc", "Escape", [0, 1], [1], B("&kp", "ESC"));
        var config = FourLayers(combos: [combo]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => LayerReferenceRemapper.Apply(config, new Delete(1)));
        Assert.Contains("esc", ex.Message);
    }

    [Fact]
    public void Delete_ComboBindingTargetingDeletedLayer_ThrowsNamingCombo()
    {
        var combo = new ZmkCombo("sys", "", [53, 52], [-1], B("&mo", "1"));
        var config = FourLayers(combos: [combo]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => LayerReferenceRemapper.Apply(config, new Delete(1)));
        Assert.Contains("sys", ex.Message);
    }

    [Fact]
    public void Move_ComboLayersRenumbered_MinusOneStays()
    {
        var combo = new ZmkCombo("esc", "", [0, 1], [-1, 2], B("&kp", "ESC"));
        var config = FourLayers(combos: [combo]);

        var result = LayerReferenceRemapper.Apply(config, new Move(2, 0));

        Assert.Equal(new[] { -1, 0 }, result.Combos[0].Layers);
        Assert.Same(combo.Binding, result.Combos[0].Binding);
    }

    [Fact]
    public void CombosUsingLayer_Slabwing_SeesSyslayerOnLayer2_AndNothingOn1()
    {
        var config = Load("slabwing.keymap", "slabwing");

        Assert.Equal(new[] { "syslayer" }, LayerReferenceRemapper.CombosUsingLayer(config, 2));
        Assert.Empty(LayerReferenceRemapper.CombosUsingLayer(config, 1));
    }

    [Fact]
    public void CombosUsingLayer_LayersList_HeldLayerListsTheCombo()
    {
        var combo = new ZmkCombo("scoped", "", [0, 1], [1, 3], B("&kp", "ESC"));
        var config = FourLayers(combos: [combo]);

        Assert.Equal(new[] { "scoped" }, LayerReferenceRemapper.CombosUsingLayer(config, 1));
        Assert.Equal(new[] { "scoped" }, LayerReferenceRemapper.CombosUsingLayer(config, 3));
        Assert.Empty(LayerReferenceRemapper.CombosUsingLayer(config, 2));
    }

    [Fact]
    public void CombosUsingLayer_ConfigOrder_TargetAndListBothCount()
    {
        var bindTwo = new ZmkCombo("binds", "", [0, 1], [-1], B("&mo", "2"));
        var scopedTwo = new ZmkCombo("scopes", "", [2, 3], [2], B("&kp", "ESC"));
        var config = FourLayers(combos: [bindTwo, scopedTwo]);

        Assert.Equal(new[] { "binds", "scopes" }, LayerReferenceRemapper.CombosUsingLayer(config, 2));
    }

    [Fact]
    public void Delete_MacroBindingTargetingDeletedLayer_ThrowsNamingMacro()
    {
        var macro = new ZmkMacro("&weird", ["x"], [B("&mo", "1")]);
        var config = FourLayers(macros: [macro]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => LayerReferenceRemapper.Apply(config, new Delete(1)));
        Assert.Contains("&weird", ex.Message);
    }

    [Fact]
    public void Move_MacroBindingsRenumbered()
    {
        var macro = new ZmkMacro("&weird", [], [B("&mo", "2"), B("&kp", "A")]);
        var config = FourLayers(macros: [macro]);

        var result = LayerReferenceRemapper.Apply(config, new Move(2, 0));

        Assert.Equal("&mo", result.Macros[0].Bindings[0].Behavior);
        Assert.Equal(new[] { "0" }, result.Macros[0].Bindings[0].Params); // old 2 → new 0
        Assert.Same(macro.Bindings[1], result.Macros[0].Bindings[1]);
    }

    // ---------- Hold-taps ----------

    [Fact]
    public void Move_RenumbersHoldTapOverMo_IgnoresHoldTapOverKp()
    {
        var magicTap = new HoldTap("&magic", "&mo", "&kp", 1, 1);
        var plainTap = new HoldTap("&plain", "&kp", "&kp", 1, 1);
        var config = FourLayers(
            layer2: [B("&magic", "2", "0"), B("&plain", "2", "0"), B("&trans")],
            holdTaps: [magicTap, plainTap]);

        var result = LayerReferenceRemapper.Apply(config, new Move(2, 0));

        var gamma = result.Layers[0]; // old index 2 → position 0 (moved to the front)
        Assert.Equal("Gamma", gamma.Name);
        Assert.Equal("&magic", gamma.Bindings[0].Behavior);
        Assert.Equal(new[] { "0", "0" }, gamma.Bindings[0].Params); // &magic 2 0 → &magic 0 0
        Assert.Same(config.Layers[2].Bindings[1], gamma.Bindings[1]); // hold &kp untouched
    }

    // ---------- Validation, misc ----------

    [Fact]
    public void Apply_Validation()
    {
        var config = FourLayers();

        // Add with the wrong key count.
        Assert.Throws<ArgumentException>(
            () => LayerReferenceRemapper.Apply(config, new Add(1, "New", [B("&trans")])));
        // Index ranges.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LayerReferenceRemapper.Apply(config, new Delete(7)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LayerReferenceRemapper.Apply(config, new Move(-1, 0)));

        // AtIndex == count appends.
        var appended = LayerReferenceRemapper.Apply(
            config, new Add(4, "New", [B("&trans"), B("&trans"), B("&trans")]));
        Assert.Equal("New", appended.Layers[^1].Name);
        Assert.Equal(4, appended.Layers[^1].Index);

        // Carry-overs.
        var moved = LayerReferenceRemapper.Apply(config, new Move(3, 0));
        Assert.Equal("test", moved.KeyboardId);
        Assert.Same(config.HoldTaps, moved.HoldTaps);
    }

    [Fact]
    public void ReferenceOutsideOldLayerSpace_Untouched()
    {
        var config = FourLayers(layer2: [B("&mo", "11"), B("&trans"), B("&trans")]);

        var result = LayerReferenceRemapper.Apply(
            config, new Add(0, "New", [B("&trans"), B("&trans"), B("&trans")]));

        Assert.Same(config.Layers[2].Bindings[0], result.Layers[3].Bindings[0]);
    }

    // ---------- Real fixtures ----------

    [Fact]
    public void Glove80_Move_4_1_EveryTargetFollowsTheIndexMap()
    {
        var config = Load("glove80-editor.keymap", "glove80");
        var edit = new Move(4, 1);
        var map = LayerReferenceRemapper.IndexMap(config.LayerCount, edit);
        var result = LayerReferenceRemapper.Apply(config, edit);

        for (int layer = 0; layer < config.LayerCount; layer++)
        {
            int newPos = map[layer]!.Value;
            var oldBindings = config.Layers[layer].Bindings;
            for (int key = 0; key < oldBindings.Count; key++)
            {
                var oldBinding = oldBindings[key];
                var newBinding = result.Layers[newPos].Bindings[key];
                var oldTarget = TargetOf(config, oldBinding);
                if (oldTarget is int t && t >= 0 && t < config.LayerCount)
                    Assert.Equal(map[t], TargetOf(result, newBinding));
                else
                {
                    Assert.True(SameBinding(oldBinding, newBinding),
                        $"({layer}, {key}) changed but had no in-range layer target");
                    Assert.Same(oldBinding, newBinding);
                }
            }
        }
    }

    [Fact]
    public void Slabwing_Move_2_0_AdjustGoesToTop_ComboRetargets()
    {
        var config = Load("slabwing.keymap", "slabwing");
        var result = LayerReferenceRemapper.Apply(config, new Move(2, 0));

        Assert.Equal(
            new[] { "Adjust", "Base", "Symbols", "Nav", "Nav-alt", "Nav-alt-shift", "Numbers" },
            result.Layers.Select(l => l.Name));

        var original = Assert.Single(config.Combos, c => c.Binding.Behavior == "&mo");
        Assert.Equal(new[] { "2" }, original.Binding.Params);
        var moved = Assert.Single(result.Combos, c => c.Binding.Behavior == "&mo");
        Assert.Equal("syslayer", moved.Name);
        Assert.Equal(new[] { "0" }, moved.Binding.Params); // &mo 2 → &mo 0
    }

    [Fact]
    public void ApplyAll_EmptyReturnsSame_RoundTripRestoresBindings()
    {
        var config = FourLayers();
        Assert.Same(config, LayerReferenceRemapper.ApplyAll(config, []));

        var result = LayerReferenceRemapper.ApplyAll(config, [new Move(0, 2), new Move(2, 0)]);

        Assert.Equal(config.Layers.Select(l => l.Name), result.Layers.Select(l => l.Name));
        for (int layer = 0; layer < config.LayerCount; layer++)
            for (int key = 0; key < config.Layers[layer].Bindings.Count; key++)
                Assert.True(
                    SameBinding(config.Layers[layer].Bindings[key], result.Layers[layer].Bindings[key]),
                    $"binding ({layer}, {key}) differs after the round trip");
    }
}
