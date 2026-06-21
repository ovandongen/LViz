using LViz.Core.Diagnostics;
using LViz.Core.Settings;
using ZmkHidProtocol.Capabilities;

namespace LViz.App.Services;

/// <summary>
/// One discovered device in the router's current inventory, for the Settings UI.
/// </summary>
/// <param name="StableKey">Persistence identity (configId GUID, else <c>vid:pid:name</c>).</param>
/// <param name="DisplayName">Human-readable label (manifest name falls back to transport label).</param>
/// <param name="IsKeyboard">True for the visualized keyboard (the pipeline adapter).</param>
/// <param name="Manifest">The device's manifest, or null if it didn't answer this scan.</param>
public sealed record RoutedDeviceInfo(string StableKey, string DisplayName, bool IsKeyboard, DeviceManifest? Manifest);

/// <summary>
/// Brokers capability traffic between the keyboard and bus devices: reads each
/// device's manifest, builds a <c>capabilityId → handlers</c> table, and
/// forwards a recognized trigger report verbatim to every handler of that
/// capability except the originator (mirrors the reference hub's
/// <c>build_routes</c> / <c>handle_report</c>). The keyboard and a handler
/// device never see each other; the router is the only broker.
/// </summary>
public interface ICapabilityRouter : IDisposable
{
    /// <summary>Current device inventory (keyboard + bus devices) with derived keys. UI-facing.</summary>
    IReadOnlyList<RoutedDeviceInfo> Devices { get; }

    /// <summary>True only when ≥2 protocol-capable devices are present and ≥1 trigger→handler pair exists.</summary>
    bool IsRoutingAvailable { get; }

    /// <summary>
    /// The live device for a stable key in the current inventory, or null if
    /// absent. Lets app-originated control (the Settings UI) resolve a target
    /// from a <see cref="RoutedDeviceInfo.StableKey"/> without leaking the device
    /// handle into the UI-facing DTO.
    /// </summary>
    ICapabilityDevice? FindDevice(string stableKey);

    /// <summary>
    /// The stable key of a live device in the current inventory, or null if it's
    /// not (currently) inventoried. The reverse of <see cref="FindDevice"/>; lets
    /// an inbound-report consumer (the signal dispatcher) resolve which device
    /// fired. Resolved against the live snapshot, so a manifest-race key flip
    /// reflects immediately rather than sticking a stale subscribe-time key.
    /// </summary>
    string? StableKeyFor(ICapabilityDevice device);

    /// <summary>Raised after a scan changes the inventory/table. Fires on a background thread — marshal for UI.</summary>
    event Action? RoutesChanged;

    /// <summary>Begin discovery: subscribe to bus hot-plug and run the first scan.</summary>
    void Start();

    /// <summary>Stop discovery and forwarding; drop all device subscriptions.</summary>
    void Stop();

    /// <summary>Re-read the master toggle + routing rules into the forwarding cache (after a settings change).</summary>
    void RefreshConfig();

    /// <summary>Re-read every device's manifest and rebuild the table (on profile change or hot-plug).</summary>
    Task RescanAsync();
}

public sealed class CapabilityRouter : ICapabilityRouter
{
    private readonly ICapabilityDevice _keyboard;
    private readonly ICapabilityDeviceBus _bus;
    private readonly ISettingsService _settings;
    private readonly TimeSpan _manifestTimeout;
    private readonly Func<ICapabilityDevice, CancellationToken, Task<DeviceManifest?>> _readManifest;

    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _subLock = new();
    private readonly Dictionary<ICapabilityDevice, Action<ReadOnlyMemory<byte>>> _subscriptions = new();

    private CancellationTokenSource _cts = new();
    private bool _started;
    private bool _disposed;

    // Self-healing retry: a startup scan contends with the keyboard HID pipeline
    // reconnect, so a capable device's manifest read can time out — leaving it
    // out of the routing table AND keyed by its transient transport name instead
    // of its manifest key (so a saved pipeline target stops resolving). When a
    // scan comes back with any device missing a manifest, re-scan a few times
    // with backoff so it heals without waiting for a Settings tab open or an
    // unrelated hot-plug. Bounded so a genuinely non-capable device (always null)
    // doesn't loop. An external trigger (Start / hot-plug / explicit rescan)
    // resets the budget; a clean scan resets it to zero.
    private const int MaxRetryScans = 3;
    private int _retryAttempt;

    // Forwarding hot-path cache — read on background report threads, written on
    // scan / RefreshConfig. Volatile so the swap is visible without a lock.
    private volatile bool _routingEnabled;
    private volatile IReadOnlyList<RoutingRule> _rules = Array.Empty<RoutingRule>();
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public event Action? RoutesChanged;

    /// <param name="manifestReader">
    /// Override for the manifest acquisition (tests inject canned manifests);
    /// defaults to <see cref="ManifestReader.ReadAsync"/> over the wire.
    /// </param>
    public CapabilityRouter(
        ICapabilityDevice keyboard,
        ICapabilityDeviceBus bus,
        ISettingsService settings,
        TimeSpan? manifestTimeout = null,
        Func<ICapabilityDevice, CancellationToken, Task<DeviceManifest?>>? manifestReader = null)
    {
        _keyboard = keyboard;
        _bus = bus;
        _settings = settings;
        _manifestTimeout = manifestTimeout ?? TimeSpan.FromSeconds(2);
        _readManifest = manifestReader ?? DefaultReadManifestAsync;
    }

    public IReadOnlyList<RoutedDeviceInfo> Devices => _snapshot.Devices;
    public bool IsRoutingAvailable => _snapshot.Available;

    public ICapabilityDevice? FindDevice(string stableKey)
        => _snapshot.DeviceByKey.TryGetValue(stableKey, out var device) ? device : null;

    public string? StableKeyFor(ICapabilityDevice device)
        => _snapshot.ByDevice.TryGetValue(device, out var entry) ? entry.StableKey : null;

    public void Start()
    {
        if (_started) return;
        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new();
        }
        _started = true;
        RefreshConfig();
        _bus.DevicesChanged += OnDevicesChanged;
        _ = RescanSafeAsync();
    }

    public void Stop()
    {
        // Idempotent teardown: a scan can create subscriptions even before
        // Start (RescanAsync is callable on its own), so the cleanup can't be
        // gated on _started — only the "was there anything?" notification is.
        _started = false;
        _bus.DevicesChanged -= OnDevicesChanged;
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        lock (_subLock)
        {
            foreach (var (device, handler) in _subscriptions)
                device.ReportReceived -= handler;
            _subscriptions.Clear();
        }
        var hadInventory = _snapshot.Devices.Count > 0;
        _snapshot = Snapshot.Empty;
        if (hadInventory) RaiseRoutesChanged();
    }

    public void RefreshConfig()
    {
        var s = _settings.Load();
        _routingEnabled = s.DeviceRoutingEnabled;
        _rules = s.RoutingRules is { Count: > 0 } r ? r.ToArray() : Array.Empty<RoutingRule>();
    }

    public async Task RescanAsync()
    {
        // Explicit/external request — start with a fresh retry budget.
        _retryAttempt = 0;
        await RescanCoreAsync().ConfigureAwait(false);
    }

    private async Task RescanCoreAsync()
    {
        var ct = _cts.Token;
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try { await ScanAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { _scanGate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts.Dispose();
        _scanGate.Dispose();
    }

    private void OnDevicesChanged(IReadOnlyList<ICapabilityDevice> devices) => _ = RescanSafeAsync();

    private async Task RescanSafeAsync()
    {
        try { await RescanAsync().ConfigureAwait(false); }
        catch (Exception ex) { DiagnosticLog.Warn("CapRoute", $"scan failed: {ex.Message}"); }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        RefreshConfig();

        var devices = new List<ICapabilityDevice> { _keyboard };
        devices.AddRange(_bus.Devices);
        ReconcileSubscriptions(devices);

        var read = await Task.WhenAll(devices.Select(async d =>
            (Device: d, Manifest: await SafeReadManifest(d, ct).ConfigureAwait(false)))).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        var entries = read
            .Select(r => new Entry(r.Device, DeriveStableKey(r.Device, r.Manifest), ReferenceEquals(r.Device, _keyboard), r.Manifest))
            .ToList();

        _snapshot = BuildSnapshot(entries);
        LogScanSummary(entries, _snapshot);
        RaiseRoutesChanged();
        ScheduleRetryIfIncomplete(entries, ct);
    }

    /// <summary>
    /// If any device in this scan returned no manifest, queue a bounded delayed
    /// re-scan with backoff so a read that lost the startup HID race self-heals.
    /// A clean scan clears the budget; once <see cref="MaxRetryScans"/> is spent
    /// we stop (a permanently non-capable device shouldn't loop forever).
    /// </summary>
    private void ScheduleRetryIfIncomplete(IReadOnlyList<Entry> entries, CancellationToken ct)
    {
        if (entries.All(e => e.Manifest is not null)) { _retryAttempt = 0; return; }
        if (_retryAttempt >= MaxRetryScans || ct.IsCancellationRequested) return;

        var attempt = ++_retryAttempt;
        var delay = TimeSpan.FromMilliseconds(500 * (1 << (attempt - 1))); // 500, 1000, 2000ms
        DiagnosticLog.Info("CapRoute",
            $"manifest incomplete; retry {attempt}/{MaxRetryScans} in {delay.TotalMilliseconds:0}ms");
        _ = RetryAfterAsync(delay, ct);
    }

    private async Task RetryAfterAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            await RescanCoreAsync().ConfigureAwait(false); // keeps the retry budget (no reset)
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DiagnosticLog.Warn("CapRoute", $"retry scan failed: {ex.Message}"); }
    }

    /// <summary>
    /// Logs what a scan found — device count, which devices returned a manifest,
    /// each device's triggers/handles, and whether routing came out available.
    /// The router is otherwise silent on success; until the Settings UI exists
    /// this is the only window into the routing table. Scans are infrequent (set
    /// change / profile switch / start), so this does not spam.
    /// </summary>
    private static void LogScanSummary(IReadOnlyList<Entry> entries, Snapshot snapshot)
    {
        var capable = entries.Count(e => e.Manifest is not null);
        DiagnosticLog.Info("CapRoute",
            $"scan: {entries.Count} device(s), {capable} with manifest, routing {(snapshot.Available ? "AVAILABLE" : "unavailable")}");
        foreach (var e in entries)
        {
            if (e.Manifest is null)
            {
                DiagnosticLog.Info("CapRoute", $"  {e.Device.DisplayName} [{e.StableKey}]: no manifest");
                continue;
            }
            var triggers = e.Manifest.Capabilities.Where(c => c.Role == CapabilityRole.Triggers).Select(c => c.Id);
            var handles = e.Manifest.Capabilities.Where(c => c.Role == CapabilityRole.Handles).Select(c => c.Id);
            DiagnosticLog.Info("CapRoute",
                $"  {e.Device.DisplayName} [{e.StableKey}]: triggers=[{string.Join(", ", triggers)}] handles=[{string.Join(", ", handles)}]");
        }
    }

    private void ReconcileSubscriptions(IReadOnlyList<ICapabilityDevice> devices)
    {
        lock (_subLock)
        {
            var present = new HashSet<ICapabilityDevice>(devices);
            foreach (var device in _subscriptions.Keys.ToList())
            {
                if (present.Contains(device)) continue;
                device.ReportReceived -= _subscriptions[device];
                _subscriptions.Remove(device);
            }
            foreach (var device in devices)
            {
                if (_subscriptions.ContainsKey(device)) continue;
                Action<ReadOnlyMemory<byte>> handler = report => OnDeviceReport(device, report);
                _subscriptions[device] = handler;
                device.ReportReceived += handler;
            }
        }
    }

    // ─── Forwarding hot path (background report threads) ─────────────────────

    private void OnDeviceReport(ICapabilityDevice origin, ReadOnlyMemory<byte> report)
    {
        if (!_routingEnabled || report.Length == 0) return;
        if (!CapabilityCatalog.ActionIdByByte.TryGetValue(report.Span[0], out var capabilityId)) return;

        var snapshot = _snapshot;
        if (!snapshot.Handlers.TryGetValue(capabilityId, out var handlers) || handlers.Count == 0) return;

        var originKey = snapshot.ByDevice.TryGetValue(origin, out var originEntry) ? originEntry.StableKey : null;
        var (whitelist, blacklist) = ResolveRules(capabilityId, originKey);

        foreach (var handler in handlers)
        {
            if (ReferenceEquals(handler.Device, origin)) continue;
            if (blacklist?.Contains(handler.StableKey) == true) continue;
            if (whitelist is not null && !whitelist.Contains(handler.StableKey)) continue;
            ForwardTo(handler.Device, report, capabilityId);
        }
    }

    /// <summary>
    /// Splits the rules for one (capability, source) into an enabled-target
    /// whitelist (disambiguation — restricts to named targets) and a
    /// disabled-target blacklist (suppresses named targets). Either may be null
    /// (no such rule), in which case the auto behaviour applies: forward to all
    /// handlers minus originator.
    /// </summary>
    private (HashSet<string>? Whitelist, HashSet<string>? Blacklist) ResolveRules(string capabilityId, string? originKey)
    {
        if (originKey is null) return (null, null);
        HashSet<string>? whitelist = null, blacklist = null;
        foreach (var rule in _rules)
        {
            if (!string.Equals(rule.CapabilityId, capabilityId, StringComparison.Ordinal)) continue;
            if (!string.Equals(rule.SourceDeviceKey, originKey, StringComparison.Ordinal)) continue;
            if (rule.Enabled) (whitelist ??= new()).Add(rule.TargetDeviceKey);
            else (blacklist ??= new()).Add(rule.TargetDeviceKey);
        }
        return (whitelist, blacklist);
    }

    private void ForwardTo(ICapabilityDevice target, ReadOnlyMemory<byte> report, string capabilityId)
    {
        try
        {
            var pending = target.SendReportAsync(report, CancellationToken.None);
            if (!pending.IsCompletedSuccessfully) _ = AwaitForward(pending, target, capabilityId);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("CapRoute", $"forward {capabilityId} → {target.DisplayName} failed: {ex.Message}");
        }
    }

    private static async Task AwaitForward(ValueTask pending, ICapabilityDevice target, string capabilityId)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception ex) { DiagnosticLog.Warn("CapRoute", $"forward {capabilityId} → {target.DisplayName} failed: {ex.Message}"); }
    }

    // ─── Manifest reads + table build ───────────────────────────────────────

    private async Task<DeviceManifest?> DefaultReadManifestAsync(ICapabilityDevice device, CancellationToken ct)
        => await ManifestReader.ReadAsync(device, _manifestTimeout, ct).ConfigureAwait(false);

    private async Task<DeviceManifest?> SafeReadManifest(ICapabilityDevice device, CancellationToken ct)
    {
        try { return await _readManifest(device, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("CapRoute", $"manifest read failed for {device.DisplayName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Stable persistence key: the configId GUID when the manifest carries one,
    /// else <c>vid:pid:name</c>. The transport path is deliberately dropped — it
    /// churns across replug, while the key must survive it.
    /// </summary>
    private static string DeriveStableKey(ICapabilityDevice device, DeviceManifest? manifest)
    {
        var configId = manifest?.ConfigId;
        if (!string.IsNullOrWhiteSpace(configId)) return configId.Trim();

        var name = manifest?.Name;
        if (string.IsNullOrWhiteSpace(name)) name = device.DisplayName;

        // TransportKey is "vid:pid:path" (bus devices) or a bare token (keyboard).
        // Keep the stable vid:pid prefix, drop the volatile path tail.
        var parts = device.TransportKey.Split(':');
        var vidpid = parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : device.TransportKey;
        return $"{vidpid}:{name}";
    }

    private static Snapshot BuildSnapshot(List<Entry> entries)
    {
        var handlers = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        var triggers = new List<(string Id, Entry Source)>();
        var capableCount = 0;

        foreach (var entry in entries)
        {
            if (entry.Manifest is null) continue;
            capableCount++;
            foreach (var capability in entry.Manifest.Capabilities)
            {
                if (capability.Role == CapabilityRole.Handles)
                {
                    if (!handlers.TryGetValue(capability.Id, out var list))
                        handlers[capability.Id] = list = new();
                    list.Add(entry);
                }
                else if (capability.Role == CapabilityRole.Triggers)
                {
                    triggers.Add((capability.Id, entry));
                }
            }
        }

        var hasMatch = triggers.Any(t =>
            handlers.TryGetValue(t.Id, out var hs) && hs.Any(h => !ReferenceEquals(h.Device, t.Source.Device)));

        var handlersRo = handlers.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<Entry>)kv.Value, StringComparer.Ordinal);
        var byDevice = entries.ToDictionary(e => e.Device, e => e);
        // Stable-key → live device for app-originated control. Identical keys
        // collapse (accepted, same as the hub) — first entry wins.
        var deviceByKey = new Dictionary<string, ICapabilityDevice>(StringComparer.Ordinal);
        foreach (var e in entries) deviceByKey.TryAdd(e.StableKey, e.Device);
        var infos = entries
            .Select(e => new RoutedDeviceInfo(e.StableKey, e.Device.DisplayName, e.IsKeyboard, e.Manifest))
            .ToList();

        return new Snapshot(byDevice, deviceByKey, handlersRo, infos, capableCount >= 2 && hasMatch);
    }

    private void RaiseRoutesChanged() => RoutesChanged?.Invoke();

    private sealed record Entry(ICapabilityDevice Device, string StableKey, bool IsKeyboard, DeviceManifest? Manifest);

    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(
            new Dictionary<ICapabilityDevice, Entry>(),
            new Dictionary<string, ICapabilityDevice>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<Entry>>(StringComparer.Ordinal),
            Array.Empty<RoutedDeviceInfo>(),
            false);

        public Snapshot(
            IReadOnlyDictionary<ICapabilityDevice, Entry> byDevice,
            IReadOnlyDictionary<string, ICapabilityDevice> deviceByKey,
            IReadOnlyDictionary<string, IReadOnlyList<Entry>> handlers,
            IReadOnlyList<RoutedDeviceInfo> devices,
            bool available)
        {
            ByDevice = byDevice;
            DeviceByKey = deviceByKey;
            Handlers = handlers;
            Devices = devices;
            Available = available;
        }

        public IReadOnlyDictionary<ICapabilityDevice, Entry> ByDevice { get; }
        public IReadOnlyDictionary<string, ICapabilityDevice> DeviceByKey { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<Entry>> Handlers { get; }
        public IReadOnlyList<RoutedDeviceInfo> Devices { get; }
        public bool Available { get; }
    }
}
