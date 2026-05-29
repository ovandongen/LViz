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
        Assert.False(vm.BoardLayout.IsStackedLayout);

        var profile = new CorneProfile();
        Assert.Equal(profile.CanvasWidth, vm.BoardLayout.CanvasWidth);
        Assert.Equal(profile.CanvasHeight, vm.BoardLayout.CanvasHeight);
        Assert.Equal(0, vm.BoardLayout.LeftHandX);
        Assert.Equal(0, vm.BoardLayout.LeftHandY);
        Assert.Equal(0, vm.BoardLayout.RightHandX);
        Assert.Equal(0, vm.BoardLayout.RightHandY);
    }

    [Fact]
    public void StackedMode_StacksHalvesVertically()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService());
        vm.BoardLayout.IsStackedLayout = true;
        var profile = new CorneProfile();

        // Stacked canvas should be roughly: each half's height + gap, instead of side-by-side.
        Assert.True(vm.BoardLayout.CanvasHeight > profile.CanvasHeight,
            $"Expected stacked height > profile height; got {vm.BoardLayout.CanvasHeight} vs {profile.CanvasHeight}");
        Assert.True(vm.BoardLayout.CanvasWidth < profile.CanvasWidth,
            $"Expected stacked width < profile width; got {vm.BoardLayout.CanvasWidth} vs {profile.CanvasWidth}");
    }

    [Fact]
    public void StackedMode_LeftOnTop_PutsRightBelow()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService());
        vm.BoardLayout.IsStackedLayout = true;
        vm.BoardLayout.StackedTopHand = "Left";
        Assert.True(vm.BoardLayout.RightHandY > vm.BoardLayout.LeftHandY,
            $"Right should sit below Left; got LeftY={vm.BoardLayout.LeftHandY}, RightY={vm.BoardLayout.RightHandY}");
    }

    [Fact]
    public void StackedMode_RightOnTop_PutsLeftBelow()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService());
        vm.BoardLayout.IsStackedLayout = true;
        vm.BoardLayout.StackedTopHand = "Right";
        Assert.True(vm.BoardLayout.LeftHandY > vm.BoardLayout.RightHandY,
            $"Left should sit below Right; got LeftY={vm.BoardLayout.LeftHandY}, RightY={vm.BoardLayout.RightHandY}");
    }

    [Fact]
    public void StackedMode_TopHandToggleSwapsWhichHandSitsLower()
    {
        // The two halves don't have to be equal-height, so absolute Y values
        // won't be a literal swap — just the *ordering* should flip.
        var vm = new MainWindowViewModel(new InMemorySettingsService());
        vm.BoardLayout.IsStackedLayout = true;
        vm.BoardLayout.StackedTopHand = "Left";
        Assert.True(vm.BoardLayout.LeftHandY < vm.BoardLayout.RightHandY);

        vm.BoardLayout.StackedTopHand = "Right";
        Assert.True(vm.BoardLayout.RightHandY < vm.BoardLayout.LeftHandY);
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
        var vm = new MainWindowViewModel(settings);
        vm.BoardLayout.IsStackedLayout = true;
        vm.BoardLayout.StackedTopHand = "Right";
        Assert.True(settings.Current.StackedLayout);
        Assert.Equal("Right", settings.Current.StackedTopHand);

        // Round-trip: a new VM seeded from the persisted settings should reflect them.
        var vm2 = new MainWindowViewModel(settings);
        Assert.True(vm2.BoardLayout.IsStackedLayout);
        Assert.Equal("Right", vm2.BoardLayout.StackedTopHand);
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
