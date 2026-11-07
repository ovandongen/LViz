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
        var vm = TestMainVm.Create(new InMemorySettingsService());
        Assert.False(vm.Board.BoardLayout.IsStackedLayout);

        var profile = new CorneProfile();
        Assert.Equal(profile.CanvasWidth, vm.Board.BoardLayout.CanvasWidth);
        Assert.Equal(profile.CanvasHeight, vm.Board.BoardLayout.CanvasHeight);
        Assert.Equal(0, vm.Board.BoardLayout.LeftHandX);
        Assert.Equal(0, vm.Board.BoardLayout.LeftHandY);
        Assert.Equal(0, vm.Board.BoardLayout.RightHandX);
        Assert.Equal(0, vm.Board.BoardLayout.RightHandY);
    }

    [Fact]
    public void StackedMode_StacksHalvesVertically()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService());
        vm.Board.BoardLayout.IsStackedLayout = true;
        var profile = new CorneProfile();

        // Stacked canvas should be roughly: each half's height + gap, instead of side-by-side.
        Assert.True(vm.Board.BoardLayout.CanvasHeight > profile.CanvasHeight,
            $"Expected stacked height > profile height; got {vm.Board.BoardLayout.CanvasHeight} vs {profile.CanvasHeight}");
        Assert.True(vm.Board.BoardLayout.CanvasWidth < profile.CanvasWidth,
            $"Expected stacked width < profile width; got {vm.Board.BoardLayout.CanvasWidth} vs {profile.CanvasWidth}");
    }

    [Fact]
    public void StackedMode_LeftOnTop_PutsRightBelow()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService());
        vm.Board.BoardLayout.IsStackedLayout = true;
        vm.Board.BoardLayout.StackedTopHand = "Left";
        Assert.True(vm.Board.BoardLayout.RightHandY > vm.Board.BoardLayout.LeftHandY,
            $"Right should sit below Left; got LeftY={vm.Board.BoardLayout.LeftHandY}, RightY={vm.Board.BoardLayout.RightHandY}");
    }

    [Fact]
    public void StackedMode_RightOnTop_PutsLeftBelow()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService());
        vm.Board.BoardLayout.IsStackedLayout = true;
        vm.Board.BoardLayout.StackedTopHand = "Right";
        Assert.True(vm.Board.BoardLayout.LeftHandY > vm.Board.BoardLayout.RightHandY,
            $"Left should sit below Right; got LeftY={vm.Board.BoardLayout.LeftHandY}, RightY={vm.Board.BoardLayout.RightHandY}");
    }

    [Fact]
    public void StackedMode_TopHandToggleSwapsWhichHandSitsLower()
    {
        // The two halves don't have to be equal-height, so absolute Y values
        // won't be a literal swap — just the *ordering* should flip.
        var vm = TestMainVm.Create(new InMemorySettingsService());
        vm.Board.BoardLayout.IsStackedLayout = true;
        vm.Board.BoardLayout.StackedTopHand = "Left";
        Assert.True(vm.Board.BoardLayout.LeftHandY < vm.Board.BoardLayout.RightHandY);

        vm.Board.BoardLayout.StackedTopHand = "Right";
        Assert.True(vm.Board.BoardLayout.RightHandY < vm.Board.BoardLayout.LeftHandY);
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
        var vm = TestMainVm.Create(settings);
        vm.Board.BoardLayout.IsStackedLayout = true;
        vm.Board.BoardLayout.StackedTopHand = "Right";
        Assert.True(settings.Current.StackedLayout);
        Assert.Equal("Right", settings.Current.StackedTopHand);

        // Round-trip: a new VM seeded from the persisted settings should reflect them.
        var vm2 = TestMainVm.Create(settings);
        Assert.True(vm2.Board.BoardLayout.IsStackedLayout);
        Assert.Equal("Right", vm2.Board.BoardLayout.StackedTopHand);
    }

    [Fact]
    public void BuildKeysFromProfile_PartitionsKeysByHand()
    {
        var vm = TestMainVm.Create(new InMemorySettingsService());
        Assert.Equal(42, vm.Board.Keys.Count);
        Assert.Equal(21, vm.Board.LeftKeys.Count);
        Assert.Equal(21, vm.Board.RightKeys.Count);
        Assert.All(vm.Board.LeftKeys, k => Assert.Equal(Hand.Left, k.Position.Hand));
        Assert.All(vm.Board.RightKeys, k => Assert.Equal(Hand.Right, k.Position.Hand));
    }
}
