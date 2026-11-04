using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using static LViz.Core.Keymap.Editing.LayerStructureEdit;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// <see cref="KeymapSavePlan.Build"/> over a real fixture: the untouched session
/// keeps the text character for character, a cleared key comes back as exactly
/// one changed line, and renames and layer adds show up as themselves. The plan
/// must describe what it writes: the lists and the text agree, and a session
/// undone back to clean produces an empty plan, not a coincidental copy.
/// </summary>
public class KeymapSavePlanTests
{
    private static string Source() => KeymapFixtures.Read("slabwing.keymap");

    private static KeyboardConfig Parse(string source) =>
        ZmkKeymapLoader.LoadFromText(source, "test");

    private static KeymapEditSession Session() => new(Parse(Source()));

    /// <summary>Compare a binding by behaviour plus params — record equality
    /// ignores the contents of <c>Params</c> (AGENTS.md gotcha).</summary>
    private static void AssertSameBinding(KeyBinding actual, string behavior, params string[] parameters)
    {
        Assert.Equal(behavior, actual.Behavior);
        Assert.True(
            actual.Params.SequenceEqual(parameters, StringComparer.Ordinal),
            $"expected {behavior} [{string.Join(' ', parameters)}], got {actual.Display}");
    }

    [Fact]
    public void Build_UntouchedSession_IsEmptyAndKeepsTheText()
    {
        var source = Source();

        var plan = KeymapSavePlan.Build(source, new KeymapEditSession(Parse(source)));

        Assert.True(plan.IsEmpty);
        Assert.True(
            string.Equals(plan.NewSource, source, StringComparison.Ordinal),
            "an untouched session must return the fixture text unchanged");
        Assert.Empty(plan.StructureEdits);
        Assert.Empty(plan.BindingChanges);
        Assert.Empty(plan.Renames);
    }

    [Fact]
    public void Build_OneKeyCleared_WritesExactlyThatLine()
    {
        var source = Source();
        var session = Session();
        Assert.True(session.SetBindings([new BindingChange(0, 11, KeyBinding.Transparent)]));

        var plan = KeymapSavePlan.Build(source, session);

        var change = Assert.Single(plan.BindingChanges);
        Assert.Equal(0, change.LayerIndex);
        Assert.Equal("Base", change.LayerName);
        Assert.Equal(11, change.KeyIndex);
        AssertSameBinding(change.Old, "&kp", "Q");
        AssertSameBinding(change.New, "&trans");
        Assert.Empty(plan.Renames);
        Assert.Empty(plan.StructureEdits);
        Assert.False(plan.IsEmpty);

        // The written text re-parses to the session's model.
        var reparsed = Parse(plan.NewSource);
        var current = session.Current;
        Assert.Equal(current.Layers.Count, reparsed.Layers.Count);
        for (int layer = 0; layer < current.Layers.Count; layer++)
        {
            Assert.Equal(current.Layers[layer].Bindings.Count, reparsed.Layers[layer].Bindings.Count);
            for (int key = 0; key < current.Layers[layer].Bindings.Count; key++)
            {
                var expected = current.Layers[layer].Bindings[key];
                var actual = reparsed.Layers[layer].Bindings[key];
                Assert.Equal(expected.Behavior, actual.Behavior);
                Assert.True(
                    actual.Params.SequenceEqual(expected.Params, StringComparer.Ordinal),
                    $"layer {layer} key {key}: expected {expected.Display}, got {actual.Display}");
            }
        }
        AssertSameBinding(reparsed.Layers[0].Bindings[11], "&trans");

        // Only the line holding that key moved.
        var was = source.Split('\n');
        var now = plan.NewSource.Split('\n');
        Assert.Equal(was.Length, now.Length);
        var changedLine = Assert.Single(
            Enumerable.Range(0, was.Length)
                .Where(i => !string.Equals(was[i], now[i], StringComparison.Ordinal))
                .ToList());
        Assert.Contains("&kp ESCAPE", was[changedLine], StringComparison.Ordinal);
        Assert.DoesNotContain("&kp Q", now[changedLine], StringComparison.Ordinal);
    }

    [Fact]
    public void Build_Rename_ReportsOldAndNewName()
    {
        var source = Source();
        var session = Session();
        Assert.True(session.RenameLayer(1, "Sym"));

        var plan = KeymapSavePlan.Build(source, session);

        var rename = Assert.Single(plan.Renames);
        Assert.Equal(1, rename.LayerIndex);
        Assert.Equal("Symbols", rename.OldName);
        Assert.Equal("Sym", rename.NewName);
        Assert.Empty(plan.BindingChanges);
        Assert.Empty(plan.StructureEdits);
        Assert.Contains("display-name = \"Sym\";", plan.NewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AddLayerAtEnd_ReParsesToEightLayers()
    {
        var source = Source();
        var session = Session();
        int keyCount = session.Current.Layers[0].Bindings.Count;
        var edit = new Add(7, "Test", Enumerable.Repeat(KeyBinding.Transparent, keyCount).ToList());
        Assert.True(session.ApplyStructure(edit, LayerReferenceRemapper.Apply(session.Current, edit)));

        var plan = KeymapSavePlan.Build(source, session);

        var structureEdit = Assert.IsType<Add>(Assert.Single(plan.StructureEdits));
        Assert.Equal((7, "Test"), (structureEdit.AtIndex, structureEdit.Name));
        Assert.Equal(edit.Bindings.Select(b => b.Display), structureEdit.Bindings.Select(b => b.Display));
        Assert.Empty(plan.BindingChanges);
        Assert.Empty(plan.Renames);

        var reparsed = Parse(plan.NewSource);
        Assert.Equal(8, reparsed.Layers.Count);
        Assert.Equal("Test", reparsed.Layers[^1].Name);
    }

    [Fact]
    public void Build_EditsUndoneToClean_AreEmptyAndKeepTheText()
    {
        var source = Source();
        var session = Session();
        var edit = new Add(7, "Test", Enumerable.Repeat(KeyBinding.Transparent, session.Current.Layers[0].Bindings.Count).ToList());
        session.ApplyStructure(edit, LayerReferenceRemapper.Apply(session.Current, edit));
        session.Undo(); // the layer list is back at the baseline ids
        session.SetBindings([new BindingChange(0, 11, KeyBinding.Transparent)]);
        session.Undo();

        var plan = KeymapSavePlan.Build(source, session);

        Assert.True(plan.IsEmpty);
        Assert.True(
            string.Equals(plan.NewSource, source, StringComparison.Ordinal),
            "undoing everything must return the fixture text unchanged");
    }

    [Fact]
    public void Build_MismatchedBaseline_ThrowsArgumentException()
    {
        var session = Session();

        Assert.Throws<ArgumentException>(() =>
            KeymapSavePlan.Build(KeymapFixtures.Read("minimal.keymap"), session));
    }

    [Fact]
    public void Build_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => KeymapSavePlan.Build(null!, Session()));
        Assert.Throws<ArgumentNullException>(() => KeymapSavePlan.Build(Source(), null!));
    }
}
