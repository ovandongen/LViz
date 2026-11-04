using LViz.App.ViewModels;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// <see cref="LayerViewModel"/> edit affordances: without a
/// <see cref="LayerTabActions"/> bundle every edit command is inert, and with
/// one the commands call the matching action with the tab's own index while
/// the two move commands stay bounded by position and layer count.
/// </summary>
public class LayerViewModelTests
{
    /// <summary>Actions that log "<c>what</c>:<c>index</c>[:<c>delta</c>]"
    /// for a layer list of <paramref name="layerCount"/> layers.</summary>
    private static LayerTabActions Actions(List<string> log, int layerCount) => new(
        i => log.Add($"rename:{i}"),
        i => log.Add($"duplicate:{i}"),
        (i, delta) => log.Add($"move:{i}:{delta}"),
        i => log.Add($"delete:{i}"),
        layerCount);

    [Fact]
    public void NoActions_NotEditable_EveryEditCommandDisabled()
    {
        var vm = new LayerViewModel(0, "0 : Base", "#89B4FA", _ => { });

        Assert.False(vm.IsEditable);
        Assert.False(vm.CanMoveLeft);
        Assert.False(vm.CanMoveRight);
        Assert.False(vm.RenameCommand.CanExecute(null));
        Assert.False(vm.DuplicateCommand.CanExecute(null));
        Assert.False(vm.MoveLeftCommand.CanExecute(null));
        Assert.False(vm.MoveRightCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public void WithActions_MoveBounds_FollowPosition()
    {
        var log = new List<string>();

        var first = new LayerViewModel(0, "0 : Base", "#89B4FA", _ => { }, Actions(log, 3));
        var last = new LayerViewModel(2, "2 : Raise", "#A6E3A1", _ => { }, Actions(log, 3));

        Assert.True(first.IsEditable);
        Assert.False(first.CanMoveLeft);
        Assert.True(first.CanMoveRight);
        Assert.False(first.MoveLeftCommand.CanExecute(null));
        Assert.True(first.MoveRightCommand.CanExecute(null));

        Assert.True(last.CanMoveLeft);
        Assert.False(last.CanMoveRight);
        Assert.True(last.MoveLeftCommand.CanExecute(null));
        Assert.False(last.MoveRightCommand.CanExecute(null));
    }

    [Fact]
    public void Commands_CallTheirActionWithTheIndex()
    {
        var log = new List<string>();
        var vm = new LayerViewModel(1, "1 : Lower", "#F38BA8", _ => { }, Actions(log, 3));

        vm.MoveRightCommand.Execute(null);
        vm.MoveLeftCommand.Execute(null);
        vm.RenameCommand.Execute(null);
        vm.DuplicateCommand.Execute(null);
        vm.DeleteCommand.Execute(null);

        Assert.Equal(
            new[] { "move:1:1", "move:1:-1", "rename:1", "duplicate:1", "delete:1" },
            log);
    }
}
