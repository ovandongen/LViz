using LViz.App.ViewModels;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

public class BackgroundOpacityTests
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public UserSettings Current { get; set; } = new();
        public UserSettings Load() => Current;
        public void Save(UserSettings settings) => Current = settings;
    }

    private static MainWindowViewModel NewVm(double opacity) =>
        new(new InMemorySettingsService { Current = new UserSettings { BackgroundOpacity = opacity } });

    [Theory]
    [InlineData(0.0, "#18182500")]
    [InlineData(0.5, "#1818257F")]
    [InlineData(1.0, "#181825FF")]
    public void BoardBackground_AlphaScalesLinearlyAcrossSlider(double opacity, string expected)
    {
        Assert.Equal(expected, NewVm(opacity).BoardBackground);
    }

    [Theory]
    // baseAlpha = 0x66 (102). 0.0 → 0x66; 1.0 → 0xFF; 0.5 → 102 + 76 = 178 = 0xB2.
    [InlineData(0.0, "#18182566")]
    [InlineData(0.5, "#181825B2")]
    [InlineData(1.0, "#181825FF")]
    public void TabBackground_BlendsBaseAlphaWithSlider(double opacity, string expected)
    {
        Assert.Equal(expected, NewVm(opacity).TabBackground);
    }

    [Fact]
    public void SettingChange_PersistsToSettingsService()
    {
        var settings = new InMemorySettingsService();
        var vm = new MainWindowViewModel(settings);
        vm.BackgroundOpacity = 0.25;
        Assert.Equal(0.25, settings.Current.BackgroundOpacity);
    }
}
