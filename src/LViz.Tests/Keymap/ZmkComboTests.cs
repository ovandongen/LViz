using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// <see cref="ZmkCombo.IsSameAs"/> — the "did this combo change as far as
/// the file is concerned?" check the combo editor compares drafts with.
/// </summary>
public class ZmkComboTests
{
    private static ZmkCombo Combo(
        string? node = "combo_x",
        string name = "Esc",
        int[]? positions = null,
        int[]? layers = null,
        string[]? bindingParams = null,
        int? timeout = null,
        int? idle = null,
        bool slowRelease = false,
        string description = "") =>
        new(name, description, positions ?? new[] { 0, 1 }, layers ?? new[] { -1 },
            new KeyBinding("&kp", bindingParams ?? new[] { "A" }))
        {
            NodeName = node,
            TimeoutMs = timeout,
            RequirePriorIdleMs = idle,
            SlowRelease = slowRelease,
        };

    [Fact]
    public void IdenticalCombos_AreSame() =>
        Assert.True(Combo().IsSameAs(Combo()));

    [Fact]
    public void SamePositionsInDifferentOrder_AreSame() =>
        Assert.True(Combo(positions: new[] { 53, 52 }).IsSameAs(Combo(positions: new[] { 52, 53 })));

    [Fact]
    public void DifferentPositionCount_IsNotSame() =>
        Assert.False(Combo(positions: new[] { 0, 1 }).IsSameAs(Combo(positions: new[] { 0, 1, 2 })));

    [Fact]
    public void AllLayersMinusOne_VersusEmptyList_AreSame() =>
        Assert.True(Combo(layers: new[] { -1 }).IsSameAs(Combo(layers: new int[0])));

    [Fact]
    public void SameLayersInDifferentOrder_AreSame() =>
        Assert.True(Combo(layers: new[] { 1, 2 }).IsSameAs(Combo(layers: new[] { 2, 1 })));

    [Fact]
    public void OneLayer_VersusAllLayers_IsNotSame() =>
        Assert.False(Combo(layers: new[] { 1 }).IsSameAs(Combo(layers: new[] { -1 })));

    [Fact]
    public void DifferentTimeout_IsNotSame() =>
        Assert.False(Combo(timeout: 80).IsSameAs(Combo()));

    [Fact]
    public void DifferentPriorIdle_IsNotSame() =>
        Assert.False(Combo(idle: 150).IsSameAs(Combo()));

    [Fact]
    public void DifferentSlowRelease_IsNotSame() =>
        Assert.False(Combo(slowRelease: true).IsSameAs(Combo()));

    [Fact]
    public void DifferentNodeName_IsNotSame() =>
        Assert.False(Combo(node: "combo_a").IsSameAs(Combo(node: "combo_b")));

    [Fact]
    public void DifferentBindingParam_IsNotSame() =>
        Assert.False(Combo(bindingParams: new[] { "A" }).IsSameAs(Combo(bindingParams: new[] { "B" })));

    [Fact]
    public void DifferentDescription_IsSame() =>
        Assert.True(Combo(description: "").IsSameAs(Combo(description: "the escape combo")));
}
