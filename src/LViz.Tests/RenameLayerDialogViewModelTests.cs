using System.ComponentModel;
using LViz.App.Localization;
using LViz.App.ViewModels;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The rename-layer dialog's view model: the gate that decides whether the
/// typed name is a usable new layer name. Everything the dialog can answer
/// is trimmed; nothing that equals the current name, is blank, or holds a
/// control character is confirmable.
/// </summary>
public class RenameLayerDialogViewModelTests
{
    private const string Current = "Raise";

    private static RenameLayerDialogViewModel Vm(int layerIndex = 1, string current = Current) =>
        new(new RenameLayerRequest(layerIndex, current));

    [Fact]
    public void FreshVm_PrefillsCurrentName_AndNothingToConfirmYet()
    {
        var vm = Vm(layerIndex: 2);

        Assert.Equal(Current, vm.Name);
        Assert.False(vm.CanConfirm);
        Assert.Null(vm.Answer());
        Assert.Equal(Loc.Instance["Edit_RenameLayerTitle"], vm.Title);
        Assert.Equal(
            Loc.Instance.Format("Edit_RenameLayerPromptFormat", 2, Current),
            vm.Prompt);
    }

    [Fact]
    public void NewName_IsConfirmable_AndAnsweredTrimmed()
    {
        var vm = Vm();

        vm.Name = "Nav";
        Assert.True(vm.CanConfirm);
        Assert.Equal("Nav", vm.Answer());

        vm.Name = "  Nav  ";
        Assert.True(vm.CanConfirm);
        Assert.Equal("Nav", vm.Answer());
    }

    [Theory]
    [InlineData("")]        // nothing typed at all
    [InlineData("   ")]     // blank is not a name
    [InlineData("a\tb")]    // control characters are out of the alphabet
    [InlineData(" Raise ")] // padding around the current name changes nothing
    public void UnusableName_IsNotConfirmable_AnswersNull(string name)
    {
        var vm = Vm();

        vm.Name = name;

        Assert.False(vm.CanConfirm);
        Assert.Null(vm.Answer());
    }

    [Fact]
    public void NameChange_RaisesCanConfirmChanged()
    {
        var vm = Vm();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Name = "Nav";

        Assert.Contains(nameof(RenameLayerDialogViewModel.Name), changed);
        Assert.Contains(nameof(RenameLayerDialogViewModel.CanConfirm), changed);
    }
}
