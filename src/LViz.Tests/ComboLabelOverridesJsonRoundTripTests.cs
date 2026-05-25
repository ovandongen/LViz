using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Defends the persistence path for <see cref="UserSettings.ComboLabelOverrides"/>.
/// Two-level <c>profile → keyPositionsKey → ComboLabelOverride</c>; string keys
/// throughout so System.Text.Json handles it without custom converters.
/// </summary>
public class ComboLabelOverridesJsonRoundTripTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(),
        $"lviz-combolabels-{Guid.NewGuid():N}.json");

    [Fact]
    public void RoundTrip_PreservesLabel()
    {
        var path = TempFile();
        try
        {
            var input = new UserSettings
            {
                ComboLabelOverrides =
                {
                    ["Kyria"] = new()
                    {
                        ["12,13"] = new() { MainLabel = "[]" },
                        ["14,15"] = new() { MainLabel = "{}" },
                    },
                },
            };
            var svc = new SettingsService(path);
            svc.Save(input);

            var loaded = svc.Load();

            Assert.True(loaded.ComboLabelOverrides.ContainsKey("Kyria"));
            Assert.Equal("[]", loaded.ComboLabelOverrides["Kyria"]["12,13"].MainLabel);
            Assert.Equal("{}", loaded.ComboLabelOverrides["Kyria"]["14,15"].MainLabel);
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

            Assert.Empty(loaded.ComboLabelOverrides);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingComboOverridesKey_ResolvesToEmpty()
    {
        // Settings files written before this feature have no ComboLabelOverrides
        // key. The default initializer should produce an empty dict, not null.
        var path = TempFile();
        try
        {
            File.WriteAllText(path, """{"SchemaVersion": 1, "Keyboard": "Corne"}""");
            var svc = new SettingsService(path);
            var loaded = svc.Load();

            Assert.NotNull(loaded.ComboLabelOverrides);
            Assert.Empty(loaded.ComboLabelOverrides);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
