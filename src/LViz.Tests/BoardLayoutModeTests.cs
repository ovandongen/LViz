using LViz.App.ViewModels;
using LViz.Core.Layout;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

public class BoardLayoutModeTests
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    [Fact]
    public void HorizontalMode_PreservesProfileCanvas()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService());
        Assert.False(vm.IsStackedLayout);

        var profile = new CorneProfile();
        Assert.Equal(profile.CanvasWidth, vm.CanvasWidth);
        Assert.Equal(profile.CanvasHeight, vm.CanvasHeight);
        Assert.Equal(0, vm.LeftHandX);
        Assert.Equal(0, vm.LeftHandY);
        Assert.Equal(0, vm.RightHandX);
        Assert.Equal(0, vm.RightHandY);
    }

    [Fact]
    public void StackedMode_StacksHalvesVertically()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService()) { IsStackedLayout = true };
        var profile = new CorneProfile();

        // Stacked canvas should be roughly: each half's height + gap, instead of side-by-side.
        Assert.True(vm.CanvasHeight > profile.CanvasHeight,
            $"Expected stacked height > profile height; got {vm.CanvasHeight} vs {profile.CanvasHeight}");
        Assert.True(vm.CanvasWidth < profile.CanvasWidth,
            $"Expected stacked width < profile width; got {vm.CanvasWidth} vs {profile.CanvasWidth}");
    }

    [Fact]
    public void StackedMode_LeftOnTop_PutsRightBelow()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService())
        {
            IsStackedLayout = true,
            StackedTopHand = "Left",
        };
        Assert.True(vm.RightHandY > vm.LeftHandY,
            $"Right should sit below Left; got LeftY={vm.LeftHandY}, RightY={vm.RightHandY}");
    }

    [Fact]
    public void StackedMode_RightOnTop_PutsLeftBelow()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService())
        {
            IsStackedLayout = true,
            StackedTopHand = "Right",
        };
        Assert.True(vm.LeftHandY > vm.RightHandY,
            $"Left should sit below Right; got LeftY={vm.LeftHandY}, RightY={vm.RightHandY}");
    }

    [Fact]
    public void StackedMode_TopHandToggleSwapsWhichHandSitsLower()
    {
        // The two halves don't have to be equal-height, so absolute Y values
        // won't be a literal swap — just the *ordering* should flip.
        var vm = new MainWindowViewModel(new InMemorySettingsService()) { IsStackedLayout = true };
        vm.StackedTopHand = "Left";
        Assert.True(vm.LeftHandY < vm.RightHandY);

        vm.StackedTopHand = "Right";
        Assert.True(vm.RightHandY < vm.LeftHandY);
    }

    [Fact]
    public void CorneProfile_TagsKeysWithHand()
    {
        var keys = new CorneProfile().Keys;
        // Corne 6-col: 42 keys split 21 / 21. Index 0 = left outer pinky;
        // index 6 = right inner index (first key past the midline).
        Assert.Equal(Hand.Left, keys[0].Hand);
        Assert.Equal(Hand.Right, keys[6].Hand);

        Assert.Equal(21, keys.Count(k => k.Hand == Hand.Left));
        Assert.Equal(21, keys.Count(k => k.Hand == Hand.Right));
    }

    [Fact]
    public void Settings_RoundTripStackedFields()
    {
        var settings = new InMemorySettingsService();
        var vm = new MainWindowViewModel(settings)
        {
            IsStackedLayout = true,
            StackedTopHand = "Right",
        };
        Assert.True(settings.Current.StackedLayout);
        Assert.Equal("Right", settings.Current.StackedTopHand);

        // Round-trip: a new VM seeded from the persisted settings should reflect them.
        var vm2 = new MainWindowViewModel(settings);
        Assert.True(vm2.IsStackedLayout);
        Assert.Equal("Right", vm2.StackedTopHand);
    }

    [Fact]
    public void BuildKeysFromProfile_PartitionsKeysByHand()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService());
        Assert.Equal(42, vm.Keys.Count);
        Assert.Equal(21, vm.LeftKeys.Count);
        Assert.Equal(21, vm.RightKeys.Count);
        Assert.All(vm.LeftKeys, k => Assert.Equal(Hand.Left, k.Position.Hand));
        Assert.All(vm.RightKeys, k => Assert.Equal(Hand.Right, k.Position.Hand));
    }
}
