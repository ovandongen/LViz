using LViz.App.Services;
using LViz.Core.Layout;
using LViz.Core.Settings;
using ZmkHidProtocol.Protocol;
using ZmkHidProtocol.Transport;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// Pins the device stack's start / stop / dispose order with recording doubles.
/// The device bus is a real, never-started <see cref="RawHidDeviceBus"/> (sealed,
/// so its slot in the order is the one thing these tests cannot record).
/// </summary>
public class DeviceStackTests
{
    private sealed class Log
    {
        public List<string> Entries { get; } = new();
        public void Add(string entry) => Entries.Add(entry);
    }

    private sealed class RecHid(Log log) : IHidPipeline
    {
#pragma warning disable CS0067
        public event Action<int>? ActiveLayerChanged;
        public event Action<int, bool>? KeyPositionEvent;
        public event Action? ConnectionChanged;
        public event Action<ReadOnlyMemory<byte>>? ReportReceived;
#pragma warning restore CS0067
        public bool IsConnected => false;
        public string? OpenDevicePath => null;
        public IDeviceMatcher KeyboardMatcher { get; } = new KeyboardProfileMatcherStub();
        public string SourceLabel => "";
        public int CurrentLayer => 0;
        public bool IsLastChangeAppControlled => false;
        public void Start() => log.Add("hid.start");
        public void Stop() => log.Add("hid.stop");
        public void SetActiveProfile(IKeyboardProfile profile) => log.Add("hid.profile");
        public void PushLayer(int index) { }
        public void PushLayerSync(int index, TimeSpan timeout) { }
        public Task<DeviceInfo?> QueryDeviceInfoAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult<DeviceInfo?>(null);
        public Task<string?> QueryConfigIdAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult<string?>(null);
        public ValueTask SendReportAsync(ReadOnlyMemory<byte> report, CancellationToken ct) => ValueTask.CompletedTask;
        public void Dispose() => log.Add("hid.dispose");

        private sealed class KeyboardProfileMatcherStub : IDeviceMatcher
        {
            public bool Matches(int vendorId, int productId, string? productName) => false;
            public bool MatchesName(string? productName) => false;
        }
    }

    private sealed class RecPush(Log log) : ILayerPushCoordinator
    {
        public AutoSwitchEngine AutoSwitch => throw new NotSupportedException();
        public IMouseLayerEngine? MouseLayer => null;
#pragma warning disable CS0067
        public event Action<int, bool>? KeyPositionForUi;
#pragma warning restore CS0067
        public void SetTransparencyPredicate(Func<int, int, LViz.Core.Models.TransparentBindingKind?>? classifier) { }
        public void SetActiveProfile(IKeyboardProfile profile) { }
        public void OnHidConnectionChanged() { }
        public void RevertMouseLayerForShutdown(TimeSpan timeout) => log.Add("push.revertMouse");
        public void ForceBaseLayerOnExit(TimeSpan timeout) => log.Add("push.forceBase");
        public void Shutdown() => log.Add("push.shutdown");
        public void Dispose() => log.Add("push.dispose");
    }

    private sealed class RecRouter(Log log) : ICapabilityRouter
    {
        public IReadOnlyList<RoutedDeviceInfo> Devices => [];
        public bool IsRoutingAvailable => false;
        public ZmkHidProtocol.Capabilities.ICapabilityDevice? FindDevice(string stableKey) => null;
        public string? StableKeyFor(ZmkHidProtocol.Capabilities.ICapabilityDevice device) => null;
#pragma warning disable CS0067
        public event Action? RoutesChanged;
#pragma warning restore CS0067
        public void Start() => log.Add("router.start");
        public void Stop() => log.Add("router.stop");
        public void RefreshConfig() { }
        public Task RescanAsync() { log.Add("router.rescan"); return Task.CompletedTask; }
        public void Dispose() => log.Add("router.dispose");
    }

    private sealed class RecSignals(Log log) : ISignalDispatcher
    {
        public void Start() => log.Add("signals.start");
        public void Stop() => log.Add("signals.stop");
        public void Dispose() => log.Add("signals.dispose");
    }

    private sealed class RecDisposable(Log log, string name) : IDisposable
    {
        public void Dispose() => log.Add(name);
    }

    private sealed class RecIpc(Log log) : IIpcHost
    {
        public void Start() => log.Add("ipc.start");
        public void Dispose() => log.Add("ipc.dispose");
    }

    private sealed class NoPipelines : IPipelineRunner
    {
        public Task RunAsync(ActionPipeline pipeline, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static (DeviceStack Stack, Log Log) Build()
    {
        var log = new Log();
        var stack = new DeviceStack(
            new RecHid(log), new RawHidDeviceBus(), new RecRouter(log), new NoPipelines(),
            new RecSignals(log), new RecIpc(log), new RecPush(log),
            new RecDisposable(log, "appLayer.dispose"));
        return (stack, log);
    }

    [Fact]
    public void Start_StartsHidThenRouterThenSignals()
    {
        var (stack, log) = Build();
        stack.Start();
        Assert.Equal(["hid.start", "router.start", "signals.start"], log.Entries);
    }

    [Fact]
    public void Ipc_OpensOnlyOnStartIpc_NeverWithLiveTracking()
    {
        var (stack, log) = Build();

        Assert.Empty(log.Entries); // building the stack opens nothing
        stack.Start();
        Assert.DoesNotContain("ipc.start", log.Entries);

        stack.StartIpc();
        Assert.Equal("ipc.start", log.Entries[^1]);
    }

    [Fact]
    public void Stop_AppExit_ForcesBaseLayerBeforeAnythingStops()
    {
        var (stack, log) = Build();
        stack.Stop(isAppExit: true);
        Assert.Equal(["push.forceBase", "signals.stop", "router.stop", "hid.stop"], log.Entries);
    }

    [Fact]
    public void Stop_ToggleOff_RevertsMouseLayerNotBaseLayer()
    {
        var (stack, log) = Build();
        stack.Stop(isAppExit: false);
        Assert.Equal(["push.revertMouse", "signals.stop", "router.stop", "hid.stop"], log.Entries);
    }

    [Fact]
    public void Dispose_ClosesIpcThenDispatchersThenPushThenSignalsRouterHid()
    {
        var (stack, log) = Build();
        stack.Dispose();
        Assert.Equal(
            ["ipc.dispose", "appLayer.dispose", "push.shutdown", "push.dispose",
             "signals.dispose", "router.dispose", "hid.dispose"],
            log.Entries);
    }

    [Fact]
    public void SwitchProfile_RescopesHidThenRescansRouter()
    {
        var (stack, log) = Build();
        stack.SwitchProfile(new CorneProfile());
        Assert.Equal(["hid.profile", "router.rescan"], log.Entries);
    }
}
