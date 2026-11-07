using LViz.App.Localization;
using LViz.App.ViewModels;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// <see cref="GridQuestionsViewModel"/> on its own: defaults, clamping of the
/// three counts, the summary notifications and the keymap default (including
/// the split toggle). The wizard-level interplay (scan, editor, save) is in
/// <see cref="LayoutWizardViewModelTests"/>.
/// </summary>
public sealed class GridQuestionsViewModelTests
{
    private static GridQuestionsViewModel Create(int? keymapKeys) => new(() => keymapKeys);

    [Fact]
    public void Defaults_AreACorneLikeBoard()
    {
        var vm = Create(null);

        Assert.Equal(42, vm.KeyCount);
        Assert.Equal(Loc.Instance.Format("Wizard_GridKeyCountFormat", 42), vm.KeyCountText);
    }

    [Fact]
    public void Inputs_ClampToGeneratorLimits_AndRaiseTheSummary()
    {
        var vm = Create(null);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.KeysPerHalf = 0;
        Assert.Equal(1, vm.KeysPerHalf);
        vm.KeysPerHalf = 200;
        Assert.Equal(96, vm.KeysPerHalf);
        vm.Columns = 0;
        Assert.Equal(1, vm.Columns);
        vm.ThumbKeys = 99;
        Assert.Equal(8, vm.ThumbKeys);

        Assert.Contains(nameof(GridQuestionsViewModel.KeyCount), raised);
        Assert.Contains(nameof(GridQuestionsViewModel.MatchesKeymap), raised);
        Assert.Contains(nameof(GridQuestionsViewModel.KeyCountText), raised);
    }

    [Fact]
    public void Default_NoKeymap_KeepsEighteenPerHalf()
    {
        var vm = Create(null);

        Assert.Equal(18, vm.KeysPerHalf);
        Assert.Equal(42, vm.KeyCount);
    }

    [Fact]
    public void Default_LoadedKeymap_KeepsEighteenWhenItAlreadyMatches()
    {
        var vm = Create(42);

        // 42 / 2 - 3 = 18: the default answer was already the right one.
        Assert.Equal(18, vm.KeysPerHalf);
        Assert.Equal(42, vm.KeyCount);
        Assert.True(vm.MatchesKeymap);
    }

    [Fact]
    public void Default_LoadedKeymap_SizesKeysPerHalfToTheBindingCount()
    {
        var vm = Create(40);

        Assert.Equal(17, vm.KeysPerHalf); // 40 / 2 - 3
        Assert.Equal(40, vm.KeyCount);
        Assert.True(vm.MatchesKeymap);
    }

    [Fact]
    public void Default_LoadedKeymap_OddBindingCountRoundsDownAndStillWarns()
    {
        var vm = Create(43);

        // 43 / 2 = 21, minus 3 thumbs = 18 — and 42 generated keys don't fit 43.
        Assert.Equal(18, vm.KeysPerHalf);
        Assert.Equal(42, vm.KeyCount);
        Assert.False(vm.MatchesKeymap);
    }

    [Fact]
    public void Default_LoadedKeymap_BelowTheFloor_KeepsTheCurrentCount()
    {
        var vm = Create(4);

        // 4 / 2 - 3 = -1 < MinKeysPerHalf: no default, the 18 stays.
        Assert.Equal(18, vm.KeysPerHalf);
        Assert.Equal(42, vm.KeyCount);

        // Exactly 0 is below the floor too (1 would apply): 6 / 2 - 3 = 0.
        var zero = Create(6);
        Assert.Equal(18, zero.KeysPerHalf);
    }

    [Fact]
    public void Default_SplitToggle_ReappliesTheDefault()
    {
        var vm = Create(42);

        vm.Split = false;
        Assert.Equal(39, vm.KeysPerHalf); // 42 - 3
        Assert.True(vm.MatchesKeymap);

        vm.Split = true;
        Assert.Equal(18, vm.KeysPerHalf); // 42 / 2 - 3
        Assert.True(vm.MatchesKeymap);
    }

    [Fact]
    public void Default_SplitToggleAfterAUserCount_KeepsTheCount()
    {
        var vm = Create(42);
        vm.KeysPerHalf = 10;

        vm.Split = false;

        Assert.Equal(10, vm.KeysPerHalf);
    }
}
