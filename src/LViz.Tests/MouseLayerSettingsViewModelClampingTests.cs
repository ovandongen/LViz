using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Layout;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Endless mouse-layer timeout has no organic way to revert — it must be
/// paired with at least one of (ExitOnTransparentKey, ExitOnEmptyKey) so the
/// user always has a way back. MouseLayerSettingsViewModel owns the cross-field
/// invariant: enabling endless auto-arms transparent-key exit when neither
/// flag is set, and the per-flag setters refuse to disable the last enabled
/// flag while endless is on.
/// </summary>
public class MouseLayerSettingsViewModelClampingTests
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    private static MouseLayerSettingsViewModel NewVm(UserSettings? seed = null)
    {
        var settings = new InMemorySettingsService { Current = seed ?? new UserSettings { Keyboard = "GO60" } };
        var main = TestMainVm.Create(settings);
        // Mouse layer must be enabled for snapshot updates to flow back through
        // the engine without being short-circuited.
        var vm = new SettingsViewModel(settings, main, new UpdateService(settings)).MouseLayer;
        vm.IsEnabled = true;
        return vm;
    }

    [Fact]
    public void EnablingEndless_WithNeitherExitFlag_AutoEnablesTransparent()
    {
        var vm = NewVm();
        Assert.False(vm.IsExitOnTransparentKey);
        Assert.False(vm.IsExitOnEmptyKey);

        vm.IsEndlessTimeout = true;

        Assert.True(vm.IsEndlessTimeout);
        Assert.True(vm.IsExitOnTransparentKey);
        Assert.False(vm.IsExitOnEmptyKey);
    }

    [Fact]
    public void EnablingEndless_WhenEmptyOnly_LeavesEmptyAndDoesNotForceTransparent()
    {
        var vm = NewVm();
        vm.IsExitOnEmptyKey = true;

        vm.IsEndlessTimeout = true;

        Assert.True(vm.IsEndlessTimeout);
        Assert.False(vm.IsExitOnTransparentKey);
        Assert.True(vm.IsExitOnEmptyKey);
    }

    [Fact]
    public void DisablingLastExitFlag_WhileEndless_IsRefused()
    {
        var vm = NewVm();
        vm.IsEndlessTimeout = true; // auto-enables transparent
        Assert.True(vm.IsExitOnTransparentKey);

        vm.IsExitOnTransparentKey = false;

        Assert.True(vm.IsExitOnTransparentKey);
    }

    [Fact]
    public void DisablingOneOfTwoExitFlags_WhileEndless_IsAllowed()
    {
        var vm = NewVm();
        vm.IsExitOnTransparentKey = true;
        vm.IsExitOnEmptyKey = true;
        vm.IsEndlessTimeout = true;

        vm.IsExitOnEmptyKey = false;

        Assert.True(vm.IsEndlessTimeout);
        Assert.True(vm.IsExitOnTransparentKey);
        Assert.False(vm.IsExitOnEmptyKey);
    }

    [Fact]
    public void DisablingEndless_ThenDisablingExitFlag_IsAllowed()
    {
        var vm = NewVm();
        vm.IsEndlessTimeout = true; // auto-enables transparent

        vm.IsEndlessTimeout = false;
        vm.IsExitOnTransparentKey = false;

        Assert.False(vm.IsEndlessTimeout);
        Assert.False(vm.IsExitOnTransparentKey);
    }
}
