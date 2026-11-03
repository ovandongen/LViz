using LViz.Core.Keymap.Editing;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// The diff that turns an edited keymap into <c>KeymapWriter</c> input: which cells
/// to rewrite and which layers to rename. Structure is not part of it — layers
/// added, deleted or moved are applied to the text before the diff runs.
/// </summary>
public class KeymapEditDiffTests
{
    private static KeyBinding Bind(string behavior, params string[] parameters) => new(behavior, parameters);

    /// <summary>3 layers × 4 keys, every cell distinct.</summary>
    private static KeyboardConfig Config()
    {
        var layers = Enumerable.Range(0, 3).Select(i => new Layer(
            i,
            $"layer{i}",
            Enumerable.Range(0, 4).Select(k => Bind("&kp", $"K{i}{k}")).ToArray()
        )).ToList();

        return new KeyboardConfig("test", layers, [], [], []);
    }

    /// <summary>The same config with these layers in it, <c>Index</c> kept equal to
    /// position the way the writer expects.</summary>
    private static KeyboardConfig With(KeyboardConfig config, params Layer[] layers) =>
        config with { Layers = layers.Select((l, i) => l.Index == i ? l : l with { Index = i }).ToList() };

    [Fact]
    public void Compute_IdenticalConfigs_FindsNothing()
    {
        var config = Config();

        var result = KeymapEditDiff.Compute(config, config);

        Assert.Empty(result.Bindings);
        Assert.Empty(result.Renames);
    }

    [Fact]
    public void Compute_ChangedCellsAndRenamedLayer_EditsInLayerThenKeyOrder()
    {
        var from = Config();

        var keys0 = from.Layers[0].Bindings.ToArray();
        keys0[1] = Bind("&kp", "A");
        var keys2 = from.Layers[2].Bindings.ToArray();
        keys2[3] = Bind("&mo", "1");

        var to = With(
            from,
            from.Layers[0] with { Name = "work", Bindings = keys0 },
            from.Layers[1],
            from.Layers[2] with { Bindings = keys2 });

        var result = KeymapEditDiff.Compute(from, to);

        Assert.Equal(2, result.Bindings.Count);

        Assert.Equal(0, result.Bindings[0].LayerIndex);
        Assert.Equal(1, result.Bindings[0].KeyIndex);
        Assert.Same(keys0[1], result.Bindings[0].Binding); // the binding to write, by reference

        Assert.Equal(2, result.Bindings[1].LayerIndex);
        Assert.Equal(3, result.Bindings[1].KeyIndex);
        Assert.Same(keys2[3], result.Bindings[1].Binding);

        var rename = Assert.Single(result.Renames);
        Assert.Equal(0, rename.LayerIndex);
        Assert.Equal("work", rename.Name);
    }

    [Fact]
    public void Compute_RenamesAreOrderedByLayer()
    {
        var from = Config();
        var to = With(from,
            from.Layers[0] with { Name = "work" },
            from.Layers[1],
            from.Layers[2] with { Name = "gaming" });

        var result = KeymapEditDiff.Compute(from, to);

        Assert.Empty(result.Bindings);
        Assert.Equal([0, 2], result.Renames.Select(r => r.LayerIndex));
        Assert.Equal(["work", "gaming"], result.Renames.Select(r => r.Name));
    }

    [Fact]
    public void Compute_NameDifferingOnlyByCaseOrSpace_IsStillARename()
    {
        var from = Config();
        var to = With(from, from.Layers[0], from.Layers[1] with { Name = "Layer1 " }, from.Layers[2]);

        var rename = Assert.Single(KeymapEditDiff.Compute(from, to).Renames);

        Assert.Equal(1, rename.LayerIndex);
        Assert.Equal("Layer1 ", rename.Name);
    }

    [Fact]
    public void Compute_SameBindingAsANewList_IsNotAnEdit()
    {
        var from = Config();
        var current = from.Layers[1].Bindings[2];

        var keys = from.Layers[1].Bindings.ToArray();
        keys[2] = new KeyBinding(current.Behavior, [.. current.Params]); // e.g. rebuilt by the UI
        Assert.NotSame(current.Params, keys[2].Params);

        var result = KeymapEditDiff.Compute(from, With(
            from, from.Layers[0], from.Layers[1] with { Bindings = keys }, from.Layers[2]));

        Assert.Empty(result.Bindings);
        Assert.Empty(result.Renames);
    }

    [Fact]
    public void Compute_LayerCountMismatch_Throws()
    {
        var from = Config();
        var to = new KeyboardConfig(
            from.KeyboardId, [.. from.Layers.Take(2)], from.Macros, from.HoldTaps, from.Combos);

        var ex = Assert.Throws<ArgumentException>(() => KeymapEditDiff.Compute(from, to));

        Assert.Contains("3 and 2 layers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compute_BindingCountMismatch_Throws()
    {
        var from = Config();
        var shorter = from.Layers[1] with { Bindings = [.. from.Layers[1].Bindings.Take(3)] };
        var to = With(from, from.Layers[0], shorter, from.Layers[2]);

        var ex = Assert.Throws<ArgumentException>(() => KeymapEditDiff.Compute(from, to));

        Assert.Contains("layer 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4 binding(s) before and 3 after", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compute_NullArguments_Throw()
    {
        var config = Config();

        Assert.Throws<ArgumentNullException>(() => KeymapEditDiff.Compute(null!, config));
        Assert.Throws<ArgumentNullException>(() => KeymapEditDiff.Compute(config, null!));
    }

    [Fact]
    public void Compute_SessionState_GivesExactlyWhatTheWriterNeeds()
    {
        var config = Config();
        var session = new KeymapEditSession(config);

        session.RenameLayer(1, "gaming");
        session.SetBindings([new BindingChange(1, 2, Bind("&mo", "2"))]);

        var result = KeymapEditDiff.Compute(session.Baseline, session.Current);

        var edit = Assert.Single(result.Bindings);
        Assert.Equal(1, edit.LayerIndex);
        Assert.Equal(2, edit.KeyIndex);
        Assert.Same(session.Current.Layers[1].Bindings[2], edit.Binding);

        var rename = Assert.Single(result.Renames);
        Assert.Equal(1, rename.LayerIndex);
        Assert.Equal("gaming", rename.Name);
    }

    [Fact]
    public void Compute_AfterALayerWasAdded_Throws_BecauseStructureIsWrittenSeparately()
    {
        var config = Config();
        var session = new KeymapEditSession(config);
        var added = new Layer(
            0, "extra", Enumerable.Range(0, 4).Select(k => Bind("&kp", $"X{k}")).ToArray());

        Assert.True(session.ApplyStructure(
            new LayerStructureEdit.Add(0, "extra", added.Bindings),
            With(config, added, config.Layers[0], config.Layers[1], config.Layers[2])));

        // the save path applies the structural edits to the text and re-parses
        // first; only then do both sides line up for the diff
        Assert.Throws<ArgumentException>(() => KeymapEditDiff.Compute(session.Baseline, session.Current));
    }
}
