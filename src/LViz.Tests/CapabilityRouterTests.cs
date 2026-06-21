using LViz.App.Services;
using LViz.Core.Settings;
using ZmkHidProtocol.Capabilities;
using ZmkHidProtocol.Protocol;
using Xunit;

namespace LViz.Tests;

public class CapabilityRouterTests
{
    private const string DpiSet = "core.pointing.dpi.set";
    private const string SnipeSet = "core.pointing.snipe.set";

    private sealed class FakeDeviceBus : ICapabilityDeviceBus
    {
        public List<ICapabilityDevice> Items { get; } = new();
        public IReadOnlyList<ICapabilityDevice> Devices => Items.ToArray();
        public event Action<IReadOnlyList<ICapabilityDevice>>? DevicesChanged;
        public void RaiseChanged() => DevicesChanged?.Invoke(Devices);
    }

    private static Capability Handles(string id) => new(CapabilityRole.Handles, CapabilityTier.Core, false, id);
    private static Capability Triggers(string id) => new(CapabilityRole.Triggers, CapabilityTier.Core, false, id);
    private static DeviceManifest Manifest(string configId, string name, params Capability[] caps) => new(name, configId, caps);

    private static byte[] DpiReport(uint value)
        => new[] { HidConstants.PointingAction.DpiSet }
            .Concat(BitConverter.GetBytes(value)).ToArray();

    private static CapabilityRouter NewRouter(
        ICapabilityDevice keyboard,
        FakeDeviceBus bus,
        Dictionary<ICapabilityDevice, DeviceManifest?> manifests,
        UserSettings settings) =>
        new(keyboard, bus, new InMemorySettingsService { Current = settings },
            manifestTimeout: TimeSpan.FromMilliseconds(50),
            manifestReader: (d, _) => Task.FromResult(manifests.TryGetValue(d, out var m) ? m : null));

    private static Task WaitForRoutesChanged(ICapabilityRouter router, int timeoutMs = 2000)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler() { router.RoutesChanged -= Handler; tcs.TrySetResult(); }
        router.RoutesChanged += Handler;
        return tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    [Fact]
    public async Task Scan_PairsTriggerAndHandler_DerivesKeys_MarksAvailable()
    {
        var kb = new FakeDevice { DisplayName = "Corne", TransportKey = "keyboard" };
        var ball = new FakeDevice { DisplayName = "Bean", TransportKey = "1234:5678:/dev/p" };
        var bus = new FakeDeviceBus { Items = { ball } };
        var manifests = new Dictionary<ICapabilityDevice, DeviceManifest?>
        {
            [kb] = Manifest("kb-guid", "Corne", Triggers(DpiSet)),
            [ball] = Manifest("ball-guid", "Bean", Handles(DpiSet)),
        };
        using var router = NewRouter(kb, bus, manifests, new UserSettings { DeviceRoutingEnabled = true });

        await router.RescanAsync();

        Assert.True(router.IsRoutingAvailable);
        Assert.Equal(2, router.Devices.Count);
        Assert.Contains(router.Devices, d => d.StableKey == "kb-guid" && d.IsKeyboard);
        Assert.Contains(router.Devices, d => d.StableKey == "ball-guid" && !d.IsKeyboard);
    }

    [Fact]
    public async Task Scan_NoTriggerHandlerMatch_NotAvailable()
    {
        // Two devices, but the handler's capability is never triggered by anyone.
        var kb = new FakeDevice { TransportKey = "keyboard" };
        var ball = new FakeDevice { TransportKey = "1234:5678:/dev/p" };
        var bus = new FakeDeviceBus { Items = { ball } };
        var manifests = new Dictionary<ICapabilityDevice, DeviceManifest?>
        {
            [kb] = Manifest("kb-guid", "Corne", Triggers(SnipeSet)),
            [ball] = Manifest("ball-guid", "Bean", Handles(DpiSet)),
        };
        using var router = NewRouter(kb, bus, manifests, new UserSettings { DeviceRoutingEnabled = true });

        await router.RescanAsync();

        Assert.False(router.IsRoutingAvailable);
    }

    [Fact]
    public async Task Forward_RoutesTriggerToHandler_Verbatim()
    {
        var kb = new FakeDevice { TransportKey = "keyboard" };
        var ball = new FakeDevice { TransportKey = "1234:5678:/dev/p" };
        var bus = new FakeDeviceBus { Items = { ball } };
        var manifests = new Dictionary<ICapabilityDevice, DeviceManifest?>
        {
            [kb] = Manifest("kb-guid", "Corne", Triggers(DpiSet)),
            [ball] = Manifest("ball-guid", "Bean", Handles(DpiSet)),
        };
        using var router = NewRouter(kb, bus, manifests, new UserSettings { DeviceRoutingEnabled = true });
        await router.RescanAsync();

        var report = DpiReport(800);
        kb.RaiseReport(report);

        Assert.Single(ball.Sent);
        Assert.Equal(report, ball.Sent[0]);
        Assert.Empty(kb.Sent); // originator never receives its own trigger
    }

    [Fact]
    public async Task Forward_ExcludesOriginator_EvenWhenItAlsoHandles()
    {
        // kb both triggers and handles; ball handles. kb's emit must reach ball only.
        var kb = new FakeDevice { TransportKey = "keyboard" };
        var ball = new FakeDevice { TransportKey = "1234:5678:/dev/p" };
        var bus = new FakeDeviceBus { Items = { ball } };
        var manifests = new Dictionary<ICapabilityDevice, DeviceManifest?>
        {
            [kb] = Manifest("kb-guid", "Corne", Triggers(DpiSet), Handles(DpiSet)),
            [ball] = Manifest("ball-guid", "Bean", Handles(DpiSet)),
        };
        using var router = NewRouter(kb, bus, manifests, new UserSettings { DeviceRoutingEnabled = true });
        await router.RescanAsync();

        kb.RaiseReport(DpiReport(1200));

        Assert.Single(ball.Sent);
        Assert.Empty(kb.Sent);
    }

    [Fact]
    public async Task Forward_DisabledRule_SuppressesThatTarget()
    {
        var kb = new FakeDevice { TransportKey = "keyboard" };
        var ball = new FakeDevice { TransportKey = "1234:5678:/dev/p" };
        var bus = new FakeDeviceBus { Items = { ball } };
        var manifests = new Dictionary<ICapabilityDevice, DeviceManifest?>
        {
            [kb] = Manifest("kb-guid", "Corne", Triggers(DpiSet)),
            [ball] = Manifest("ball-guid", "Bean", Handles(DpiSet)),
        };
        var settings = new UserSettings
        {
            DeviceRoutingEnabled = true,
            RoutingRules = new List<RoutingRule> { new(DpiSet, "kb-guid", "ball-guid", Enabled: false) },
        };
        using var router = NewRouter(kb, bus, manifests, settings);
        await router.RescanAsync();

        kb.RaiseReport(DpiReport(800));

        Assert.Empty(ball.Sent);
    }

    [Fact]
    public async Task Forward_OverrideRule_PicksAmongMultipleHandlers()
    {
        var kb = new FakeDevice { TransportKey = "keyboard" };
        var ballA = new FakeDevice { DisplayName = "A", TransportKey = "1234:5678:/dev/a" };
        var ballB = new FakeDevice { DisplayName = "B", TransportKey = "1234:9999:/dev/b" };
        var bus = new FakeDeviceBus { Items = { ballA, ballB } };
        var manifests = new Dictionary<ICapabilityDevice, DeviceManifest?>
        {
            [kb] = Manifest("kb-guid", "Corne", Triggers(DpiSet)),
            [ballA] = Manifest("a-guid", "A", Handles(DpiSet)),
            [ballB] = Manifest("b-guid", "B", Handles(DpiSet)),
        };
        var settings = new UserSettings
        {
            DeviceRoutingEnabled = true,
            RoutingRules = new List<RoutingRule> { new(DpiSet, "kb-guid", "a-guid", Enabled: true) },
        };
        using var router = NewRouter(kb, bus, manifests, settings);
        await router.RescanAsync();

        kb.RaiseReport(DpiReport(800));

        Assert.Single(ballA.Sent);
        Assert.Empty(ballB.Sent);
    }

    [Fact]
    public async Task Forward_NoRule_BroadcastsToAllHandlers()
    {
        var kb = new FakeDevice { TransportKey = "keyboard" };
        var ballA = new FakeDevice { TransportKey = "1234:5678:/dev/a" };
        var ballB = new FakeDevice { TransportKey = "1234:9999:/dev/b" };
        var bus = new FakeDeviceBus { Items = { ballA, ballB } };
        var manifests = new Dictionary<ICapabilityDevice, DeviceManifest?>
        {
            [kb] = Manifest("kb-guid", "Corne", Triggers(DpiSet)),
            [ballA] = Manifest("a-guid", "A", Handles(DpiSet)),
            [ballB] = Manifest("b-guid", "B", Handles(DpiSet)),
        };
        using var router = NewRouter(kb, bus, manifests, new UserSettings { DeviceRoutingEnabled = true });
        await router.RescanAsync();

        kb.RaiseReport(DpiReport(800));

        Assert.Single(ballA.Sent);
        Assert.Single(ballB.Sent);
    }

    [Fact]
    public async Task Forward_UnknownActionByte_Ignored()
    {
        var kb = new FakeDevice { TransportKey = "keyboard" };
        var ball = new FakeDevice { TransportKey = "1234:5678:/dev/p" };
        var bus = new FakeDeviceBus { Items = { ball } };
        var manifests = new Dictionary<ICapabilityDevice, DeviceManifest?>
        {
            [kb] = Manifest("kb-guid", "Corne", Triggers(DpiSet)),
            [ball] = Manifest("ball-guid", "Bean", Handles(DpiSet)),
        };
        using var router = NewRouter(kb, bus, manifests, new UserSettings { DeviceRoutingEnabled = true });
        await router.RescanAsync();

        kb.RaiseReport(new byte[] { 0x10, 0x01, 0x02, 0x03, 0x04 }); // not a routable action

        Assert.Empty(ball.Sent);
    }

    [Fact]
    public async Task Forward_WhenRoutingDisabled_DoesNothing()
    {
        var kb = new FakeDevice { TransportKey = "keyboard" };
        var ball = new FakeDevice { TransportKey = "1234:5678:/dev/p" };
        var bus = new FakeDeviceBus { Items = { ball } };
        var manifests = new Dictionary<ICapabilityDevice, DeviceManifest?>
        {
            [kb] = Manifest("kb-guid", "Corne", Triggers(DpiSet)),
            [ball] = Manifest("ball-guid", "Bean", Handles(DpiSet)),
        };
        using var router = NewRouter(kb, bus, manifests, new UserSettings { DeviceRoutingEnabled = false });
        await router.RescanAsync();

        kb.RaiseReport(DpiReport(800));

        Assert.Empty(ball.Sent);
    }

    [Fact]
    public async Task Rescan_RebuildsTable_WhenDeviceSetChanges()
    {
        var kb = new FakeDevice { TransportKey = "keyboard" };
        var ball = new FakeDevice { TransportKey = "1234:5678:/dev/p" };
        var bus = new FakeDeviceBus();
        var manifests = new Dictionary<ICapabilityDevice, DeviceManifest?>
        {
            [kb] = Manifest("kb-guid", "Corne", Triggers(DpiSet)),
            [ball] = Manifest("ball-guid", "Bean", Handles(DpiSet)),
        };
        using var router = NewRouter(kb, bus, manifests, new UserSettings { DeviceRoutingEnabled = true });

        await router.RescanAsync();
        Assert.Single(router.Devices);          // keyboard only
        Assert.False(router.IsRoutingAvailable);

        bus.Items.Add(ball);
        await router.RescanAsync();

        Assert.Equal(2, router.Devices.Count);
        Assert.True(router.IsRoutingAvailable);
        kb.RaiseReport(DpiReport(800));
        Assert.Single(ball.Sent);               // forwarding picks up the new handler
    }

    [Fact]
    public async Task FindDevice_ResolvesByStableKey_NullForUnknown()
    {
        var kb = new FakeDevice { TransportKey = "keyboard" };
        var ball = new FakeDevice { TransportKey = "1234:5678:/dev/p" };
        var bus = new FakeDeviceBus { Items = { ball } };
        var manifests = new Dictionary<ICapabilityDevice, DeviceManifest?>
        {
            [kb] = Manifest("kb-guid", "Corne", Triggers(DpiSet)),
            [ball] = Manifest("ball-guid", "Bean", Handles(DpiSet)),
        };
        using var router = NewRouter(kb, bus, manifests, new UserSettings { DeviceRoutingEnabled = true });
        await router.RescanAsync();

        Assert.Same(kb, router.FindDevice("kb-guid"));
        Assert.Same(ball, router.FindDevice("ball-guid"));
        Assert.Null(router.FindDevice("nope"));
    }

    [Fact]
    public async Task Scan_FailedManifestRead_SelfHealsOnRetry()
    {
        // A device whose manifest read loses the startup HID race comes back null,
        // so it isn't keyed by its manifest key and a saved target won't resolve.
        // The bounded retry must heal it without an external trigger.
        var kb = new FakeDevice { TransportKey = "keyboard" };
        var ball = new FakeDevice { DisplayName = "Bean", TransportKey = "1234:5678:/dev/p" };
        var bus = new FakeDeviceBus { Items = { ball } };
        var kbManifest = Manifest("kb-guid", "Corne", Triggers(DpiSet));
        var ballManifest = Manifest("ball-guid", "Bean", Handles(DpiSet));
        var ballReads = 0;
        using var router = new CapabilityRouter(
            kb, bus, new InMemorySettingsService { Current = new UserSettings { DeviceRoutingEnabled = true } },
            manifestTimeout: TimeSpan.FromMilliseconds(50),
            manifestReader: (d, _) => ReferenceEquals(d, kb)
                ? Task.FromResult<DeviceManifest?>(kbManifest)
                // First ball read fails; subsequent reads succeed.
                : Task.FromResult<DeviceManifest?>(Interlocked.Increment(ref ballReads) == 1 ? null : ballManifest));

        await router.RescanAsync();                         // ball missing → retry scheduled
        Assert.Null(router.FindDevice("ball-guid"));

        var healed = WaitForRoutesChanged(router);          // the retry's re-scan
        await healed;

        Assert.Same(ball, router.FindDevice("ball-guid"));
        Assert.True(router.IsRoutingAvailable);
    }

    [Fact]
    public async Task Start_SubscribesHotPlug_RescansOnDevicesChanged()
    {
        var kb = new FakeDevice { TransportKey = "keyboard" };
        var ball = new FakeDevice { TransportKey = "1234:5678:/dev/p" };
        var bus = new FakeDeviceBus();
        var manifests = new Dictionary<ICapabilityDevice, DeviceManifest?>
        {
            [kb] = Manifest("kb-guid", "Corne", Triggers(DpiSet)),
            [ball] = Manifest("ball-guid", "Bean", Handles(DpiSet)),
        };
        using var router = NewRouter(kb, bus, manifests, new UserSettings { DeviceRoutingEnabled = true });

        var firstScan = WaitForRoutesChanged(router);
        router.Start();
        await firstScan;
        Assert.Single(router.Devices);

        bus.Items.Add(ball);
        var hotPlugScan = WaitForRoutesChanged(router);
        bus.RaiseChanged();
        await hotPlugScan;

        Assert.Equal(2, router.Devices.Count);
    }
}
