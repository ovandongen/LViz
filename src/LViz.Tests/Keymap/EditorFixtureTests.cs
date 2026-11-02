using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// Gate for the keymap-editor fixtures (Olaf's real Cosmos and Slabwing
/// keymaps): both must parse and map one-to-one onto their source text, because
/// the layer writer and edit-session tests build on them. Cosmos uses plain layer
/// numbers only; Slabwing has unused layer defines, a conditional layer and a
/// combo bound to <c>&amp;mo 2</c>.
/// </summary>
public sealed class EditorFixtureTests
{
    [Theory]
    [InlineData("cosmos82.keymap", 7, 82)]
    [InlineData("slabwing.keymap", 7, 56)]
    public void Fixture_parses_and_maps_every_binding(string file, int layers, int keys)
    {
        var source = KeymapFixtures.Read(file);

        var config = ZmkKeymapLoader.LoadFromText(source, "fixture");
        var maps = KeymapSourceMap.Build(source, config);

        Assert.Equal(layers, config.Layers.Count);
        Assert.All(config.Layers, l => Assert.Equal(keys, l.Bindings.Count));
        Assert.Equal(layers, maps.Count);
        Assert.All(maps, m => Assert.Equal(keys, m.Bindings.Count));
    }
}
