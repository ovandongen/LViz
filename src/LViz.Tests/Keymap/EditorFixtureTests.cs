using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// Gate for the keymap-editor fixtures (Olaf's real Cosmos, Slabwing, Glove80
/// and Go60 keymaps): all must parse and map one-to-one onto their source text,
/// because the layer writer, combo writer and edit-session tests build on them.
/// Cosmos uses plain layer numbers only and has no combos node; Slabwing has
/// unused layer defines, a conditional layer and four numbered combos (one bound
/// to <c>&amp;mo 2</c>); Glove80 and Go60 write combo positions as
/// <c>POS_*</c> defines and use MoErgo's <c>&amp;magic LAYER_Magic 0</c>.
/// </summary>
public sealed class EditorFixtureTests
{
    [Theory]
    [InlineData("cosmos82.keymap", 7, 82)]
    [InlineData("slabwing.keymap", 7, 56)]
    [InlineData("glove80.keymap", 8, 80)]
    [InlineData("go60.keymap", 8, 60)]
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

    [Theory]
    [InlineData("cosmos82.keymap", "")]
    [InlineData("slabwing.keymap", "combo_bootloader=1,2,12;mouse-mid=46,47;f12=0,1;syslayer=53,52")]
    [InlineData("glove80.keymap", "combo_snipe3=66,68")]
    [InlineData("go60.keymap", "combo_Space=55,56;combo_snipe3=48,50")]
    public void Fixture_combos_parse_with_their_key_positions(string file, string expected)
    {
        var config = ZmkKeymapLoader.LoadFromText(KeymapFixtures.Read(file), "fixture");

        var actual = string.Join(";", config.Combos.Select(
            c => c.Name + "=" + string.Join(",", c.KeyPositions)));

        Assert.Equal(expected, actual);
        Assert.All(config.Combos, c => Assert.True(c.AppliesToAllLayers));
    }

    [Theory]
    [InlineData("glove80.keymap")]
    [InlineData("go60.keymap")]
    public void Fixture_magic_is_a_layer_hold_tap_whose_tap_takes_no_parameter(string file)
    {
        var config = ZmkKeymapLoader.LoadFromText(KeymapFixtures.Read(file), "fixture");

        var magic = Assert.Single(config.HoldTaps, h => h.Name == "&magic");
        Assert.Equal("&mo", magic.HoldBinding);
        Assert.Equal(1, magic.HoldArity);
        Assert.Equal(0, magic.TapArity);
    }
}
