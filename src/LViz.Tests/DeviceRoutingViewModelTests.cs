using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Settings;
using ZmkHidProtocol.Capabilities;
using ZmkHidProtocol.Protocol;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Covers the Device Routing settings VM: inventory + route table built from the
/// router snapshot, master-toggle persistence, route-rule edits translated to
/// the smallest <see cref="RoutingRule"/> set, and app-originated control
/// dispatch through <see cref="ICapabilityControl"/>.
/// </summary>
public class DeviceRoutingViewModelTests
{
    private const string DpiSet = "core.pointing.dpi.set";

    private sealed class FakeRouter : ICapabilityRouter
    {
        public List<RoutedDeviceInfo> DeviceList { get; } = new();
        public Dictionary<string, ICapabilityDevice> Lookup { get; } = new();
        public bool Available { get; set; }
        public int RefreshConfigCount { get; private set; }
        public int RescanCount { get; private set; }

        public IReadOnlyList<RoutedDeviceInfo> Devices => DeviceList;
        public bool IsRoutingAvailable => Available;
        public ICapabilityDevice? FindDevice(string stableKey) => Lookup.GetValueOrDefault(stableKey);
        public string? StableKeyFor(ICapabilityDevice device)
            => Lookup.FirstOrDefault(kv => ReferenceEquals(kv.Value, device)).Key;
        public event Action? RoutesChanged;
        public void Raise() => RoutesChanged?.Invoke();
        public void Start() { }
        public void Stop() { }
        public void RefreshConfig() => RefreshConfigCount++;
        public Task RescanAsync() { RescanCount++; return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class FakeControl : ICapabilityControl
    {
        public List<(string Kind, ICapabilityDevice Target, uint Value, byte ActionByte)> Calls { get; } = new();
        public List<(ICapabilityDevice Target, RgbSet Set)> RgbCalls { get; } = new();
        public ControlResult Result { get; set; } = new(ControlOutcome.Sent);

        public Task<ControlResult> SendPointingActionAsync(ICapabilityDevice target, byte actionByte, uint value, CancellationToken ct = default)
        { Calls.Add(("pointing", target, value, actionByte)); return Task.FromResult(Result); }
        public Task<ControlResult> SendRgbAsync(ICapabilityDevice target, RgbSet set, CancellationToken ct = default)
        { RgbCalls.Add((target, set)); return Task.FromResult(Result); }
        public Task<ControlResult> SetLayerBaseAsync(ICapabilityDevice target, byte layerIndex, CancellationToken ct = default)
        { Calls.Add(("setBase", target, layerIndex, 0)); return Task.FromResult(Result); }
        public Task<ControlResult> ActivateLayerAsync(ICapabilityDevice target, byte layerIndex, CancellationToken ct = default)
        { Calls.Add(("activate", target, layerIndex, 0)); return Task.FromResult(Result); }
        public Task<ControlResult> DeactivateLayerAsync(ICapabilityDevice target, byte layerIndex, CancellationToken ct = default)
        { Calls.Add(("deactivate", target, layerIndex, 0)); return Task.FromResult(Result); }
    }

    private static Capability Triggers(string id) => new(CapabilityRole.Triggers, CapabilityTier.Core, false, id);
    private static Capability Handles(string id) => new(CapabilityRole.Handles, CapabilityTier.Core, false, id);

    private static RoutedDeviceInfo Dev(string key, string name, bool isKeyboard, params Capability[] caps)
        => new(key, name, isKeyboard, new DeviceManifest(name, key, caps));

    private static (DeviceRoutingViewModel Vm, FakeRouter Router, FakeControl Control, InMemorySettingsService Settings) Build(
        UserSettings? seed = null, params RoutedDeviceInfo[] devices)
    {
        var router = new FakeRouter();
        router.DeviceList.AddRange(devices);
        var control = new FakeControl();
        var settings = new InMemorySettingsService { Current = seed ?? new UserSettings() };
        var vm = new DeviceRoutingViewModel(router, control, settings);
        return (vm, router, control, settings);
    }

    [Fact]
    public void Rebuild_PopulatesInventory_WithCapabilitySurface()
    {
        var (vm, _, _, _) = Build(
            devices: new[]
            {
                Dev("kb", "Corne", true, Triggers(DpiSet)),
                Dev("ball", "Bean", false, Handles(DpiSet)),
            });

        Assert.Equal(2, vm.Devices.Count);
        var kb = vm.Devices.First(d => d.StableKey == "kb");
        Assert.True(kb.IsKeyboard);
        Assert.Equal(DpiSet, kb.Triggers);
        Assert.Equal("—", kb.Handles);
        var ball = vm.Devices.First(d => d.StableKey == "ball");
        Assert.False(ball.IsKeyboard);
        Assert.Equal(DpiSet, ball.Handles);
        Assert.True(vm.HasDevices);
        Assert.False(vm.HasNoDevices);
    }

    [Fact]
    public void Opening_TriggersAFreshRescan_SoStaleManifestsRefresh()
    {
        // The constructed VM must kick a rescan so an already-connected device
        // whose manifest failed to read on a busy startup scan gets re-read,
        // rather than waiting for an unrelated hotplug.
        var (_, router, _, _) = Build();
        Assert.Equal(1, router.RescanCount);
    }

    [Fact]
    public void SingleCapableDevice_MarksTabActive_EvenWithoutRoutablePair()
    {
        // One device that speaks the protocol (answered a manifest) is enough to
        // "light up" the tab, before any second device makes routing possible.
        var (vm, _, _, _) = Build(
            devices: new[] { Dev("ball", "Bean", false, Handles("core.rgb.set")) });

        Assert.True(vm.HasCapableDevice);
        Assert.False(vm.HasNoCapableDevice);
        Assert.False(vm.IsRoutingAvailable); // no trigger→handler pair
    }

    [Fact]
    public void NoCapableDevice_WhenNoManifestPresent()
    {
        var noManifest = new RoutedDeviceInfo("x", "Mystery", false, null);
        var (vm, _, _, _) = Build(devices: new[] { noManifest });

        Assert.False(vm.HasCapableDevice);
        Assert.True(vm.HasNoCapableDevice);
    }

    [Fact]
    public void NoManifest_Device_RendersWithoutCapabilities()
    {
        var noManifest = new RoutedDeviceInfo("x", "Mystery", false, null);
        var (vm, _, _, _) = Build(devices: new[] { noManifest });

        var row = Assert.Single(vm.Devices);
        Assert.False(row.HasManifest);
        Assert.Equal("—", row.Triggers);
        Assert.Equal("—", row.Handles);
    }

    [Fact]
    public void RoutingEnabled_Persists_AndRefreshesRouterConfig()
    {
        var (vm, router, _, settings) = Build();
        Assert.False(vm.RoutingEnabled);

        vm.RoutingEnabled = true;

        Assert.True(settings.Current.DeviceRoutingEnabled);
        Assert.True(router.RefreshConfigCount >= 1);
    }

    [Fact]
    public void Routes_PairTriggerWithHandler_DefaultRouted()
    {
        var (vm, _, _, _) = Build(
            devices: new[]
            {
                Dev("kb", "Corne", true, Triggers(DpiSet)),
                Dev("ball", "Bean", false, Handles(DpiSet)),
            });

        var route = Assert.Single(vm.Routes);
        Assert.Equal(DpiSet, route.CapabilityId);
        Assert.Equal("kb", route.SourceKey);
        var handler = Assert.Single(route.Handlers);
        Assert.Equal("ball", handler.TargetKey);
        Assert.True(handler.Routed); // no rule = auto-routed
    }

    [Fact]
    public void Route_WithNoHandlerElsewhere_IsNotListed()
    {
        // Only a trigger, no device handles it → no route row.
        var (vm, _, _, _) = Build(
            devices: new[] { Dev("kb", "Corne", true, Triggers(DpiSet)) });

        Assert.Empty(vm.Routes);
        Assert.True(vm.HasNoRoutes);
    }

    [Fact]
    public void DisablingSingleHandler_PersistsDisableRule()
    {
        var (vm, router, _, settings) = Build(
            devices: new[]
            {
                Dev("kb", "Corne", true, Triggers(DpiSet)),
                Dev("ball", "Bean", false, Handles(DpiSet)),
            });

        vm.Routes[0].Handlers[0].Routed = false;

        var rule = Assert.Single(settings.Current.RoutingRules);
        Assert.Equal(DpiSet, rule.CapabilityId);
        Assert.Equal("kb", rule.SourceDeviceKey);
        Assert.Equal("ball", rule.TargetDeviceKey);
        Assert.False(rule.Enabled);
        Assert.True(router.RefreshConfigCount >= 1);
    }

    [Fact]
    public void ExistingDisableRule_SeedsHandlerUnchecked()
    {
        var seed = new UserSettings
        {
            RoutingRules = new List<RoutingRule> { new(DpiSet, "kb", "ball", Enabled: false) },
        };
        var (vm, _, _, _) = Build(seed,
            Dev("kb", "Corne", true, Triggers(DpiSet)),
            Dev("ball", "Bean", false, Handles(DpiSet)));

        Assert.False(vm.Routes[0].Handlers[0].Routed);
    }

    [Fact]
    public void DisablingOneOfTwoHandlers_PersistsWhitelistForTheOther()
    {
        var (vm, _, _, settings) = Build(
            devices: new[]
            {
                Dev("kb", "Corne", true, Triggers(DpiSet)),
                Dev("a", "A", false, Handles(DpiSet)),
                Dev("b", "B", false, Handles(DpiSet)),
            });

        var route = Assert.Single(vm.Routes);
        Assert.Equal(2, route.Handlers.Count);

        // Uncheck B → a strict subset {A} remains → whitelist rule for A only.
        route.Handlers.First(h => h.TargetKey == "b").Routed = false;

        var rule = Assert.Single(settings.Current.RoutingRules);
        Assert.Equal("a", rule.TargetDeviceKey);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void AllHandlersOn_PersistsNoRules()
    {
        // Start from a disable rule, then re-check → rules collapse back to empty.
        var seed = new UserSettings
        {
            RoutingRules = new List<RoutingRule> { new(DpiSet, "kb", "ball", Enabled: false) },
        };
        var (vm, _, _, settings) = Build(seed,
            Dev("kb", "Corne", true, Triggers(DpiSet)),
            Dev("ball", "Bean", false, Handles(DpiSet)));

        vm.Routes[0].Handlers[0].Routed = true;

        Assert.Empty(settings.Current.RoutingRules);
    }

    [Fact]
    public void PersistRoutes_PreservesRulesForUndisplayedSources()
    {
        // A rule for a source/capability not in the current inventory must survive
        // an edit to a displayed route.
        var seed = new UserSettings
        {
            RoutingRules = new List<RoutingRule> { new("other.cap", "ghost", "target", Enabled: false) },
        };
        var (vm, _, _, settings) = Build(seed,
            Dev("kb", "Corne", true, Triggers(DpiSet)),
            Dev("ball", "Bean", false, Handles(DpiSet)));

        vm.Routes[0].Handlers[0].Routed = false; // adds a kb→ball disable rule

        Assert.Contains(settings.Current.RoutingRules, r => r.SourceDeviceKey == "ghost");
        Assert.Contains(settings.Current.RoutingRules, r => r.SourceDeviceKey == "kb");
    }

    [Fact]
    public void NoTarget_ShowsNoControls()
    {
        var (vm, _, _, _) = Build(
            devices: new[] { Dev("ball", "Bean", false, Handles(DpiSet)) });

        Assert.Null(vm.ControlTarget);
        Assert.True(vm.NoTargetSelected);
        Assert.Empty(vm.PointingControls);
        Assert.False(vm.HasLayerControls);
        Assert.False(vm.ShowControlPlaceholder);
    }

    [Fact]
    public void PointingTarget_BuildsRowsForHandledActions_IncludingSnipe()
    {
        var (vm, _, _, _) = Build(
            devices: new[]
            {
                Dev("ball", "Bean", false,
                    Handles("core.pointing.dpi.set"),
                    Handles("core.pointing.snipe.set")),
            });

        vm.ControlTarget = vm.Devices.First();

        Assert.Equal(2, vm.PointingControls.Count);
        Assert.Contains(vm.PointingControls, r => r.CapabilityId == "core.pointing.dpi.set");
        Assert.Contains(vm.PointingControls, r => r.CapabilityId == "core.pointing.snipe.set");
        Assert.False(vm.HasLayerControls);
        Assert.False(vm.ShowControlPlaceholder);
    }

    [Fact]
    public void PointingRows_ClassifyToggleVsNumericFromCatalog()
    {
        var (vm, _, _, _) = Build(
            devices: new[]
            {
                Dev("ball", "Bean", false,
                    Handles("core.pointing.dpi.set"),
                    Handles("core.pointing.dpi.setIndex"),
                    Handles("core.pointing.dragScroll.set"),
                    Handles("core.pointing.snipe.set")),
            });

        vm.ControlTarget = vm.Devices.First();

        // snipe + drag-scroll are on/off toggles; DPI + DPI index are numeric.
        Assert.True(vm.PointingControls.Single(r => r.CapabilityId == "core.pointing.snipe.set").IsToggle);
        Assert.True(vm.PointingControls.Single(r => r.CapabilityId == "core.pointing.dragScroll.set").IsToggle);
        Assert.True(vm.PointingControls.Single(r => r.CapabilityId == "core.pointing.dpi.set").IsNumeric);
        Assert.True(vm.PointingControls.Single(r => r.CapabilityId == "core.pointing.dpi.setIndex").IsNumeric);
    }

    [Theory]
    [InlineData(true, 1u)]
    [InlineData(false, 0u)]
    public async Task ToggleRow_Send_DispatchesOneOrZeroFromCheckbox(bool isOn, uint expected)
    {
        var device = new FakeDevice { DisplayName = "Bean" };
        var (vm, router, control, _) = Build(
            devices: new[] { Dev("ball", "Bean", false, Handles("core.pointing.snipe.set")) });
        router.Lookup["ball"] = device;

        vm.ControlTarget = vm.Devices.First();
        var snipe = vm.PointingControls.Single(r => r.CapabilityId == "core.pointing.snipe.set");
        snipe.IsOn = isOn;
        await snipe.SendCommand.ExecuteAsync(null);

        var call = Assert.Single(control.Calls);
        Assert.Equal("pointing", call.Kind);
        Assert.Same(device, call.Target);
        Assert.Equal(HidConstants.PointingAction.SnipeSet, call.ActionByte);
        Assert.Equal(expected, call.Value);
    }

    [Fact]
    public async Task NumericRow_Send_DispatchesActionByteAndValue()
    {
        var device = new FakeDevice { DisplayName = "Bean" };
        var (vm, router, control, _) = Build(
            devices: new[] { Dev("ball", "Bean", false, Handles("core.pointing.dpi.set")) });
        router.Lookup["ball"] = device;

        vm.ControlTarget = vm.Devices.First();
        var dpi = vm.PointingControls.Single(r => r.CapabilityId == "core.pointing.dpi.set");
        dpi.Value = 1600;
        await dpi.SendCommand.ExecuteAsync(null);

        var call = Assert.Single(control.Calls);
        Assert.Equal("pointing", call.Kind);
        Assert.Same(device, call.Target);
        Assert.Equal(HidConstants.PointingAction.DpiSet, call.ActionByte);
        Assert.Equal(1600u, call.Value);
    }

    [Fact]
    public void LayerTarget_ExposesOnlyHandledLayerButtons()
    {
        // Handles setBase only — activate / deactivate stay hidden, no pointing rows.
        var (vm, _, _, _) = Build(
            devices: new[] { Dev("kb", "Corne", true, Handles("core.layer.setBase")) });

        vm.ControlTarget = vm.Devices.First();

        Assert.True(vm.HasLayerControls);
        Assert.True(vm.CanSetBase);
        Assert.False(vm.CanActivate);
        Assert.False(vm.CanDeactivate);
        Assert.Empty(vm.PointingControls);
    }

    [Fact]
    public async Task LayerControls_DispatchSetBaseActivateDeactivate()
    {
        var device = new FakeDevice { DisplayName = "Corne" };
        var (vm, router, control, _) = Build(
            devices: new[]
            {
                Dev("kb", "Corne", true,
                    Handles("core.layer.setBase"),
                    Handles("core.layer.activate"),
                    Handles("core.layer.deactivate")),
            });
        router.Lookup["kb"] = device;

        vm.ControlTarget = vm.Devices.First();
        vm.ControlLayerIndex = 3;

        await vm.SetBaseLayerCommand.ExecuteAsync(null);
        await vm.ActivateLayerCommand.ExecuteAsync(null);
        await vm.DeactivateLayerCommand.ExecuteAsync(null);

        Assert.Collection(control.Calls,
            c => { Assert.Equal("setBase", c.Kind); Assert.Equal(3u, c.Value); },
            c => Assert.Equal("activate", c.Kind),
            c => Assert.Equal("deactivate", c.Kind));
    }

    [Fact]
    public void TargetWithNoControllableActions_ShowsPlaceholder()
    {
        // A handled capability LViz can't originate (not pointing / RGB / layer).
        var (vm, _, _, _) = Build(
            devices: new[] { Dev("x", "Mystery", false, Handles("core.capsWord.changed")) });

        vm.ControlTarget = vm.Devices.First();

        Assert.False(vm.HasAnyControls);
        Assert.True(vm.ShowControlPlaceholder);
    }

    [Fact]
    public void RgbTarget_BuildsRgbControl_AndCountsAsAControl()
    {
        var (vm, _, _, _) = Build(
            devices: new[] { Dev("kb", "Corne", true, Handles("core.rgb.set")) });

        vm.ControlTarget = vm.Devices.First();

        Assert.True(vm.HasRgbControl);
        Assert.NotNull(vm.RgbControl);
        Assert.True(vm.HasAnyControls);
        Assert.False(vm.ShowControlPlaceholder);
        Assert.Empty(vm.PointingControls);
        Assert.False(vm.HasLayerControls);
    }

    [Fact]
    public void RgbControl_ClearedWhenTargetDoesNotHandleRgb()
    {
        var (vm, _, _, _) = Build(
            devices: new[]
            {
                Dev("kb", "Corne", true, Handles("core.rgb.set")),
                Dev("ball", "Bean", false, Handles("core.pointing.dpi.set")),
            });

        vm.ControlTarget = vm.Devices.First(d => d.StableKey == "kb");
        Assert.True(vm.HasRgbControl);

        vm.ControlTarget = vm.Devices.First(d => d.StableKey == "ball");
        Assert.False(vm.HasRgbControl);
        Assert.Null(vm.RgbControl);
    }

    [Fact]
    public async Task RgbControl_Send_DispatchesOnlyIncludedFields()
    {
        var device = new FakeDevice { DisplayName = "Corne" };
        var (vm, router, control, _) = Build(
            devices: new[] { Dev("kb", "Corne", true, Handles("core.rgb.set")) });
        router.Lookup["kb"] = device;

        vm.ControlTarget = vm.Devices.First();
        var rgb = vm.RgbControl!;

        // Include only on; leave colour + brightness + effect out → no hue/sat/val.
        rgb.IncludeOn = true; rgb.On = true;
        rgb.IncludeColor = false;
        rgb.IncludeBrightness = false;
        rgb.IncludeEffect = false;
        await rgb.SendCommand.ExecuteAsync(null);

        var (target, set) = Assert.Single(control.RgbCalls);
        Assert.Same(device, target);
        Assert.Equal(true, set.On);
        Assert.Null(set.Hue);
        Assert.Null(set.Sat);
        Assert.Null(set.Val);
        Assert.Null(set.Effect);
    }

    [Fact]
    public async Task RgbControl_Send_TakesHueSatFromColor_AndBrightnessFromSlider()
    {
        var device = new FakeDevice { DisplayName = "Corne" };
        var (vm, router, control, _) = Build(
            devices: new[] { Dev("kb", "Corne", true, Handles("core.rgb.set")) });
        router.Lookup["kb"] = device;

        vm.ControlTarget = vm.Devices.First();
        var rgb = vm.RgbControl!;
        rgb.IncludeOn = true; rgb.On = false;
        rgb.IncludeColor = true;
        rgb.Color = Avalonia.Media.Color.FromRgb(0, 255, 0); // pure green → H 120, S 100% (V ignored)
        rgb.IncludeBrightness = true; rgb.Brightness = 50;    // brightness is the slider, not the colour's value
        rgb.IncludeEffect = true; rgb.Effect = 2;
        await rgb.SendCommand.ExecuteAsync(null);

        var (_, set) = Assert.Single(control.RgbCalls);
        Assert.Equal(false, set.On);
        Assert.Equal((ushort)120, set.Hue);
        Assert.Equal((byte)100, set.Sat);
        Assert.Equal((byte)50, set.Val); // from the slider, decoupled from the colour
        Assert.Equal((byte)2, set.Effect);
    }

    [Fact]
    public async Task RgbControl_NothingIncluded_SendsEmptyMaskQuery()
    {
        var device = new FakeDevice { DisplayName = "Corne" };
        var (vm, router, control, _) = Build(
            devices: new[] { Dev("kb", "Corne", true, Handles("core.rgb.set")) });
        router.Lookup["kb"] = device;

        vm.ControlTarget = vm.Devices.First();
        var rgb = vm.RgbControl!;
        rgb.IncludeOn = false;
        rgb.IncludeColor = false;
        rgb.IncludeBrightness = false;
        rgb.IncludeEffect = false;
        await rgb.SendCommand.ExecuteAsync(null);

        var (_, set) = Assert.Single(control.RgbCalls);
        Assert.Equal(new RgbSet(), set); // all-null → empty-mask state query
    }

    [Fact]
    public async Task LayerControl_NoTarget_SetsStatus_DoesNotCallControl()
    {
        var (vm, _, control, _) = Build(
            devices: new[] { Dev("kb", "Corne", true, Handles("core.layer.setBase")) });

        await vm.SetBaseLayerCommand.ExecuteAsync(null);

        Assert.Empty(control.Calls);
        Assert.NotEqual("", vm.ControlStatus);
    }

    [Fact]
    public void ControlTargetSelection_SurvivesRebuild()
    {
        var (vm, router, _, _) = Build(
            devices: new[]
            {
                Dev("kb", "Corne", true, Triggers(DpiSet)),
                Dev("ball", "Bean", false, Handles(DpiSet)),
            });
        vm.ControlTarget = vm.Devices.First(d => d.StableKey == "ball");

        vm.Rebuild(); // simulates a RoutesChanged-driven refresh

        Assert.NotNull(vm.ControlTarget);
        Assert.Equal("ball", vm.ControlTarget!.StableKey);
    }
}
