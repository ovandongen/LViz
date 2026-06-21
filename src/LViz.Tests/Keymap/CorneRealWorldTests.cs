using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// End-to-end tests against the real corne keymaps shipped in
/// <c>docs/</c>. Mirrors what a user actually drops onto the file picker.
/// </summary>
public class CorneRealWorldTests
{
    private static string LoadFixture(string name) => KeymapFixtures.Read(name);

    [Fact]
    public void CorneKeymap_LoadsFourLayers()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("corne.keymap"), "corne");
        Assert.Equal(4, config.Layers.Count);
        Assert.Equal("default_layer", config.Layers[0].Name);
        Assert.Equal("num_layer", config.Layers[1].Name);
        Assert.Equal("sym_layer", config.Layers[2].Name);
        Assert.Equal("func_layer", config.Layers[3].Name);
    }

    [Fact]
    public void CorneKeymap_ResolvesUserHoldTapArities()
    {
        // &hm is a user-defined hold-tap with #binding-cells = <2>. When it
        // shows up in a layer as `&hm LGUI A`, the slicer must consume two
        // params using the registered arity.
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("corne.keymap"), "corne");
        var hm = config.Layers[0].Bindings.First(b => b.Behavior == "&hm");
        Assert.Equal(2, hm.Params.Count);
    }

    [Fact]
    public void CorneKeymap_HasCombosAndMacro()
    {
        var config = ZmkKeymapLoader.LoadFromText(LoadFixture("corne.keymap"), "corne");
        Assert.Equal(2, config.Combos.Count);
        Assert.Single(config.Macros);
        Assert.Equal("&vim_quit", config.Macros[0].Name);
    }

    [Fact]
    public void CorneBrokenKeymap_ThrowsParseException()
    {
        // Real failure mode: the file is truncated mid-bindings array — the
        // closing '>' never appears. Must surface as an error rather than a
        // silently partial parse.
        var ex = Assert.Throws<ZmkKeymapParseException>(
            () => ZmkKeymapLoader.LoadFromText(LoadFixture("corne-broken.keymap"), "corne"));
        Assert.Contains("Unterminated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
