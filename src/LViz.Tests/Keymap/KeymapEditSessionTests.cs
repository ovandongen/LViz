using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// The in-memory edit model: baseline vs current, one undo step per user action,
/// and what still differs from the baseline.
/// </summary>
public class KeymapEditSessionTests
{
    private static KeyBinding Bind(string behavior, params string[] parameters) => new(behavior, parameters);

    /// <summary>3 layers × 4 keys, every cell distinct, plus macros/hold-taps/combos
    /// so instance identity is worth asserting.</summary>
    private static KeyboardConfig Config()
    {
        var layers = Enumerable.Range(0, 3).Select(i => new Layer(
            i,
            $"layer{i}",
            Enumerable.Range(0, 4).Select(k => Bind("&kp", $"K{i}{k}")).ToArray()
        )).ToList();

        return new KeyboardConfig(
            "test",
            layers,
            [new ZmkMacro("&vim_quit", ["layer"], [Bind("&kt", "TOGGLE")])],
            [new HoldTap("&ht_symbol", "&mo_symbol_f16signal", "&kp", 1, 1)],
            [new ZmkCombo("F12", "", [16, 19], [-1], Bind("&kp", "F12"))]);
    }

    /// <summary>The same config with these layers in it, exactly as a caller that
    /// rebuilt the layer list has to hand it over: <c>Index</c> equal to position.</summary>
    private static KeyboardConfig WithLayers(KeyboardConfig config, params Layer[] layers) =>
        config with { Layers = layers.Select((l, i) => l.Index == i ? l : l with { Index = i }).ToList() };

    /// <summary>A 4-key layer of its own, ready to insert into <see cref="Config"/>.</summary>
    private static Layer Added(string name, string keyPrefix = "N") =>
        new(0, name, Enumerable.Range(0, 4).Select(k => Bind("&kp", $"{keyPrefix}{k}")).ToArray());

    /// <summary>The slabwing fixture: 4 combos — combo_bootloader, mouse-mid,
    /// f12, syslayer — the base for the combo-edit tests.</summary>
    private static KeyboardConfig Slabwing() =>
        ZmkKeymapLoader.LoadFromText(KeymapFixtures.Read("slabwing.keymap"), "fixture");

    /// <summary>A combo that fits no baseline fixture list: node combo_esc, ESC at 0+1.</summary>
    private static ZmkCombo EscCombo() =>
        new("esc", "", [0, 1], [-1], Bind("&kp", "ESCAPE")) { NodeName = "combo_esc" };

    // ---- a fresh session --------------------------------------------------------

    [Fact]
    public void NewSession_CurrentIsBaseline_WithNothingToUndo()
    {
        var config = Config();
        var session = new KeymapEditSession(config);

        Assert.Same(config, session.Baseline);
        Assert.Same(config, session.Current);
        Assert.Equal(0, session.ChangeCount);
        Assert.False(session.HasPendingChanges);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
        Assert.False(session.Undo());
        Assert.False(session.Redo());
    }

    [Fact]
    public void Constructor_NullBaseline_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new KeymapEditSession(null!));
    }

    // ---- binding edits ----------------------------------------------------------

    [Fact]
    public void SetBindings_TwoCells_WritesBothAsOneStep()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var raised = 0;
        session.Changed += (_, _) => raised++;

        var accepted = session.SetBindings([
            new BindingChange(0, 1, Bind("&kp", "A")),
            new BindingChange(1, 2, Bind("&mo", "2"))]);

        Assert.True(accepted);
        Assert.Equal("&kp A", session.Current.Layers[0].Bindings[1].Display);
        Assert.Equal("&mo 2", session.Current.Layers[1].Bindings[2].Display);
        Assert.Equal(2, session.ChangeCount);
        Assert.True(session.HasPendingChanges);
        Assert.True(session.IsKeyChanged(0, 1));
        Assert.True(session.IsKeyChanged(1, 2));
        Assert.False(session.IsKeyChanged(0, 0));
        Assert.False(session.IsKeyChanged(0, 2));
        Assert.Equal(1, raised);
        Assert.True(session.CanUndo);
        Assert.False(session.CanRedo);

        // the rest of the config is carried over by reference
        Assert.NotSame(config, session.Current);
        Assert.Same(config.Layers[2], session.Current.Layers[2]);
        Assert.Same(config.Macros, session.Current.Macros);
        Assert.Same(config.HoldTaps, session.Current.HoldTaps);
        Assert.Same(config.Combos, session.Current.Combos);
    }

    [Fact]
    public void SetBindings_ChangeEqualToCurrent_IsDroppedFromTheStep()
    {
        var session = new KeymapEditSession(Config());

        var accepted = session.SetBindings([
            new BindingChange(0, 0, Bind("&kp", "A")),
            new BindingChange(0, 1, Bind("&kp", "K01")), // already the current binding
            new BindingChange(0, 2, Bind("&kp", "C"))]);

        Assert.True(accepted);
        Assert.Equal(2, session.ChangeCount);

        // one undo restores everything the step wrote
        Assert.True(session.Undo());
        Assert.Equal(0, session.ChangeCount);
        Assert.Equal("&kp K00", session.Current.Layers[0].Bindings[0].Display);
        Assert.Equal("&kp K01", session.Current.Layers[0].Bindings[1].Display);
        Assert.Equal("&kp K02", session.Current.Layers[0].Bindings[2].Display);
    }

    [Theory]
    [InlineData(0, 0, "&kp", "K00")]
    [InlineData(2, 3, "&kp", "K23")]
    public void SetBindings_OnlyNoOps_RecordsNothing(int layer, int key, string behavior, params string[] parameters)
    {
        var session = new KeymapEditSession(Config());
        var before = session.Current;
        var raised = 0;
        session.Changed += (_, _) => raised++;

        Assert.False(session.SetBindings([new BindingChange(layer, key, Bind(behavior, parameters))]));
        Assert.False(session.SetBindings([]));

        Assert.False(session.CanUndo);
        Assert.Equal(0, session.ChangeCount);
        Assert.Same(before, session.Current);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void SetBindings_SameParamsInANewList_IsANoOp()
    {
        var session = new KeymapEditSession(Config());
        var current = session.Current.Layers[0].Bindings[0];

        var sameButFresh = new KeyBinding(current.Behavior, [.. current.Params]);
        Assert.NotSame(current.Params, sameButFresh.Params);

        Assert.False(session.SetBindings([new BindingChange(0, 0, sameButFresh)]));
        Assert.Equal(0, session.ChangeCount);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void SetBindings_DuplicateCell_ThrowsAndChangesNothing()
    {
        var session = new KeymapEditSession(Config());
        var before = session.Current;

        var ex = Assert.Throws<ArgumentException>(() => session.SetBindings([
            new BindingChange(1, 1, Bind("&kp", "A")),
            new BindingChange(1, 1, Bind("&kp", "B"))]));

        Assert.Contains("layer 1, key 1", ex.Message, StringComparison.Ordinal);
        Assert.Same(before, session.Current);
        Assert.Equal(0, session.ChangeCount);
        Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3, 0)]  // 3 layers
    [InlineData(0, -1)]
    [InlineData(0, 4)]  // 4 keys
    public void SetBindings_IndexOutOfRange_Throws(int layer, int key)
    {
        var session = new KeymapEditSession(Config());
        var before = session.Current;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => session.SetBindings([new BindingChange(layer, key, Bind("&kp", "A"))]));

        Assert.Same(before, session.Current);
        Assert.False(session.CanUndo);
    }

    // ---- undo / redo ------------------------------------------------------------

    [Fact]
    public void Undo_RestoresTheOldBinding_RedoWritesTheNewOne()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var oldBinding = config.Layers[1].Bindings[2];
        var newBinding = Bind("&mo", "2");
        var raised = 0;
        session.Changed += (_, _) => raised++;

        session.SetBindings([new BindingChange(1, 2, newBinding)]);
        Assert.Equal(1, raised);

        Assert.True(session.Undo());
        Assert.Same(oldBinding, session.Current.Layers[1].Bindings[2]);
        Assert.False(session.IsKeyChanged(1, 2));
        Assert.Equal(0, session.ChangeCount);
        Assert.False(session.HasPendingChanges);
        Assert.True(session.CanRedo);

        Assert.True(session.Redo());
        Assert.Same(newBinding, session.Current.Layers[1].Bindings[2]);
        Assert.True(session.IsKeyChanged(1, 2));
        Assert.Equal(1, session.ChangeCount);
        Assert.True(session.CanUndo);
        Assert.False(session.CanRedo);
        Assert.Equal(3, raised);
    }

    [Fact]
    public void EditBackToTheBaselineValue_ReadsCleanButStaysUndoable()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var baselineBinding = config.Layers[0].Bindings[0];

        session.SetBindings([new BindingChange(0, 0, Bind("&kp", "A"))]);
        Assert.Equal(1, session.ChangeCount);

        session.SetBindings([new BindingChange(0, 0, baselineBinding)]);

        Assert.Equal(0, session.ChangeCount);
        Assert.False(session.HasPendingChanges);
        Assert.True(session.CanUndo);

        // the two steps are still separate: one undo lands on the intermediate value
        Assert.True(session.Undo());
        Assert.Equal("&kp A", session.Current.Layers[0].Bindings[0].Display);
        Assert.Equal(1, session.ChangeCount);
    }

    [Fact]
    public void NewEditAfterUndo_ClearsTheRedoHistory()
    {
        var session = new KeymapEditSession(Config());
        session.SetBindings([new BindingChange(0, 0, Bind("&kp", "A"))]);

        Assert.True(session.Undo());
        Assert.True(session.CanRedo);

        Assert.True(session.SetBindings([new BindingChange(1, 1, Bind("&kp", "B"))]));

        Assert.False(session.CanRedo);
        Assert.False(session.Redo());
    }

    // ---- layer renames ----------------------------------------------------------

    [Fact]
    public void RenameLayer_WritesName_UndoRestoresIt()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var before = config.Layers[1];
        var raised = 0;
        session.Changed += (_, _) => raised++;

        Assert.True(session.RenameLayer(1, "gaming"));

        Assert.Equal("gaming", session.Current.Layers[1].Name);
        Assert.Same(before.Bindings, session.Current.Layers[1].Bindings); // only the name moved
        Assert.Same(config.Layers[0], session.Current.Layers[0]);
        Assert.True(session.IsLayerRenamed(1));
        Assert.False(session.IsLayerRenamed(0));
        Assert.Equal(1, session.ChangeCount);
        Assert.True(session.HasPendingChanges);
        Assert.Equal(1, raised);

        Assert.True(session.Undo());
        Assert.Equal(before.Name, session.Current.Layers[1].Name);
        Assert.False(session.IsLayerRenamed(1));
        Assert.Equal(0, session.ChangeCount);

        Assert.True(session.Redo());
        Assert.Equal("gaming", session.Current.Layers[1].Name);
        Assert.Equal(1, session.ChangeCount);
    }

    [Fact]
    public void RenameLayer_SameName_RecordsNothing()
    {
        var session = new KeymapEditSession(Config());
        var before = session.Current;
        var raised = 0;
        session.Changed += (_, _) => raised++;

        Assert.False(session.RenameLayer(0, "layer0"));

        Assert.Same(before, session.Current);
        Assert.Equal(0, session.ChangeCount);
        Assert.False(session.CanUndo);
        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sym\nlayer")]
    [InlineData("sym\tlayer")]
    public void RenameLayer_BlankOrControlName_Throws(string? name)
    {
        var session = new KeymapEditSession(Config());
        var before = session.Current;

        Assert.Throws<ArgumentException>(() => session.RenameLayer(1, name!));

        Assert.Same(before, session.Current);
        Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void RenameLayer_LayerOutOfRange_Throws(int layer)
    {
        var session = new KeymapEditSession(Config());
        var before = session.Current;

        Assert.Throws<ArgumentOutOfRangeException>(() => session.RenameLayer(layer, "gaming"));

        Assert.Same(before, session.Current);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ChangeCount_CountsRenamedLayersAndChangedKeysTogether()
    {
        var session = new KeymapEditSession(Config());

        session.RenameLayer(0, "work");
        session.SetBindings([
            new BindingChange(0, 1, Bind("&kp", "A")),
            new BindingChange(2, 2, Bind("&kp", "B"))]);

        Assert.Equal(3, session.ChangeCount);
        Assert.True(session.IsLayerRenamed(0));
        Assert.True(session.IsKeyChanged(0, 1));
        Assert.True(session.IsKeyChanged(2, 2));

        session.RenameLayer(0, "layer0"); // renamed, then renamed back
        Assert.False(session.IsLayerRenamed(0));
        Assert.Equal(2, session.ChangeCount);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(3, 0)]
    [InlineData(0, 4)]
    public void IsKeyChanged_IndexOutOfRange_ReadsAsUnchanged(int layer, int key)
    {
        var session = new KeymapEditSession(Config());
        session.SetBindings([new BindingChange(0, 0, Bind("&kp", "A"))]);

        Assert.False(session.IsKeyChanged(layer, key));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void IsLayerRenamed_IndexOutOfRange_ReadsAsUnchanged(int layer)
    {
        var session = new KeymapEditSession(Config());
        session.RenameLayer(0, "work");

        Assert.False(session.IsLayerRenamed(layer));
    }

    // ---- layer indices ----------------------------------------------------------

    [Fact]
    public void TouchedLayerIndexFollowsPosition_EvenWhenTheBaselineDisagrees()
    {
        // What the structural edits of part 2 lean on: a touched layer gets its
        // Index rewritten to the position it is written at, so the
        // "Index == position" invariant of Layer survives layers moving.
        var layers = new List<Layer>
        {
            new(5, "layerA", [Bind("&kp", "A"), Bind("&kp", "B")]),
            new(9, "layerB", [Bind("&kp", "C"), Bind("&kp", "D")]),
        };
        var config = new KeyboardConfig("test", layers, [], [], []);
        var session = new KeymapEditSession(config);

        session.SetBindings([new BindingChange(0, 0, Bind("&kp", "Z"))]);
        session.RenameLayer(1, "renamed");

        Assert.Equal(0, session.Current.Layers[0].Index);
        Assert.Equal(1, session.Current.Layers[1].Index);
        Assert.Equal("renamed", session.Current.Layers[1].Name);
        Assert.Equal("&kp Z", session.Current.Layers[0].Bindings[0].Display);
        Assert.Equal(2, session.ChangeCount); // the index gap itself is not a change
    }

    // ---- structural edits: add / delete / move ---------------------------------

    [Fact]
    public void ApplyStructure_Add_InsertsTheLayerAsOneStep()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added("extra");
        var after = WithLayers(config, config.Layers[0], added, config.Layers[1], config.Layers[2]);
        var edit = new LayerStructureEdit.Add(1, "extra", added.Bindings);
        var raised = 0;
        session.Changed += (_, _) => raised++;

        Assert.True(session.ApplyStructure(edit, after));

        Assert.Same(after, session.Current);
        Assert.Equal(4, session.Current.Layers.Count);
        Assert.Equal("extra", session.Current.Layers[1].Name);
        Assert.True(session.IsLayerAdded(1));
        Assert.False(session.IsLayerAdded(0));
        Assert.False(session.IsLayerAdded(2));
        Assert.Equal(1, session.ChangeCount); // the layer itself; its own keys read as unchanged
        Assert.False(session.IsKeyChanged(1, 0));
        AssertSameEdit(edit, Assert.Single(session.PendingStructureEdits));
        Assert.Equal(1, raised);
        Assert.True(session.CanUndo);
        Assert.False(session.CanRedo);

        Assert.True(session.Undo());
        Assert.Same(config, session.Current);
        Assert.False(session.IsLayerAdded(1));
        Assert.Equal(0, session.ChangeCount);
        Assert.Empty(session.PendingStructureEdits);
        Assert.True(session.CanRedo);

        Assert.True(session.Redo());
        Assert.Same(after, session.Current);
        Assert.True(session.IsLayerAdded(1));
        Assert.Equal(1, session.ChangeCount);
        AssertSameEdit(edit, Assert.Single(session.PendingStructureEdits));
        Assert.Equal(3, raised);
    }

    [Fact]
    public void ApplyStructure_AddAtTheEnd_AppendsTheLayer()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added("extra");
        var after = WithLayers(config, config.Layers[0], config.Layers[1], config.Layers[2], added);

        Assert.True(session.ApplyStructure(new LayerStructureEdit.Add(3, "extra", added.Bindings), after));

        Assert.True(session.IsLayerAdded(3));
        Assert.Equal("extra", session.Current.Layers[3].Name);
        Assert.Equal(1, session.ChangeCount);
    }

    [Fact]
    public void ApplyStructure_Add_ThenEditsOnTheAddedAndTheShiftedLayer()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added("extra");
        var after = WithLayers(config, config.Layers[0], added, config.Layers[1], config.Layers[2]);
        session.ApplyStructure(new LayerStructureEdit.Add(1, "extra", added.Bindings), after);

        // the added layer is measured against what it was added with
        Assert.True(session.SetBindings([new BindingChange(1, 0, Bind("&kp", "Z"))]));
        Assert.True(session.IsKeyChanged(1, 0));
        Assert.False(session.IsKeyChanged(1, 1));
        Assert.Equal(2, session.ChangeCount); // the added layer + this key

        // baseline layer 2 sits at index 3 now and still tracks its own origin
        Assert.True(session.SetBindings([new BindingChange(3, 1, Bind("&kp", "W"))]));
        Assert.True(session.IsKeyChanged(3, 1));
        Assert.False(session.IsKeyChanged(2, 1));
        Assert.Equal(3, session.ChangeCount);

        // undoing that key lands back on the same shifted position
        Assert.True(session.Undo());
        Assert.False(session.IsKeyChanged(3, 1));
        Assert.Equal("&kp K21", session.Current.Layers[3].Bindings[1].Display);
        Assert.Equal(2, session.ChangeCount);
    }

    [Fact]
    public void ApplyStructure_Delete_CountsTheMissingLayer_UndoRestoresIt()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var after = WithLayers(config, config.Layers[0], config.Layers[1]);
        var edit = new LayerStructureEdit.Delete(2);

        Assert.True(session.ApplyStructure(edit, after));

        Assert.Same(after, session.Current);
        Assert.Equal(2, session.Current.Layers.Count);
        Assert.Equal(1, session.ChangeCount);
        AssertSameEdit(edit, Assert.Single(session.PendingStructureEdits));
        Assert.False(session.IsLayerAdded(0));
        Assert.False(session.IsLayerAdded(2)); // outside the keymap now

        Assert.True(session.Undo());
        Assert.Same(config, session.Current);
        Assert.Equal(0, session.ChangeCount);
        Assert.Empty(session.PendingStructureEdits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ApplyStructure_DeleteAnEarlierLayer_CountsOnlyTheDeletion(int index)
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var after = WithLayers(config, config.Layers.Where((_, i) => i != index).ToArray());

        Assert.True(session.ApplyStructure(new LayerStructureEdit.Delete(index), after));

        // the layers after the gap shift down, but their relative order is unchanged
        Assert.Equal(1, session.ChangeCount);
    }

    [Fact]
    public void ApplyStructure_DeleteThenMoveTheRest_CountsTheDeletionAndTheOrder()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var afterDelete = WithLayers(config, config.Layers[1], config.Layers[2]);
        session.ApplyStructure(new LayerStructureEdit.Delete(0), afterDelete);

        var afterMove = WithLayers(config, config.Layers[2], config.Layers[1]);
        Assert.True(session.ApplyStructure(new LayerStructureEdit.Move(1, 0), afterMove));

        Assert.Equal(2, session.ChangeCount);
    }

    [Fact]
    public void ApplyStructure_DeleteTheRenamedLayer_LeavesOnlyTheDeletion()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        session.RenameLayer(2, "gone");
        Assert.Equal(1, session.ChangeCount);

        var after = WithLayers(config, config.Layers[0], config.Layers[1]);
        Assert.True(session.ApplyStructure(new LayerStructureEdit.Delete(2), after));

        // the rename went away with the layer; what is left is the missing baseline layer
        Assert.Equal(1, session.ChangeCount);
        Assert.Equal("layer1", session.Current.Layers[1].Name);
    }

    [Fact]
    public void ApplyStructure_AddThenDeleteItAgain_LeavesNothingStructuralPending()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added("extra");
        var withAdded = WithLayers(config, config.Layers[0], added, config.Layers[1], config.Layers[2]);
        session.ApplyStructure(new LayerStructureEdit.Add(1, "extra", added.Bindings), withAdded);

        var withoutIt = WithLayers(config, config.Layers[0], config.Layers[1], config.Layers[2]);
        Assert.True(session.ApplyStructure(new LayerStructureEdit.Delete(1), withoutIt));

        Assert.Equal(0, session.ChangeCount); // the ids are the baseline's again
        Assert.False(session.HasPendingChanges);
        Assert.Empty(session.PendingStructureEdits);
        Assert.False(session.IsLayerAdded(1));
        Assert.True(session.CanUndo); // the steps are still there to walk back
    }

    [Fact]
    public void ApplyStructure_Move_CountsOnce_AndTheMoveBackClearsIt()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var afterMove = WithLayers(config, config.Layers[1], config.Layers[2], config.Layers[0]);
        var move = new LayerStructureEdit.Move(0, 2);

        Assert.True(session.ApplyStructure(move, afterMove));

        Assert.Same(afterMove, session.Current);
        Assert.Equal(1, session.ChangeCount); // the order, nothing else
        Assert.Equal(move, Assert.Single(session.PendingStructureEdits));

        var back = WithLayers(config, config.Layers[0], config.Layers[1], config.Layers[2]);
        Assert.True(session.ApplyStructure(new LayerStructureEdit.Move(2, 0), back));

        Assert.Same(back, session.Current);
        Assert.Equal(0, session.ChangeCount);
        Assert.False(session.HasPendingChanges);
        Assert.Empty(session.PendingStructureEdits);
        Assert.True(session.CanUndo);
    }

    [Fact]
    public void ApplyStructure_MoveToTheSameIndex_RecordsNothing()
    {
        var session = new KeymapEditSession(Config());
        var before = session.Current;
        var raised = 0;
        session.Changed += (_, _) => raised++;

        Assert.False(session.ApplyStructure(new LayerStructureEdit.Move(1, 1), before));

        Assert.Same(before, session.Current);
        Assert.Equal(0, session.ChangeCount);
        Assert.Empty(session.PendingStructureEdits);
        Assert.False(session.CanUndo);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void RenameLayer_FollowsTheLayerAcrossAMove()
    {
        var config = Config();
        var session = new KeymapEditSession(config);

        Assert.True(session.RenameLayer(0, "work"));
        Assert.Equal(1, session.ChangeCount);

        var afterMove = WithLayers(config, config.Layers[1], config.Layers[2], config.Layers[0] with { Name = "work" });
        Assert.True(session.ApplyStructure(new LayerStructureEdit.Move(0, 2), afterMove));

        Assert.True(session.IsLayerRenamed(2));
        Assert.False(session.IsLayerRenamed(0));
        Assert.Equal(2, session.ChangeCount); // the rename, once, plus the order

        Assert.True(session.Undo());
        Assert.True(session.IsLayerRenamed(0));
        Assert.False(session.IsLayerRenamed(2));
        Assert.Equal(1, session.ChangeCount);
    }

    [Fact]
    public void PendingStructureEdits_AreTheNetEdits_NotTheHistory()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added("extra");
        var withAdded = WithLayers(config, added, config.Layers[0], config.Layers[1], config.Layers[2]);
        var add = new LayerStructureEdit.Add(0, "extra", added.Bindings);
        Assert.True(session.ApplyStructure(add, withAdded));

        session.SetBindings([new BindingChange(0, 0, Bind("&kp", "A"))]); // on the added layer

        var moved = new LayerStructureEdit.Move(0, 3);
        var afterMove = WithLayers(
            config, config.Layers[0], config.Layers[1], config.Layers[2], session.Current.Layers[0]);
        Assert.True(session.ApplyStructure(moved, afterMove));

        // add-at-0 then move-to-the-end is one add at the end, with the bindings
        // it was added with (the key edit comes out of the binding diff)
        AssertSameEdit(
            new LayerStructureEdit.Add(3, "extra", added.Bindings),
            Assert.Single(session.PendingStructureEdits));

        // the binding step rode along: undoing the move puts the layer back under it
        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.False(session.IsKeyChanged(0, 0));
        Assert.Equal("&kp N0", session.Current.Layers[0].Bindings[0].Display);
    }

    [Fact]
    public void PendingStructureEdits_AMoveTheUserReversedByHand_IsDropped()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added("extra");
        Assert.True(session.ApplyStructure(
            new LayerStructureEdit.Add(3, "extra", added.Bindings),
            WithLayers(config, config.Layers[0], config.Layers[1], config.Layers[2], added)));
        var c = session.Current;
        Assert.True(session.ApplyStructure(
            new LayerStructureEdit.Move(1, 2), WithLayers(c, c.Layers[0], c.Layers[2], c.Layers[1], c.Layers[3])));
        c = session.Current;
        Assert.True(session.ApplyStructure(
            new LayerStructureEdit.Move(2, 1), WithLayers(c, c.Layers[0], c.Layers[2], c.Layers[1], c.Layers[3])));

        AssertSameEdit(
            new LayerStructureEdit.Add(3, "extra", added.Bindings),
            Assert.Single(session.PendingStructureEdits));
    }

    [Fact]
    public void PendingStructureEdits_DeletesComeHighestFirst_InBaselineNumbers_ThenMovesThenAdds()
    {
        var config = WithLayers(Config(), Config().Layers[0], Config().Layers[1], Config().Layers[2],
            Added("d", "D"), Added("e", "E"));
        var session = new KeymapEditSession(config);
        var added = Added("extra");
        var l = config.Layers;

        // delete 1 → [0,2,3,4]; delete (now) 2 = baseline 3 → [0,2,4]
        Assert.True(session.ApplyStructure(new LayerStructureEdit.Delete(1), WithLayers(config, l[0], l[2], l[3], l[4])));
        Assert.True(session.ApplyStructure(new LayerStructureEdit.Delete(2), WithLayers(config, l[0], l[2], l[4])));
        // move 4 to the front → [4,0,2]; add at 1 → [4,extra,0,2]
        Assert.True(session.ApplyStructure(new LayerStructureEdit.Move(2, 0), WithLayers(config, l[4], l[0], l[2])));
        Assert.True(session.ApplyStructure(
            new LayerStructureEdit.Add(1, "extra", added.Bindings), WithLayers(config, l[4], added, l[0], l[2])));

        var edits = session.PendingStructureEdits;
        Assert.Equal(4, edits.Count);
        Assert.Equal(new LayerStructureEdit.Delete(3), edits[0]);
        Assert.Equal(new LayerStructureEdit.Delete(1), edits[1]);
        Assert.Equal(new LayerStructureEdit.Move(2, 0), edits[2]);
        AssertSameEdit(new LayerStructureEdit.Add(1, "extra", added.Bindings), edits[3]);
    }

    [Fact]
    public void PendingStructureEdits_ReplayedOntoTheBaselineIds_GiveTheCurrentOrder()
    {
        // every permutation of 4 layers: the net moves rebuild exactly that order
        var config = WithLayers(Config(), Config().Layers[0], Config().Layers[1], Config().Layers[2], Added("d", "D"));
        foreach (var order in Permutations([0, 1, 2, 3]))
        {
            var session = new KeymapEditSession(config);
            var ids = new List<int> { 0, 1, 2, 3 };
            // reach the order by moving each target layer into place
            for (int p = 0; p < order.Count; p++)
            {
                var from = ids.IndexOf(order[p]);
                if (from == p) continue;
                var c = session.Current;
                var layers = c.Layers.ToList();
                var layer = layers[from];
                layers.RemoveAt(from);
                layers.Insert(p, layer);
                Assert.True(session.ApplyStructure(new LayerStructureEdit.Move(from, p), WithLayers(c, [.. layers])));
                ids.RemoveAt(from);
                ids.Insert(p, order[p]);
            }

            var replay = new List<int> { 0, 1, 2, 3 };
            foreach (var edit in session.PendingStructureEdits)
            {
                var move = Assert.IsType<LayerStructureEdit.Move>(edit);
                var id = replay[move.From];
                replay.RemoveAt(move.From);
                replay.Insert(move.To, id);
            }
            Assert.Equal(order, replay);
        }
    }

    private static IEnumerable<List<int>> Permutations(List<int> items)
    {
        if (items.Count <= 1) { yield return [.. items]; yield break; }
        foreach (var item in items)
            foreach (var rest in Permutations(items.Where(i => i != item).ToList()))
                yield return [item, .. rest];
    }

    /// <summary>Record equality compares an Add's bindings list by reference;
    /// compare its fields and the bindings by content instead.</summary>
    private static void AssertSameEdit(LayerStructureEdit expected, LayerStructureEdit actual)
    {
        if (expected is LayerStructureEdit.Add add)
        {
            var other = Assert.IsType<LayerStructureEdit.Add>(actual);
            Assert.Equal(add.AtIndex, other.AtIndex);
            Assert.Equal(add.Name, other.Name);
            Assert.Equal(add.Bindings.Select(b => b.Display), other.Bindings.Select(b => b.Display));
            return;
        }
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplyStructure_AfterHasTheWrongLayerCount_ThrowsAndChangesNothing()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var before = session.Current;
        var added = Added("extra");
        var three = config with { Layers = config.Layers };
        var four = WithLayers(config, config.Layers[0], added, config.Layers[1], config.Layers[2]);

        // add needs count+1, delete count−1, move count; each gets a wrong one
        Assert.Throws<ArgumentException>(() => session.ApplyStructure(
            new LayerStructureEdit.Add(1, "extra", added.Bindings), three));
        Assert.Throws<ArgumentException>(() => session.ApplyStructure(
            new LayerStructureEdit.Delete(2), four));
        Assert.Throws<ArgumentException>(() => session.ApplyStructure(
            new LayerStructureEdit.Move(0, 2), four));

        Assert.Same(before, session.Current);
        Assert.Equal(0, session.ChangeCount);
        Assert.Empty(session.PendingStructureEdits);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)] // an add may target 0 … 3 (3 == append)
    public void ApplyStructure_AddIndexOutOfRange_Throws(int atIndex)
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added("extra");
        var after = WithLayers(config, config.Layers[0], added, config.Layers[1], config.Layers[2]);

        Assert.Throws<ArgumentOutOfRangeException>(() => session.ApplyStructure(
            new LayerStructureEdit.Add(atIndex, "extra", added.Bindings), after));

        Assert.Same(config, session.Current);
        Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void ApplyStructure_DeleteIndexOutOfRange_Throws(int index)
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var after = WithLayers(config, config.Layers[0], config.Layers[1]);

        Assert.Throws<ArgumentOutOfRangeException>(() => session.ApplyStructure(
            new LayerStructureEdit.Delete(index), after));

        Assert.Same(config, session.Current);
        Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(3, 0)]
    [InlineData(0, 3)]
    public void ApplyStructure_MoveIndicesOutOfRange_Throws(int from, int to)
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var after = WithLayers(config, config.Layers[1], config.Layers[0], config.Layers[2]);

        Assert.Throws<ArgumentOutOfRangeException>(() => session.ApplyStructure(
            new LayerStructureEdit.Move(from, to), after));

        Assert.Same(config, session.Current);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ApplyStructure_DeleteTheLastLayer_Throws()
    {
        var single = new KeyboardConfig(
            "test", [new Layer(0, "only", [Bind("&kp", "A"), Bind("&kp", "B")])], [], [], []);
        var session = new KeymapEditSession(single);
        var before = session.Current;

        var ex = Assert.Throws<InvalidOperationException>(() => session.ApplyStructure(
            new LayerStructureEdit.Delete(0), single with { Layers = [] }));

        Assert.Contains("only layer", ex.Message, StringComparison.Ordinal);
        Assert.Same(before, session.Current);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ApplyStructure_AddWithTheWrongBindingCount_Throws()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var tooShort = new Layer(0, "extra", [Bind("&kp", "N0"), Bind("&kp", "N1")]); // 4 keys expected
        var after = WithLayers(config, config.Layers[0], tooShort, config.Layers[1], config.Layers[2]);

        var ex = Assert.Throws<ArgumentException>(() => session.ApplyStructure(
            new LayerStructureEdit.Add(1, "extra", tooShort.Bindings), after));

        Assert.Contains("2 binding(s)", ex.Message, StringComparison.Ordinal);
        Assert.Same(config, session.Current);
        Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sym\nlayer")]
    [InlineData("sym\tlayer")]
    public void ApplyStructure_AddWithAnInvalidName_Throws(string? name)
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added(name ?? "extra");
        var after = WithLayers(config, config.Layers[0], added, config.Layers[1], config.Layers[2]);

        Assert.Throws<ArgumentException>(() => session.ApplyStructure(
            new LayerStructureEdit.Add(1, name!, added.Bindings), after));

        Assert.Same(config, session.Current);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ApplyStructure_NullArguments_Throw()
    {
        var config = Config();
        var session = new KeymapEditSession(config);

        Assert.Throws<ArgumentNullException>(
            () => session.ApplyStructure(null!, config));
        Assert.Throws<ArgumentNullException>(
            () => session.ApplyStructure(new LayerStructureEdit.Move(0, 1), null!));

        Assert.Same(config, session.Current);
        Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)] // the keymap has 4 layers after the add
    public void IsLayerAdded_IndexOutOfRange_ReadsAsNotAdded(int layer)
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added("extra");
        session.ApplyStructure(
            new LayerStructureEdit.Add(1, "extra", added.Bindings),
            WithLayers(config, config.Layers[0], added, config.Layers[1], config.Layers[2]));

        Assert.False(session.IsLayerAdded(layer));
    }

    // ---- baseline layer identity ------------------------------------------------

    [Fact]
    public void BaselineIdentity_FreshSession_EveryIdEqualsItsPosition()
    {
        var session = new KeymapEditSession(Config());

        Assert.Equal(0, session.BaselineIndexOf(0));
        Assert.Equal(1, session.BaselineIndexOf(1));
        Assert.Equal(2, session.BaselineIndexOf(2));
        Assert.Null(session.BaselineIndexOf(3));
        Assert.Null(session.BaselineIndexOf(-1));
        Assert.Equal([0, 1, 2], session.BaselineLayerMap());
    }

    [Fact]
    public void BaselineIdentity_AfterAnAdd_TheAddedLayerHasNoBaselineAndTheRestShift()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added("extra");
        session.ApplyStructure(
            new LayerStructureEdit.Add(1, "extra", added.Bindings),
            WithLayers(config, config.Layers[0], added, config.Layers[1], config.Layers[2]));

        Assert.Null(session.BaselineIndexOf(1));
        Assert.Equal(1, session.BaselineIndexOf(2));
        Assert.Equal(2, session.BaselineIndexOf(3));
        Assert.Equal([0, 2, 3], session.BaselineLayerMap());
    }

    [Fact]
    public void BaselineIdentity_AfterADelete_TheDeletedBaselineLayerMapsToNull()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        session.ApplyStructure(
            new LayerStructureEdit.Delete(1),
            WithLayers(config, config.Layers[0], config.Layers[2]));

        Assert.Equal(2, session.BaselineIndexOf(1));
        Assert.Equal([0, null, 1], session.BaselineLayerMap());
    }

    [Fact]
    public void BaselineIdentity_AfterAMove_TheMapFollowsTheLayers()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        session.ApplyStructure(
            new LayerStructureEdit.Move(0, 2),
            WithLayers(config, config.Layers[1], config.Layers[2], config.Layers[0]));

        Assert.Equal(0, session.BaselineIndexOf(2));
        Assert.Equal(1, session.BaselineIndexOf(0));
        Assert.Equal([2, 0, 1], session.BaselineLayerMap());

        // undo/redo walk the map with the layer list
        Assert.True(session.Undo());
        Assert.Equal([0, 1, 2], session.BaselineLayerMap());

        Assert.True(session.Redo());
        Assert.Equal([2, 0, 1], session.BaselineLayerMap());
    }

    [Fact]
    public void LayerId_StaysWithItsLayer_ThroughMoveUndoAndRedo()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var id = session.LayerIdAt(0)!.Value;
        session.ApplyStructure(
            new LayerStructureEdit.Move(0, 2),
            WithLayers(config, config.Layers[1], config.Layers[2], config.Layers[0]));

        Assert.Equal(2, session.IndexOfLayerId(id));
        Assert.True(session.Undo());
        Assert.Equal(0, session.IndexOfLayerId(id));
        Assert.True(session.Redo());
        Assert.Equal(2, session.IndexOfLayerId(id));

        Assert.Null(session.LayerIdAt(3));
        Assert.Null(session.LayerIdAt(-1));
    }

    [Fact]
    public void IndexOfLayerId_AnUndoneAdd_IsGone()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added("extra");
        session.ApplyStructure(
            new LayerStructureEdit.Add(3, "extra", added.Bindings),
            WithLayers(config, config.Layers[0], config.Layers[1], config.Layers[2], added));
        var id = session.LayerIdAt(3)!.Value;

        Assert.True(session.Undo());

        Assert.Null(session.IndexOfLayerId(id));
    }

    [Fact]
    public void BaselineLayerMap_IsANewListEveryCall()
    {
        var session = new KeymapEditSession(Config());

        var first = session.BaselineLayerMap();
        var second = session.BaselineLayerMap();

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
    }

    [Fact]
    public void MixedHistory_BindAddBind_UndoesEverythingCleanly()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added("extra");

        Assert.True(session.SetBindings([new BindingChange(0, 0, Bind("&kp", "A"))]));

        // The result of the edit is derived from what the session holds now, so it
        // carries the edit above: redo replays this snapshot verbatim.
        var withAdded = WithLayers(
            session.Current,
            session.Current.Layers[0], added, session.Current.Layers[1], session.Current.Layers[2]);
        Assert.True(session.ApplyStructure(new LayerStructureEdit.Add(1, "extra", added.Bindings), withAdded));
        Assert.True(session.SetBindings([new BindingChange(3, 1, Bind("&kp", "B"))])); // shifted baseline layer

        Assert.Equal(3, session.ChangeCount);
        Assert.Single(session.PendingStructureEdits);

        Assert.True(session.Undo()); // the second binding
        Assert.Equal(2, session.ChangeCount);
        Assert.Single(session.PendingStructureEdits);

        Assert.True(session.Undo()); // the add
        Assert.Equal(1, session.ChangeCount);
        Assert.Empty(session.PendingStructureEdits);
        Assert.Equal(3, session.Current.Layers.Count);

        Assert.True(session.Undo()); // the first binding
        Assert.Equal(0, session.ChangeCount);
        Assert.False(session.HasPendingChanges);
        Assert.False(session.CanUndo);
        Assert.False(session.Undo());
        Assert.Equal("&kp K00", session.Current.Layers[0].Bindings[0].Display);
        Assert.Equal("&kp K21", session.Current.Layers[2].Bindings[1].Display);
        Assert.True(session.CanRedo);

        // redoing the whole history lands on the same shape it left
        Assert.True(session.Redo());
        Assert.True(session.Redo());
        Assert.True(session.Redo());
        Assert.Equal(3, session.ChangeCount);
        Assert.Equal(4, session.Current.Layers.Count);
        Assert.True(session.IsLayerAdded(1));
        Assert.Equal("&kp A", session.Current.Layers[0].Bindings[0].Display);
        Assert.Equal("&kp B", session.Current.Layers[3].Bindings[1].Display);
    }

    // ---- combo edits ------------------------------------------------------------

    [Fact]
    public void AddCombo_Slabwing_AppendsAsOneStep_UndoAndRedoWalkIt()
    {
        var config = Slabwing();
        Assert.Equal(4, config.Combos.Count);
        var session = new KeymapEditSession(config);
        var esc = EscCombo();
        var raised = 0;
        session.Changed += (_, _) => raised++;

        var index = session.AddCombo(esc);

        Assert.Equal(4, index);
        Assert.Equal(5, session.Current.Combos.Count);
        Assert.Same(esc, session.Current.Combos[4]);
        Assert.Equal(1, session.ChangeCount);
        Assert.True(session.HasPendingChanges);
        Assert.True(session.IsComboChanged(4));
        for (int i = 0; i < 4; i++) Assert.False(session.IsComboChanged(i));
        var add = Assert.IsType<ComboEdit.Add>(Assert.Single(session.PendingComboEdits));
        Assert.Same(esc, add.Combo);
        Assert.Equal(1, raised);
        Assert.True(session.CanUndo);
        Assert.False(session.CanRedo);

        Assert.True(session.Undo());
        Assert.Equal(4, session.Current.Combos.Count);
        Assert.Equal(0, session.ChangeCount);
        Assert.False(session.HasPendingChanges);
        Assert.Empty(session.PendingComboEdits);
        Assert.False(session.IsComboChanged(4)); // outside the list now

        Assert.True(session.Redo());
        Assert.Equal(5, session.Current.Combos.Count);
        Assert.Same(esc, session.Current.Combos[4]);
        Assert.Equal(1, session.ChangeCount);
        Assert.Equal(3, raised);
    }

    [Fact]
    public void AddCombo_ThenDeleteThatAddedComboAgain_LeavesNothingPending()
    {
        var session = new KeymapEditSession(Slabwing());
        var index = session.AddCombo(EscCombo());
        Assert.Equal(1, session.ChangeCount);

        session.DeleteCombo(index);

        Assert.Equal(4, session.Current.Combos.Count);
        Assert.Equal(0, session.ChangeCount);
        Assert.False(session.HasPendingChanges);
        Assert.Empty(session.PendingComboEdits);
        Assert.True(session.CanUndo); // the steps are still there to walk back
    }

    [Fact]
    public void SetCombo_ChangesTheBinding_SettingItBackReadsClean()
    {
        var config = Slabwing();
        var session = new KeymapEditSession(config);
        var original = config.Combos[2];
        Assert.Equal("f12", original.NodeName);

        var f11 = original with { Binding = Bind("&kp", "F11") };
        Assert.True(session.SetCombo(2, f11));

        Assert.Same(f11, session.Current.Combos[2]);
        Assert.True(session.IsComboChanged(2));
        Assert.Equal(1, session.ChangeCount);
        var change = Assert.IsType<ComboEdit.Change>(Assert.Single(session.PendingComboEdits));
        Assert.Equal("f12", change.NodeName);
        Assert.Same(f11, change.Combo);

        // editing back to the baseline value reads clean, the step stays undoable
        Assert.True(session.SetCombo(2, original));
        Assert.Equal(0, session.ChangeCount);
        Assert.False(session.IsComboChanged(2));
        Assert.Empty(session.PendingComboEdits);
        Assert.True(session.CanUndo);
    }

    [Fact]
    public void SetCombo_Renames_PendingChangeKeepsTheBaselineName()
    {
        var session = new KeymapEditSession(Slabwing());
        var current = session.Current.Combos[2];

        Assert.True(session.SetCombo(2, current with { NodeName = "combo_f12" }));

        // the writer addresses the node by the name the file still has
        var change = Assert.IsType<ComboEdit.Change>(Assert.Single(session.PendingComboEdits));
        Assert.Equal("f12", change.NodeName);
        Assert.Equal("combo_f12", change.Combo.NodeName);
        Assert.Equal(1, session.ChangeCount);
    }

    [Fact]
    public void SetCombo_WithAnIsSameAsCombo_RecordsNothing()
    {
        var session = new KeymapEditSession(Slabwing());
        var before = session.Current;
        var raised = 0;
        session.Changed += (_, _) => raised++;

        // positions reordered, description rewritten — IsSameAs ignores both
        var sameButFresh = session.Current.Combos[2] with
        {
            KeyPositions = [1, 0],
            Description = "still the same combo",
        };

        Assert.False(session.SetCombo(2, sameButFresh));

        Assert.Same(before, session.Current);
        Assert.Equal(0, session.ChangeCount);
        Assert.False(session.CanUndo);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void DeleteCombo_Slabwing_CountsTheMissingCombo_UndoRestoresItInPlace()
    {
        var config = Slabwing();
        var session = new KeymapEditSession(config);

        session.DeleteCombo(1); // mouse-mid

        Assert.Equal(3, session.Current.Combos.Count);
        Assert.Same(config.Combos[0], session.Current.Combos[0]);
        Assert.Same(config.Combos[2], session.Current.Combos[1]); // f12 shifted, unchanged
        Assert.False(session.IsComboChanged(1));
        Assert.Equal(1, session.ChangeCount);
        var delete = Assert.IsType<ComboEdit.Delete>(Assert.Single(session.PendingComboEdits));
        Assert.Equal("mouse-mid", delete.NodeName);

        Assert.True(session.Undo());
        Assert.Equal(4, session.Current.Combos.Count);
        Assert.Same(config.Combos[1], session.Current.Combos[1]); // back at index 1
        Assert.Equal(0, session.ChangeCount);
        Assert.Empty(session.PendingComboEdits);
    }

    [Fact]
    public void DeleteCombo_WithoutANodeName_DeletesByName()
    {
        var session = new KeymapEditSession(Config()); // its combo carries no NodeName

        session.DeleteCombo(0);

        var delete = Assert.IsType<ComboEdit.Delete>(Assert.Single(session.PendingComboEdits));
        Assert.Equal("F12", delete.NodeName); // the fallback is the combo's Name
        Assert.Equal(1, session.ChangeCount);
    }

    [Fact]
    public void PendingComboEdits_DeletesFirst_ThenChanges_ThenAdds()
    {
        var session = new KeymapEditSession(Slabwing());

        session.DeleteCombo(1); // mouse-mid
        var f12 = session.Current.Combos[1]; // shifted down, still the baseline f12
        var f11 = f12 with { Binding = Bind("&kp", "F11") };
        Assert.True(session.SetCombo(1, f11));
        var esc = EscCombo();
        session.AddCombo(esc);

        Assert.Equal(3, session.ChangeCount);
        var edits = session.PendingComboEdits;
        Assert.Equal(3, edits.Count);
        Assert.Equal(new ComboEdit.Delete("mouse-mid"), edits[0]);
        var change = Assert.IsType<ComboEdit.Change>(edits[1]);
        Assert.Equal("f12", change.NodeName);
        Assert.Same(f11, change.Combo);
        var add = Assert.IsType<ComboEdit.Add>(edits[2]);
        Assert.Same(esc, add.Combo);
    }

    [Fact]
    public void ComboSteps_KeepLayersMacrosAndHoldTapsTheSameInstances()
    {
        var config = Config();
        var session = new KeymapEditSession(config);

        session.AddCombo(EscCombo());
        Assert.NotSame(config, session.Current);
        Assert.Same(config.Layers, session.Current.Layers);
        Assert.Same(config.Macros, session.Current.Macros);
        Assert.Same(config.HoldTaps, session.Current.HoldTaps);

        Assert.True(session.SetCombo(0, config.Combos[0] with { Binding = Bind("&kp", "F13") }));
        Assert.Same(config.Layers, session.Current.Layers);

        session.DeleteCombo(1); // the added one
        Assert.Same(config.Layers, session.Current.Layers);
        Assert.Same(config.Macros, session.Current.Macros);
    }

    [Fact]
    public void ApplyStructure_AfterWithADifferentComboCount_Throws()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = Added("extra");

        // fresh session: one baseline combo, the after drops it
        var noCombos = WithLayers(config, config.Layers[0], added, config.Layers[1], config.Layers[2])
            with { Combos = [] };
        Assert.Throws<ArgumentException>(() => session.ApplyStructure(
            new LayerStructureEdit.Add(1, "extra", added.Bindings), noCombos));

        // after an add the check runs against Current, not the baseline
        session.AddCombo(EscCombo());
        var c = session.Current;
        var oneOfTwo = WithLayers(c, c.Layers[0], Added("extra"), c.Layers[1], c.Layers[2])
            with { Combos = [c.Combos[0]] };
        Assert.Throws<ArgumentException>(() => session.ApplyStructure(
            new LayerStructureEdit.Add(1, "extra", Added("extra").Bindings), oneOfTwo));

        Assert.Same(c, session.Current); // the second rejection left the added combo in
        // only the AddCombo step exists: the rejections recorded nothing
        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ComboEdits_AroundAStructureStep_UndoBackToTheBaselineCombos()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        Assert.Equal(1, session.AddCombo(EscCombo()));

        var c = session.Current;
        Assert.True(session.ApplyStructure(
            new LayerStructureEdit.Move(0, 2),
            WithLayers(c, c.Layers[1], c.Layers[2], c.Layers[0])));

        // the added combo rides through the structure step and still reads as changed
        Assert.Equal(2, session.Current.Combos.Count);
        Assert.True(session.IsComboChanged(1));
        Assert.Equal(2, session.ChangeCount); // the layer order + the added combo

        Assert.True(session.Undo()); // the move
        Assert.True(session.Undo()); // the add

        Assert.Equal(config.Combos.Count, session.Current.Combos.Count);
        for (int i = 0; i < config.Combos.Count; i++)
            Assert.True(session.Current.Combos[i].IsSameAs(config.Combos[i]));
        Assert.Equal(0, session.ChangeCount);
        Assert.Empty(session.PendingComboEdits);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)] // slabwing has 4 combos
    public void ComboEdit_IndexOutOfRange_Throws(int index)
    {
        var session = new KeymapEditSession(Slabwing());
        var before = session.Current;

        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetCombo(index, EscCombo()));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.DeleteCombo(index));

        Assert.Same(before, session.Current);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ComboEdits_NullCombo_Throws()
    {
        var session = new KeymapEditSession(Slabwing());
        var before = session.Current;

        Assert.Throws<ArgumentNullException>(() => session.AddCombo(null!));
        Assert.Throws<ArgumentNullException>(() => session.SetCombo(0, null!));

        Assert.Same(before, session.Current);
        Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)] // one combo was deleted, 3 are left
    public void IsComboChanged_IndexOutOfRange_ReadsAsUnchanged(int index)
    {
        var session = new KeymapEditSession(Slabwing());
        session.DeleteCombo(0); // a deletion is pending, but these indices are outside

        Assert.False(session.IsComboChanged(index));
    }

    // ---- on the real corne keymap ------------------------------------------------

    [Fact]
    public void CorneFixture_EditUndoRedo_RoundTrips()
    {
        var config = ZmkKeymapLoader.LoadFromText(KeymapFixtures.Read("corne.keymap"), "corne");
        var session = new KeymapEditSession(config);
        var before = config.Layers[1].Bindings[0];
        var newBinding = Bind("&kp", "Z");

        Assert.True(session.SetBindings([new BindingChange(1, 0, newBinding)]));
        Assert.Same(newBinding, session.Current.Layers[1].Bindings[0]);
        Assert.True(session.IsKeyChanged(1, 0));
        Assert.Equal(1, session.ChangeCount);

        Assert.True(session.Undo());
        Assert.Same(before, session.Current.Layers[1].Bindings[0]);
        Assert.Equal(before.Display, session.Current.Layers[1].Bindings[0].Display);
        Assert.Equal(0, session.ChangeCount);

        Assert.True(session.Redo());
        Assert.Equal("&kp Z", session.Current.Layers[1].Bindings[0].Display);
        Assert.Equal(1, session.ChangeCount);

        // the layer list keeps its shape and its indices
        Assert.Equal(config.LayerCount, session.Current.Layers.Count);
        Assert.Equal(1, session.Current.Layers[1].Index);
        Assert.Equal("num_layer", session.Current.Layers[1].Name);
        Assert.Equal(config.Layers[0].Bindings.Count, session.Current.Layers[0].Bindings.Count);
        Assert.Same(config.Layers[0], session.Current.Layers[0]);
        Assert.Same(config.Macros, session.Current.Macros);
        Assert.Same(config.Combos, session.Current.Combos);
    }

    [Fact]
    public void CorneFixture_RenameKeyLayer_LeavesOtherLayersAlone()
    {
        var config = ZmkKeymapLoader.LoadFromText(KeymapFixtures.Read("corne.keymap"), "corne");
        var session = new KeymapEditSession(config);
        var lastKey = config.Layers[1].Bindings.Count - 1;

        Assert.True(session.RenameLayer(1, "numbers"));
        session.SetBindings([new BindingChange(1, lastKey, Bind("&kp", "Z"))]);

        Assert.Equal("numbers", session.Current.Layers[1].Name);
        Assert.Equal(2, session.ChangeCount);
        Assert.Equal("default_layer", session.Current.Layers[0].Name);
        Assert.Same(config.Layers[0], session.Current.Layers[0]);
        Assert.Same(config.Layers[2], session.Current.Layers[2]);
        Assert.Equal(1, session.Current.Layers[1].Index);
    }
}
