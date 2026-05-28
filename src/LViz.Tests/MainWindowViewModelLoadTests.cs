using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Drives <see cref="MainWindowViewModel.LoadLayoutFromPath"/> through a fake
/// <see cref="IKeymapStateService"/> so the load orchestration (render,
/// profile auto-switch, failure isolation) is testable without disk IO.
/// </summary>
public class MainWindowViewModelLoadTests
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    // Returns a canned result and records state the way the real service does.
    private sealed class FakeKeymapStateService : IKeymapStateService
    {
        private readonly KeymapLoadResult _result;
        public FakeKeymapStateService(KeymapLoadResult result) => _result = result;

        public LoadedKeymap? Current { get; private set; }
        public bool HasLayout => Current is not null;
        public string? LoadedPath { get; private set; }
        public string? LastLoadError { get; private set; }

        public TransparentBindingKind? ClassifyBinding(int layer, int key)
            => Current?.ClassifyBinding(layer, key);

        public KeymapLoadResult Load(string path, string profileId)
        {
            if (_result is KeymapLoadResult.Success s)
            {
                Current = s.Keymap;
                LoadedPath = path;
                LastLoadError = null;
            }
            else if (_result is KeymapLoadResult.Failure f)
            {
                LastLoadError = $"{path}: {f.Error.GetType().Name}: {f.Error.Message}";
            }
            return _result;
        }

        public void Clear() { Current = null; LoadedPath = null; }
    }

    private const string TwoLayerSource = @"
        / {
            keymap {
                compatible = ""zmk,keymap"";
                default_layer { display-name = ""Base""; bindings = <&kp A &kp B &kp C>; };
                lower         { display-name = ""Lower""; bindings = <&kp X &kp Y &kp Z>; };
            };
        };";

    private static LoadedKeymap SampleKeymap() =>
        new(ZmkKeymapLoader.LoadFromText(TwoLayerSource, "test"));

    [Fact]
    public void LoadLayoutFromPath_ParseFailure_LeavesProfileAndLayoutUntouched()
    {
        var settings = new InMemorySettingsService();
        var failing = new FakeKeymapStateService(
            new KeymapLoadResult.Failure("bad.keymap", new InvalidOperationException("boom")));
        var vm = new MainWindowViewModel(settings, keymapState: failing);

        var profileBefore = vm.SelectedKeyboard;
        vm.LoadLayoutFromPath("bad.keymap");

        // The fix: a parse failure must not mutate the profile or claim a layout.
        Assert.Same(profileBefore, vm.SelectedKeyboard);
        Assert.False(vm.HasLayoutLoaded);
        Assert.Empty(vm.Layers);
    }

    [Fact]
    public void LoadLayoutFromPath_Success_PopulatesLayers()
    {
        var settings = new InMemorySettingsService();
        // Binding count matches Corne (42) → no auto-switch, stays put.
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "ok.keymap", LayerCount: 2, FirstLayerBindingCount: 42));
        var vm = new MainWindowViewModel(settings, keymapState: ok);

        vm.LoadLayoutFromPath("ok.keymap");

        Assert.True(vm.HasLayoutLoaded);
        Assert.Equal(2, vm.Layers.Count);
        Assert.Equal("Corne", vm.SelectedKeyboard.Id);
    }

    [Fact]
    public void LoadLayoutFromPath_BindingCountMatchesOtherProfile_AutoSwitches()
    {
        var settings = new InMemorySettingsService();
        // 80 bindings matches the Glove80 profile → the load should auto-switch.
        var ok = new FakeKeymapStateService(
            new KeymapLoadResult.Success(SampleKeymap(), "glove.keymap", LayerCount: 2, FirstLayerBindingCount: 80));
        var vm = new MainWindowViewModel(settings, keymapState: ok);
        Assert.Equal("Corne", vm.SelectedKeyboard.Id);

        vm.LoadLayoutFromPath("glove.keymap");

        Assert.Equal("Glove80", vm.SelectedKeyboard.Id);
        Assert.Equal("Glove80", settings.Current.Keyboard);
        Assert.True(vm.HasLayoutLoaded);
    }
}
