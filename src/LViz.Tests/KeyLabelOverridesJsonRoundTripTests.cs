using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Defends the persistence path for the new <see cref="UserSettings.KeyLabelOverrides"/>
/// dictionary. System.Text.Json supports int dictionary keys by default in
/// .NET 7+, but a three-level nested dict is a new shape — this test pins
/// the round-trip so a future schema change doesn't silently drop overrides.
/// </summary>
public class KeyLabelOverridesJsonRoundTripTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(),
        $"lviz-keylabels-{Guid.NewGuid():N}.json");

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var path = TempFile();
        try
        {
            var ov = new KeyLabelOverride
            {
                MainLabel = "nav",
                Subscript = "space",
                TopLeftBadge = "L1",
                Icon = "fa-arrows-up-down-left-right",
                FontSize = 22,
                Bold = true,
            };
            var input = new UserSettings
            {
                KeyLabelOverrides =
                {
                    ["corne"] = new() { [3] = new() { [17] = ov } },
                },
            };
            var svc = new SettingsService(path);
            svc.Save(input);

            var loaded = svc.Load();

            Assert.True(loaded.KeyLabelOverrides.ContainsKey("corne"));
            var got = loaded.KeyLabelOverrides["corne"][3][17];
            Assert.Equal("nav", got.MainLabel);
            Assert.Equal("space", got.Subscript);
            Assert.Equal("L1", got.TopLeftBadge);
            Assert.Equal("fa-arrows-up-down-left-right", got.Icon);
            Assert.Equal(22, got.FontSize);
            Assert.True(got.Bold);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RoundTrip_NullFontSizeSurvives()
    {
        var path = TempFile();
        try
        {
            var input = new UserSettings
            {
                KeyLabelOverrides =
                {
                    ["x"] = new() { [0] = new() { [0] = new KeyLabelOverride { MainLabel = "Q" } } },
                },
            };
            var svc = new SettingsService(path);
            svc.Save(input);

            var loaded = svc.Load();

            Assert.Null(loaded.KeyLabelOverrides["x"][0][0].FontSize);
            Assert.False(loaded.KeyLabelOverrides["x"][0][0].Bold);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RoundTrip_EmptyDictionary_RemainsEmpty()
    {
        var path = TempFile();
        try
        {
            var svc = new SettingsService(path);
            svc.Save(new UserSettings());

            var loaded = svc.Load();

            Assert.Empty(loaded.KeyLabelOverrides);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
