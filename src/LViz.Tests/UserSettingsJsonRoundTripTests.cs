using System.Text.Json;
using LViz.Core.Settings;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Pins the forward/backward-compat properties of <see cref="UserSettings"/>'
/// JSON shape that the HID-only refactor relied on but never had explicit
/// coverage for: silently dropping keys removed in the refactor, tolerating
/// missing per-profile dictionary entries, and round-tripping the
/// post-refactor schema without losing fields.
/// </summary>
public class UserSettingsJsonRoundTripTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Load_IgnoresLegacyLayerSourceKey()
    {
        // Phase 1/2 of the HID-only refactor dropped UserSettings.LayerSource.
        // Old settings files in the wild still carry it; we must not blow up.
        var json = """{"SchemaVersion": 1, "LayerSource": "SharpHook", "Keyboard": "GO60"}""";

        var parsed = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(parsed);
        Assert.Equal("GO60", parsed!.Keyboard);
    }

    [Fact]
    public void Load_IgnoresLegacyManualLayerSignalsKey()
    {
        // Same as above — the macro-signal subsystem was removed.
        var json = """
            {
                "SchemaVersion": 1,
                "ManualLayerSignals": {"GO60": [{"Signal": "F18", "LayerIndex": 2}]},
                "Keyboard": "Glove80"
            }
            """;

        var parsed = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(parsed);
        Assert.Equal("Glove80", parsed!.Keyboard);
    }

    [Fact]
    public void Load_MissingMouseLayerDictionary_ResolvesToEmpty()
    {
        // Settings files written before Phase 4 of the refactor have no
        // MouseLayer key. The default initializer should kick in.
        var json = """{"SchemaVersion": 1, "Keyboard": "GO60"}""";

        var parsed = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.MouseLayer);
        Assert.Empty(parsed.MouseLayer);
    }

    [Fact]
    public void Load_MissingPerProfileMouseLayerEntry_DoesNotPopulateDefault()
    {
        // Engine treats "no entry for this profile" as a disabled default —
        // the JSON should preserve that absence (not auto-populate).
        var json = """
            {
                "SchemaVersion": 1,
                "MouseLayer": {"Glove80": {"Enabled": true, "MouseLayerIndex": 4, "IdleTimeoutMs": 600}}
            }
            """;

        var parsed = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(parsed);
        Assert.True(parsed!.MouseLayer.ContainsKey("Glove80"));
        Assert.False(parsed.MouseLayer.ContainsKey("GO60"));
    }

    [Fact]
    public void Load_TolratesUnknownAdHocKey()
    {
        // System.Text.Json's default behaviour drops unknown keys; pinning it
        // so a future "preserve unknown" change doesn't quietly break old
        // settings round-trips.
        var json = """{"SchemaVersion": 1, "SomeFutureKey": "ignored", "Keyboard": "GO60"}""";

        var parsed = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(parsed);
        Assert.Equal("GO60", parsed!.Keyboard);
    }

    [Fact]
    public void RoundTrip_PreservesMouseLayerSettings()
    {
        var original = new UserSettings
        {
            Keyboard = "Glove80",
            MouseLayer = new Dictionary<string, MouseLayerSettings>
            {
                ["GO60"] = new(Enabled: true, MouseLayerIndex: 3, IdleTimeoutMs: 750),
                ["Glove80"] = new(Enabled: false, MouseLayerIndex: null, IdleTimeoutMs: 500),
            },
        };

        var json = JsonSerializer.Serialize(original, Options);
        var rehydrated = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(rehydrated);
        Assert.Equal(original.MouseLayer["GO60"], rehydrated!.MouseLayer["GO60"]);
        Assert.Equal(original.MouseLayer["Glove80"], rehydrated.MouseLayer["Glove80"]);
    }

    [Fact]
    public void RoundTrip_PreservesAutoSwitchAndHotkeySettings()
    {
        var original = new UserSettings
        {
            HotkeyKey = "F18",
            HotkeyModifiers = "Ctrl+Alt",
            AutoSwitchKeyboardLayer = true,
            ColorTrayIconByActiveLayer = false,
            ModifierStyle = "Windows",
            AutoSwitchExitKey = new Dictionary<string, int> { ["GO60"] = 42 },
            AutoSwitchFallback = new Dictionary<string, AutoSwitchFallbackMode>
            {
                ["GO60"] = AutoSwitchFallbackMode.Previous,
            },
        };

        var json = JsonSerializer.Serialize(original, Options);
        var rehydrated = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(rehydrated);
        Assert.Equal("F18", rehydrated!.HotkeyKey);
        Assert.Equal("Ctrl+Alt", rehydrated.HotkeyModifiers);
        Assert.True(rehydrated.AutoSwitchKeyboardLayer);
        Assert.False(rehydrated.ColorTrayIconByActiveLayer);
        Assert.Equal("Windows", rehydrated.ModifierStyle);
        Assert.Equal(42, rehydrated.AutoSwitchExitKey["GO60"]);
        Assert.Equal(AutoSwitchFallbackMode.Previous, rehydrated.AutoSwitchFallback["GO60"]);
    }

    [Fact]
    public void Load_MissingRoutingFields_ResolveToDefaults()
    {
        // Settings files written before the capability-bus feature carry neither
        // DeviceRoutingEnabled nor RoutingRules — the default initializers apply.
        var json = """{"SchemaVersion": 1, "Keyboard": "GO60"}""";

        var parsed = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(parsed);
        Assert.False(parsed!.DeviceRoutingEnabled);
        Assert.NotNull(parsed.RoutingRules);
        Assert.Empty(parsed.RoutingRules);
    }

    [Fact]
    public void RoundTrip_DefaultRouting_StaysDefault()
    {
        var json = JsonSerializer.Serialize(new UserSettings(), Options);
        var rehydrated = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(rehydrated);
        Assert.False(rehydrated!.DeviceRoutingEnabled);
        Assert.Empty(rehydrated.RoutingRules);
    }

    [Fact]
    public void RoundTrip_PreservesDeviceRoutingSettings()
    {
        var original = new UserSettings
        {
            DeviceRoutingEnabled = true,
            RoutingRules = new List<RoutingRule>
            {
                new("pointing.dpi.set", "keyboard", "1234:5678:bean", Enabled: true),
                new("pointing.snipe.set", "keyboard", "1234:5678:bean", Enabled: false),
            },
        };

        var json = JsonSerializer.Serialize(original, Options);
        var rehydrated = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(rehydrated);
        Assert.True(rehydrated!.DeviceRoutingEnabled);
        Assert.Equal(2, rehydrated.RoutingRules.Count);
        Assert.Equal(original.RoutingRules[0], rehydrated.RoutingRules[0]);
        Assert.Equal(original.RoutingRules[1], rehydrated.RoutingRules[1]);
    }

    [Fact]
    public void Load_MissingPipelineFields_ResolveToDefaults()
    {
        // Settings files written before the action-pipeline feature carry neither
        // ActionPipelines nor SignalBindings — the default initializers apply.
        var json = """{"SchemaVersion": 1, "Keyboard": "GO60"}""";

        var parsed = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.ActionPipelines);
        Assert.Empty(parsed.ActionPipelines);
        Assert.NotNull(parsed.SignalBindings);
        Assert.Empty(parsed.SignalBindings);
        Assert.NotNull(parsed.EventBindings);
        Assert.Empty(parsed.EventBindings);
    }

    [Fact]
    public void RoundTrip_PreservesActionPipelinesAndSignalBindings()
    {
        var original = new UserSettings
        {
            ActionPipelines = new List<ActionPipeline>
            {
                new("flash", new List<PipelineStep>
                {
                    new(PipelineStepKind.KeyboardLayer, LayerIndex: 1),
                    new(PipelineStepKind.KeyboardLayer, TargetDeviceKey: "1234:5678:bean",
                        LayerIndex: 2, LayerAction: PipelineLayerAction.Activate),
                    new(PipelineStepKind.Rgb, TargetDeviceKey: "1234:5678:bean",
                        Rgb: new ZmkHidProtocol.Capabilities.RgbSet(On: true, Hue: 120, Sat: 100, Val: 60)),
                    new(PipelineStepKind.Delay, DelayMs: 150),
                    new(PipelineStepKind.Pointing, TargetDeviceKey: "1234:5678:bean",
                        PointingActionByte: 0xEB, PointingValue: 1u),
                    new(PipelineStepKind.Launch, LaunchTarget: "/Applications/Firefox.app"),
                    new(PipelineStepKind.Shell, ShellCommand: "echo hi"),
                    new(PipelineStepKind.Pipeline, PipelineRef: "other"),
                }),
            },
            SignalBindings = new List<SignalBinding> { new(7, "1234:5678:bean", "flash") },
            EventBindings = new List<EventBinding> { new("ci:ok", "flash") },
        };

        var json = JsonSerializer.Serialize(original, Options);
        // The step kind is persisted as its enum name (own JsonStringEnumConverter),
        // not an int, so old/new files stay human-readable.
        Assert.Contains("KeyboardLayer", json);
        // The layer action serializes as its enum name too.
        Assert.Contains("Activate", json);

        var rehydrated = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(rehydrated);
        var p = Assert.Single(rehydrated!.ActionPipelines);
        Assert.Equal("flash", p.Name);
        Assert.Equal(8, p.Steps.Count);
        // PipelineStep is a value record (RgbSet is a record struct), so each step
        // round-trips by value — covers every field-carrying kind.
        for (var i = 0; i < p.Steps.Count; i++)
            Assert.Equal(original.ActionPipelines[0].Steps[i], p.Steps[i]);
        Assert.Equal(new SignalBinding(7, "1234:5678:bean", "flash"), Assert.Single(rehydrated.SignalBindings));
        Assert.Equal(new EventBinding("ci:ok", "flash"), Assert.Single(rehydrated.EventBindings));
    }

    [Fact]
    public void Load_PreSourceSignalBinding_DeserializesInert()
    {
        // Bindings written before source-device scoping carry only SignalId +
        // PipelineName. They must deserialize without throwing; the missing source
        // key resolves to null/empty (matches no live device → inert until repointed).
        var json = """
            {
                "SchemaVersion": 1,
                "SignalBindings": [{"SignalId": 7, "PipelineName": "flash"}]
            }
            """;

        var parsed = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(parsed);
        var binding = Assert.Single(parsed!.SignalBindings);
        Assert.Equal(7, binding.SignalId);
        Assert.Equal("flash", binding.PipelineName);
        Assert.True(string.IsNullOrEmpty(binding.SourceDeviceKey));
    }

    [Fact]
    public void RoundTrip_PreservesAppLayerBindings()
    {
        var original = new UserSettings
        {
            AppLayerBindings = new List<AppLayerBinding>
            {
                new("Code", AppLayerMoment.Enter, "flash"),
                new("", AppLayerMoment.Leave, "restore"),
                new("Notes", AppLayerMoment.Exit, "dim"),
                new("Notes", AppLayerMoment.Reenter, "bright"),
            },
        };

        var json = JsonSerializer.Serialize(original, Options);
        // The moment persists as its enum name (own JsonStringEnumConverter), not an int.
        Assert.Contains("Reenter", json);

        var rehydrated = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(rehydrated);
        Assert.Equal(4, rehydrated!.AppLayerBindings.Count);
        for (var i = 0; i < original.AppLayerBindings.Count; i++)
            Assert.Equal(original.AppLayerBindings[i], rehydrated.AppLayerBindings[i]);
    }

    [Fact]
    public void Load_MissingAppLayerBindings_ResolvesToEmpty()
    {
        var json = """{"SchemaVersion": 1, "Keyboard": "GO60"}""";

        var parsed = JsonSerializer.Deserialize<UserSettings>(json, Options);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.AppLayerBindings);
        Assert.Empty(parsed.AppLayerBindings);
    }

    [Fact]
    public void SettingsService_LoadFromMissingFile_ReturnsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lviz-settings-{Guid.NewGuid()}.json");
        try
        {
            var svc = new SettingsService(path);
            var loaded = svc.Load();

            Assert.NotNull(loaded);
            Assert.Equal(UserSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal("Corne", loaded.Keyboard);
            Assert.Empty(loaded.MouseLayer);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SettingsService_LoadFromFutureSchemaVersion_BacksUpAndReturnsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lviz-settings-{Guid.NewGuid()}.json");
        var backup = $"{path}.v999.bak";
        try
        {
            File.WriteAllText(path,
                """{"SchemaVersion": 999, "Keyboard": "Glove80"}""");
            var svc = new SettingsService(path);
            var loaded = svc.Load();

            Assert.Equal("Corne", loaded.Keyboard);  // default, not "Glove80"
            Assert.True(File.Exists(backup), "future-versioned settings file must be backed up");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(backup)) File.Delete(backup);
        }
    }

    [Fact]
    public void SettingsService_LoadFromCorruptJson_ReturnsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lviz-settings-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, "{ this is not valid json");
            var svc = new SettingsService(path);
            var loaded = svc.Load();

            Assert.Equal("Corne", loaded.Keyboard);
            Assert.Empty(loaded.MouseLayer);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SettingsService_SaveThenLoad_RoundTripsAllPostRefactorFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lviz-settings-{Guid.NewGuid()}.json");
        try
        {
            var svc = new SettingsService(path);
            var saved = new UserSettings
            {
                Keyboard = "Glove80",
                HotkeyKey = "F19",
                ColorTrayIconByActiveLayer = false,
                MouseLayer = new Dictionary<string, MouseLayerSettings>
                {
                    ["Glove80"] = new(Enabled: true, MouseLayerIndex: 5, IdleTimeoutMs: 800),
                },
            };
            svc.Save(saved);
            var loaded = svc.Load();

            Assert.Equal(saved.Keyboard, loaded.Keyboard);
            Assert.Equal(saved.HotkeyKey, loaded.HotkeyKey);
            Assert.Equal(saved.ColorTrayIconByActiveLayer, loaded.ColorTrayIconByActiveLayer);
            Assert.Equal(saved.MouseLayer["Glove80"], loaded.MouseLayer["Glove80"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
