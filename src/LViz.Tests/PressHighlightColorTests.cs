using LViz.App.ViewModels;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

public class PressHighlightColorTests
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    [Fact]
    public void DefaultHighlightColor_MatchesOriginalYellow()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService());
        Assert.Equal("#FFD60A", vm.PressHighlightColor);
        // 0xFF * 0.55 = 140.25 → 140 = 0x8C, 0xD6 * 0.55 = 117.7 → 117 = 0x75, 0x0A * 0.55 = 5.5 → 5 = 0x05
        Assert.Equal("#8C7505", vm.PressHighlightStrokeColor);
    }

    [Fact]
    public void StrokeColor_DerivesFromFill()
    {
        var vm = new MainWindowViewModel(new InMemorySettingsService()) { PressHighlightColor = "#FF00FF" };
        // 0xFF * 0.55 = 140 = 0x8C; 0x00 stays 0x00
        Assert.Equal("#8C008C", vm.PressHighlightStrokeColor);
    }

    [Fact]
    public void SettingChange_PersistsToSettingsService()
    {
        var settings = new InMemorySettingsService();
        var vm = new MainWindowViewModel(settings) { PressHighlightColor = "#123456" };
        Assert.Equal("#123456", settings.Current.PressHighlightColor);
    }

    [Fact]
    public void SeedsFromExistingSettings()
    {
        var settings = new InMemorySettingsService { Current = new UserSettings { PressHighlightColor = "#ABCDEF" } };
        var vm = new MainWindowViewModel(settings);
        Assert.Equal("#ABCDEF", vm.PressHighlightColor);
    }
}
