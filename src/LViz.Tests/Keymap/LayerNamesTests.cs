using LViz.Core.Keymap.Editing;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// Picking a free layer name: the wanted one when nothing holds it, else the
/// first "wanted N" that no entry holds.
/// </summary>
public class LayerNamesTests
{
    [Fact]
    public void Unique_NotTaken_ReturnsWanted()
    {
        Assert.Equal("Layer 3", LayerNames.Unique("Layer 3", ["Base", "Nav"]));
    }

    [Fact]
    public void Unique_Taken_AppendsTwo()
    {
        Assert.Equal("Nav copy 2", LayerNames.Unique("Nav copy", ["Nav", "Nav copy"]));
    }

    [Fact]
    public void Unique_NumberedSuffixesTaken_SkipsToTheFirstFree()
    {
        Assert.Equal("Nav copy 3", LayerNames.Unique("Nav copy", ["Nav copy", "Nav copy 2"]));
    }

    [Fact]
    public void Unique_ComparesOrdinally_CaseMatters()
    {
        Assert.Equal("nav", LayerNames.Unique("nav", ["Nav"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Unique_BlankWanted_Throws(string wanted)
    {
        Assert.Throws<ArgumentException>(() => LayerNames.Unique(wanted, ["Nav"]));
    }

    [Fact]
    public void Unique_NullWanted_Throws()
    {
        Assert.Throws<ArgumentException>(() => LayerNames.Unique(null!, ["Nav"]));
    }

    [Fact]
    public void Unique_NullTaken_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LayerNames.Unique("Nav", null!));
    }
}
