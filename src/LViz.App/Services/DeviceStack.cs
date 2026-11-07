using LViz.App.Services.MouseIdle;
using LViz.Core.Layout;
using LViz.Core.Settings;
using ZmkHidProtocol.ActiveWindow;
using ZmkHidProtocol.Transport;

namespace LViz.App.Services;

/// <summary>
/// The device stack: the single place that assembles and tears down everything
/// between the app and the hardware. HID pipeline (the keyboard), capability
/// bus + router (the other raw-HID devices), the action-pipeline runner with
/// its signal / app-layer / IPC triggers, and the layer-push coordinator
/// (AutoSwitch + MouseLayer). Construction is eager but opens nothing except
/// the IPC pipe; <see cref="Start"/> begins hardware discovery.
///
/// <para>The keyboard is opened only by <see cref="Hid"/>: the bus excludes it
/// by the live profile matcher AND by <c>Hid.OpenDevicePath</c>. Keep both.</para>
///
/// <para>Order is load-bearing and lives only here: <see cref="Stop"/> reverts
/// the layer first, then signal listening, router, bus, HID;
/// <see cref="Dispose"/> closes IPC, the app-layer dispatcher, the push
/// coordinator, the signal dispatcher, the router, the bus, then HID.</para>
/// </summary>
public sealed class DeviceStack : IDisposable
{
    private readonly RawHidDeviceBus _bus;
    private readonly ISignalDispatcher _signals;
    private readonly IDisposable _ipcServer;
    private readonly IDisposable _appLayerDispatcher;

    public IHidPipeline Hid { get; }
    public ILayerPushCoordinator Push { get; }
    public ICapabilityRouter Router { get; }
    public IPipelineRunner Pipelines { get; }

    /// <summary>
    /// Product names of the Raw HID devices the bus sees (the shown board is
    /// excluded from the bus, and is never the auto-switch target). Empty while
    /// the stack is stopped. Tests replace it.
    /// </summary>
    public Func<IReadOnlyCollection<string>> AttachedHidNames { get; set; }

    /// <summary>Wires prebuilt parts together; <see cref="Create"/> builds the real ones.</summary>
    internal DeviceStack(
        IHidPipeline hid,
        RawHidDeviceBus bus,
        ICapabilityRouter router,
        IPipelineRunner pipelines,
        ISignalDispatcher signals,
        IDisposable ipcServer,
        ILayerPushCoordinator push,
        IDisposable appLayerDispatcher)
    {
        Hid = hid;
        _bus = bus;
        Router = router;
        Pipelines = pipelines;
        _signals = signals;
        _ipcServer = ipcServer;
        Push = push;
        _appLayerDispatcher = appLayerDispatcher;
        AttachedHidNames = () => BusDeviceNames;
    }

    /// <param name="getRenderedLayer">The layer the view is showing; both push engines read it lazily.</param>
    public static DeviceStack Create(
        IKeyboardProfile profile,
        ISettingsService settings,
        Func<int> getRenderedLayer,
        IActiveWindowMonitor? activeWindowMonitor,
        IMouseIdleMonitor? mouseIdleMonitor,
        ICapabilityControl capabilityControl,
        IHostActionExecutor hostActionExecutor)
    {
        // The underlying RawHidLayerSource doesn't open the device until Start().
        var hid = new HidPipeline(profile);

        // The bus excludes the keyboard via its live profile matcher (the
        // open-path is a defensive secondary exclusion); the router consumes the
        // keyboard as an adapter over the HID pipeline's already-open handle plus
        // the bus's other devices. Opens nothing until Start().
        var bus = new RawHidDeviceBus(hid.KeyboardMatcher, () => hid.OpenDevicePath);
        var router = new CapabilityRouter(new KeyboardCapabilityDevice(hid), bus, settings);

        // signal.fire → action-pipeline. The runner drives bus devices through
        // capability control + the keyboard through the HID layer-push path.
        var runner = new PipelineRunner(
            router,
            capabilityControl,
            hostActionExecutor,
            hid.PushLayer,
            () => settings.Load().ActionPipelines);
        var signals = new SignalDispatcher(router, settings, runner);

        // Host IPC: the CLI ('lviz event …', 'status', 'devices') forwards a line
        // over a named pipe. Live for the app's whole lifetime, independent of
        // the live-tracking toggle.
        var ipcServer = new LvizIpcServer(new IpcCommandService(settings, router, runner).Handle);
        ipcServer.Start();

        // The coordinator owns AutoSwitch + MouseLayer and the handoff between
        // them; both route their pushes through the HID pipeline.
        var push = new LayerPushCoordinator(
            hid, getRenderedLayer, profile, settings, activeWindowMonitor, mouseIdleMonitor);
        // App-focus auto-switch lifecycle → action-pipeline. No Start/Stop: the
        // engine only raises while auto-switch is enabled.
        var appLayer = new AppLayerPipelineDispatcher(push.AutoSwitch, settings, runner);

        return new DeviceStack(hid, bus, router, runner, signals, ipcServer, push, appLayer);
    }

    /// <summary>
    /// Starts live tracking. Discovery (bus + router inventory) runs regardless
    /// of the master routing toggle, which only gates forwarding.
    /// </summary>
    public void Start()
    {
        Hid.Start();
        _bus.Start();
        Router.Start();
        _signals.Start();
    }

    /// <summary>
    /// Stops live tracking. On app exit the keyboard is always returned to the
    /// base layer (nothing else can leave an app-pushed layer once LViz is
    /// gone), superseding the mouse-layer revert. On a plain toggle-off the
    /// in-flight mouse-layer push is reverted instead, before HID goes down, so
    /// the engine's pre-move layer doesn't survive the stop.
    /// </summary>
    public void Stop(bool isAppExit)
    {
        if (isAppExit)
            Push.ForceBaseLayerOnExit(TimeSpan.FromMilliseconds(500));
        else
            Push.RevertMouseLayerForShutdown(TimeSpan.FromMilliseconds(500));
        // Signal listening first (it holds per-device subscriptions), then the
        // router before the bus so forwarding is torn down before handles close.
        _signals.Stop();
        Router.Stop();
        _bus.Stop();
        Hid.Stop();
    }

    /// <summary>
    /// Re-scopes discovery to a newly shown board so a Go60 stops feeding reports
    /// into a Glove80 layout (or vice versa). The keyboard's manifest and the
    /// bus exclusion change with the profile, so the bus matcher is updated and
    /// the routing table rebuilt (fire-and-forget; the router serializes scans).
    /// </summary>
    public void SwitchProfile(IKeyboardProfile profile)
    {
        Hid.SetActiveProfile(profile);
        _bus.SetKeyboardMatcher(Hid.KeyboardMatcher);
        _ = Router.RescanAsync();
    }

    /// <summary>Names of the devices on the bus, for the Settings routing tab.</summary>
    public IReadOnlyList<string> BusDeviceNames => _bus.Devices.Select(d => d.DisplayName).ToList();

    public void Dispose()
    {
        // Stop accepting CLI connections before tearing down the router it queries.
        _ipcServer.Dispose();
        // App-layer dispatcher before the push coordinator: it subscribes to the
        // coordinator's AutoSwitch engine.
        _appLayerDispatcher.Dispose();
        Push.Shutdown();
        Push.Dispose();
        // Signal dispatcher before the router: its per-device report subscriptions
        // come from the router's inventory.
        _signals.Dispose();
        // Router before the bus and HID: it unsubscribes from the keyboard
        // adapter (which rides HID's report stream) and the bus devices.
        Router.Dispose();
        _bus.Dispose();
        Hid.Dispose();
    }
}
