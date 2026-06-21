using LViz.Core.Diagnostics;
using LViz.Core.Settings;
using ZmkHidProtocol.Capabilities;
using ZmkHidProtocol.Protocol;

namespace LViz.App.Services;

/// <summary>
/// Bridges inbound <c>signal.fire</c> (0xC0) reports to action-pipeline runs.
/// LViz is the <em>handler</em> for <c>signal.fire</c> — the id is opaque on the
/// wire and all meaning is host config (see <c>signal-capability-spec.md</c>), so
/// this is deliberately not part of the router's device→device forwarding
/// (0xC0 isn't a routable action; it stays out of
/// <c>CapabilityCatalog.ActionIdByByte</c>).
///
/// <para>Subscribes a report handler to every device the router knows about (the
/// ZMK keyboard with <c>CONFIG_HID_VIZ_SIGNAL</c> is the primary emitter),
/// reconciling on <see cref="ICapabilityRouter.RoutesChanged"/>. On a fire it
/// live-reads settings so config edits take effect without restart, resolves the
/// bound pipeline, and runs it off the read thread via <see cref="Task.Run(Func{Task})"/>.</para>
/// </summary>
public interface ISignalDispatcher : IDisposable
{
    /// <summary>Begin listening: subscribe to router changes and attach handlers to current devices.</summary>
    void Start();

    /// <summary>Stop listening and drop all device subscriptions. Idempotent.</summary>
    void Stop();
}

public sealed class SignalDispatcher : ISignalDispatcher
{
    private readonly ICapabilityRouter _router;
    private readonly ISettingsService _settings;
    private readonly IPipelineRunner _runner;

    private readonly object _subLock = new();
    private readonly Dictionary<ICapabilityDevice, Action<ReadOnlyMemory<byte>>> _subscriptions = new();
    private bool _started;
    private bool _disposed;

    public SignalDispatcher(ICapabilityRouter router, ISettingsService settings, IPipelineRunner runner)
    {
        _router = router;
        _settings = settings;
        _runner = runner;
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _router.RoutesChanged += OnRoutesChanged;
        ReconcileSubscriptions();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _router.RoutesChanged -= OnRoutesChanged;
        lock (_subLock)
        {
            foreach (var (device, handler) in _subscriptions)
                device.ReportReceived -= handler;
            _subscriptions.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void OnRoutesChanged() => ReconcileSubscriptions();

    // Attach one report handler per live device, detach handlers for devices that
    // vanished. Mirrors CapabilityRouter.ReconcileSubscriptions — multiple
    // subscribers per device coexist fine.
    private void ReconcileSubscriptions()
    {
        lock (_subLock)
        {
            if (!_started) return;

            var present = new HashSet<ICapabilityDevice>();
            foreach (var info in _router.Devices)
            {
                var device = _router.FindDevice(info.StableKey);
                if (device is not null) present.Add(device);
            }

            foreach (var device in _subscriptions.Keys.ToList())
            {
                if (present.Contains(device)) continue;
                device.ReportReceived -= _subscriptions[device];
                _subscriptions.Remove(device);
            }

            foreach (var device in present)
            {
                if (_subscriptions.ContainsKey(device)) continue;
                // Capture the firing device so its source key can be resolved at
                // dispatch — signal ids are per-device, the same id from two
                // devices is two distinct bindings.
                Action<ReadOnlyMemory<byte>> handler = report => OnDeviceReport(device, report);
                _subscriptions[device] = handler;
                device.ReportReceived += handler;
            }
        }
    }

    // Hot path — fires on a background report thread.
    private void OnDeviceReport(ICapabilityDevice origin, ReadOnlyMemory<byte> report)
    {
        var id = RawHidProtocol.TryParseSignalFire(report.Span);
        if (id is null) return;
        // Resolve against the live snapshot (not subscribe time) so a manifest-race
        // key flip doesn't misroute. A device with no current key can't match a
        // binding, so drop it.
        var sourceKey = _router.StableKeyFor(origin);
        if (string.IsNullOrEmpty(sourceKey))
        {
            DiagnosticLog.Debug("Signal", $"fire id {id} from {origin.DisplayName}: no stable key; ignored");
            return;
        }
        DispatchSignal(id.Value, sourceKey);
    }

    private void DispatchSignal(byte id, string sourceKey)
    {
        // Live-read so edits to bindings/pipelines apply without a restart.
        var settings = _settings.Load();
        var binding = settings.SignalBindings.FirstOrDefault(b =>
            b.SignalId == id && string.Equals(b.SourceDeviceKey, sourceKey, StringComparison.Ordinal));
        if (binding is null)
        {
            DiagnosticLog.Debug("Signal", $"fire id {id} from {sourceKey}: no binding; ignored");
            return;
        }

        DiagnosticLog.Debug("Signal", $"fire id {id} → '{binding.PipelineName}'");
        PipelineNameDispatch.RunByName(_runner, settings, binding.PipelineName, "Signal");
    }
}
