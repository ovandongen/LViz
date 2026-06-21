using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.Core.Diagnostics;
using LViz.Core.Settings;
using ZmkHidProtocol.Capabilities;

namespace LViz.App.ViewModels;

/// <summary>
/// Settings-scoped view model for the Device Routing tab. Observes the
/// long-lived <see cref="ICapabilityRouter"/> (owned by
/// <see cref="MainWindowViewModel"/>) while the Settings window is open, and
/// surfaces four things:
/// <list type="bullet">
///   <item>the master <see cref="RoutingEnabled"/> opt-in (persisted globally);</item>
///   <item>the live device/capability inventory;</item>
///   <item>the resolved routing table with per-route enable/disable;</item>
///   <item>app-originated control widgets (send DPI / layer action to a device).</item>
/// </list>
/// Transient — built fresh on each Settings open, subscribes to
/// <see cref="ICapabilityRouter.RoutesChanged"/>, and unsubscribes on
/// <see cref="Dispose"/> so a closed window doesn't keep a zombie observer alive.
/// </summary>
public sealed partial class DeviceRoutingViewModel : ObservableObject, IDisposable
{
    private readonly ICapabilityRouter _router;
    private readonly ICapabilityControl _control;
    private readonly ISettingsService _settings;
    private bool _disposed;

    // True while Rebuild() seeds the row collections, so the route handler
    // toggles it sets don't echo back into PersistRoutes mid-rebuild.
    private bool _rebuilding;

    public DeviceRoutingViewModel(ICapabilityRouter router, ICapabilityControl control, ISettingsService settings)
    {
        _router = router;
        _control = control;
        _settings = settings;

        // Seed the toggle straight onto the field so construction doesn't
        // round-trip through OnRoutingEnabledChanged (which would persist).
        _routingEnabled = _settings.Load().DeviceRoutingEnabled;

        _router.RoutesChanged += OnRoutesChanged;
        Rebuild();

        // Rebuild() above only reflects the router's last snapshot, which can be
        // stale or carry a manifest that failed to read on a busy startup scan
        // (HID enumerate/read contends with the keyboard pipeline). Without this,
        // an already-connected capable device shows as "no protocol" until an
        // unrelated hotplug happens to trigger a rescan. Refresh on open so the
        // tab reflects the live capability surface immediately; completion posts a
        // Rebuild via RoutesChanged.
        _ = RefreshOnOpenAsync();
    }

    private async Task RefreshOnOpenAsync()
    {
        try { await _router.RescanAsync().ConfigureAwait(false); }
        catch (Exception ex) { DiagnosticLog.Warn("DeviceRouting", $"open rescan failed: {ex.Message}"); }
    }

    // ── Master toggle ─────────────────────────────────────────────────────

    /// <summary>Master opt-in. When off the bus still discovers (inventory stays
    /// live) but nothing is forwarded; flipping it persists and refreshes the
    /// router's forwarding cache.</summary>
    [ObservableProperty]
    private bool _routingEnabled;

    partial void OnRoutingEnabledChanged(bool value)
    {
        _settings.Update(s => s with { DeviceRoutingEnabled = value }, "DeviceRouting");
        _router.RefreshConfig();
    }

    /// <summary>True when ≥2 capable devices expose a routable trigger→handler pair.</summary>
    public bool IsRoutingAvailable => _router.IsRoutingAvailable;

    /// <summary>True when at least one connected device answered the capability
    /// manifest — i.e. speaks the protocol. The tab is already useful at this
    /// point (the Send-action tester can drive that device), even before a second
    /// device makes routing possible. Drives the tab "lighting up" on a single
    /// capable device rather than only on a routable pair.</summary>
    public bool HasCapableDevice => _router.Devices.Any(d => d.Manifest is not null);
    public bool HasNoCapableDevice => !HasCapableDevice;

    /// <summary>Status line shown once ≥1 capable device is present: full routing
    /// (a trigger→handler pair) vs control-only (a lone capable device).</summary>
    public string RoutingStatus => IsRoutingAvailable
        ? Loc.Instance["Settings_DeviceRouting_Available"]
        : Loc.Instance["Settings_DeviceRouting_ControlReady"];

    // ── Inventory ─────────────────────────────────────────────────────────

    /// <summary>Discovered devices (keyboard + bus) with their capability surface.</summary>
    public ObservableCollection<RoutedDeviceRow> Devices { get; } = new();

    public bool HasDevices => Devices.Count > 0;
    public bool HasNoDevices => Devices.Count == 0;

    // ── Routing table ─────────────────────────────────────────────────────

    /// <summary>One row per (trigger capability, source device) that has at
    /// least one candidate handler on another device.</summary>
    public ObservableCollection<RouteRow> Routes { get; } = new();

    public bool HasRoutes => Routes.Count > 0;
    public bool HasNoRoutes => Routes.Count == 0;

    // ── App-originated control ────────────────────────────────────────────
    // The widgets shown adapt to the selected target's manifest: a pointing row
    // per handled pointing action (DPI / DPI index / drag-scroll / snipe) and
    // only the layer buttons it actually handles (setBase / activate /
    // deactivate). A pointing device shows no layer controls, and vice versa.

    /// <summary>Target device for the control widgets — any device in the
    /// inventory. Changing it rebuilds the widget set from its capabilities.</summary>
    [ObservableProperty]
    private RoutedDeviceRow? _controlTarget;

    partial void OnControlTargetChanged(RoutedDeviceRow? value) => BuildControlsForTarget();

    /// <summary>One row per handled pointing action on the current target.</summary>
    public ObservableCollection<PointingControlRow> PointingControls { get; } = new();

    /// <summary>The RGB editor for the current target, or null when it doesn't
    /// handle <c>core.rgb.set</c>. A single multi-field control (its own edit
    /// logic), unlike the one-row-per-action pointing list.</summary>
    [ObservableProperty]
    private RgbControlRow? _rgbControl;

    [ObservableProperty]
    private int _controlLayerIndex;

    [ObservableProperty]
    private string _controlStatus = "";

    private bool _canSetBase, _canActivate, _canDeactivate;

    public bool HasPointingControls => PointingControls.Count > 0;
    public bool HasRgbControl => RgbControl is not null;
    public bool CanSetBase => _canSetBase;
    public bool CanActivate => _canActivate;
    public bool CanDeactivate => _canDeactivate;
    public bool HasLayerControls => _canSetBase || _canActivate || _canDeactivate;
    public bool HasAnyControls => HasPointingControls || HasRgbControl || HasLayerControls;

    /// <summary>True before a target is picked — prompts the user to choose one.</summary>
    public bool NoTargetSelected => ControlTarget is null;

    /// <summary>True when a target is selected but exposes nothing LViz can send.</summary>
    public bool ShowControlPlaceholder => ControlTarget is not null && !HasAnyControls;

    // App-side presentation for the pointing actions: capability id → (label key,
    // default numeric value — ignored for toggle actions). The wire byte and the
    // value-kind (checkbox vs numeric) come from the CapabilityRegistry, not here;
    // this table only carries the UI concerns the registry doesn't (localized label,
    // sensible default). Keyed by the registry's id constants.
    private static readonly Dictionary<string, (string LabelKey, int Default)> PointingPresentation = new()
    {
        [CapabilityIds.PointingDpiSet] = ("Settings_DeviceRouting_Dpi", 800),
        [CapabilityIds.PointingDpiSetIndex] = ("Settings_DeviceRouting_PointingDpiIndex", 0),
        [CapabilityIds.PointingDragScrollSet] = ("Settings_DeviceRouting_PointingDragScroll", 0),
        [CapabilityIds.PointingSnipeSet] = ("Settings_DeviceRouting_PointingSnipe", 0),
    };

    private void BuildControlsForTarget()
    {
        PointingControls.Clear();
        ControlStatus = "";
        var handled = ControlTarget?.HandledCapabilities;
        if (handled is not null)
        {
            // Pointing actions are the scalar (uint32) registry definitions; iterate
            // in canonical order and render any the target handles.
            foreach (var d in CapabilityRegistry.All)
            {
                if (d.Payload != PayloadShape.Uint32LE || !handled.Contains(d.Id)) continue;
                if (!PointingPresentation.TryGetValue(d.Id, out var p)) continue;
                PointingControls.Add(new PointingControlRow(
                    d.Id, d.WireByte!.Value, Loc.Instance[p.LabelKey], d.Value, p.Default, SendPointingAsync));
            }
        }
        RgbControl = handled?.Contains(CapabilityIds.RgbSet) == true
            ? new RgbControlRow(SendRgbAsync)
            : null;

        _canSetBase = handled?.Contains(CapabilityIds.LayerSetBase) == true;
        _canActivate = handled?.Contains(CapabilityIds.LayerActivate) == true;
        _canDeactivate = handled?.Contains(CapabilityIds.LayerDeactivate) == true;

        OnPropertyChanged(nameof(HasPointingControls));
        OnPropertyChanged(nameof(HasRgbControl));
        OnPropertyChanged(nameof(CanSetBase));
        OnPropertyChanged(nameof(CanActivate));
        OnPropertyChanged(nameof(CanDeactivate));
        OnPropertyChanged(nameof(HasLayerControls));
        OnPropertyChanged(nameof(HasAnyControls));
        OnPropertyChanged(nameof(NoTargetSelected));
        OnPropertyChanged(nameof(ShowControlPlaceholder));
    }

    private Task SendPointingAsync(PointingControlRow row)
        => RunControlAsync(row.Label,
            (device, ct) => _control.SendPointingActionAsync(device, row.ActionByte, row.PayloadValue, ct));

    private Task SendRgbAsync(RgbControlRow row)
        => RunControlAsync(Loc.Instance["Settings_DeviceRouting_Rgb"],
            (device, ct) => _control.SendRgbAsync(device, row.ToRgbSet(), ct));

    [RelayCommand]
    private Task SetBaseLayerAsync()
        => RunControlAsync(
            Loc.Instance["Settings_DeviceRouting_SetBase"],
            (device, ct) => _control.SetLayerBaseAsync(device, ClampLayer(ControlLayerIndex), ct));

    [RelayCommand]
    private Task ActivateLayerAsync()
        => RunControlAsync(
            Loc.Instance["Settings_DeviceRouting_Activate"],
            (device, ct) => _control.ActivateLayerAsync(device, ClampLayer(ControlLayerIndex), ct));

    [RelayCommand]
    private Task DeactivateLayerAsync()
        => RunControlAsync(
            Loc.Instance["Settings_DeviceRouting_Deactivate"],
            (device, ct) => _control.DeactivateLayerAsync(device, ClampLayer(ControlLayerIndex), ct));

    private static byte ClampLayer(int index) => (byte)Math.Clamp(index, 0, 255);

    private async Task RunControlAsync(string actionLabel, Func<ICapabilityDevice, CancellationToken, Task<ControlResult>> send)
    {
        var target = ControlTarget;
        if (target is null)
        {
            ControlStatus = Loc.Instance["Settings_DeviceRouting_ControlNoTarget"];
            return;
        }

        var device = _router.FindDevice(target.StableKey);
        if (device is null)
        {
            // The device vanished between selection and send (unplug / rescan).
            ControlStatus = Loc.Instance["Settings_DeviceRouting_ControlGone"];
            return;
        }

        ControlStatus = Loc.Instance.Format("Settings_DeviceRouting_ControlStatusFormat", actionLabel, Loc.Instance["Settings_DeviceRouting_OutcomeSending"]);
        try
        {
            var result = await send(device, CancellationToken.None);
            ControlStatus = Loc.Instance.Format("Settings_DeviceRouting_ControlStatusFormat", actionLabel, DescribeOutcome(result));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("DeviceRouting", $"control '{actionLabel}' failed: {ex.Message}");
            ControlStatus = Loc.Instance.Format("Settings_DeviceRouting_ControlStatusFormat", actionLabel,
                Loc.Instance.Format("Settings_DeviceRouting_OutcomeFailedDetail", ex.Message));
        }
    }

    private static string DescribeOutcome(ControlResult result) => result.Outcome switch
    {
        ControlOutcome.Sent => Loc.Instance["Settings_DeviceRouting_OutcomeSent"],
        ControlOutcome.Confirmed => Loc.Instance["Settings_DeviceRouting_OutcomeConfirmed"],
        ControlOutcome.TimedOut => Loc.Instance["Settings_DeviceRouting_OutcomeTimedOut"],
        _ => string.IsNullOrEmpty(result.Detail)
            ? Loc.Instance["Settings_DeviceRouting_OutcomeFailed"]
            : Loc.Instance.Format("Settings_DeviceRouting_OutcomeFailedDetail", result.Detail),
    };

    // ── Rebuild from the router snapshot ──────────────────────────────────

    // RoutesChanged fires on a background scan thread; marshal onto the UI
    // thread before touching the bound collections.
    private void OnRoutesChanged() => Dispatcher.UIThread.Post(Rebuild);

    /// <summary>
    /// Rebuilds the inventory + routing table from the router's current
    /// snapshot and the persisted rules. Internal so tests can drive it
    /// directly without pumping the Avalonia dispatcher.
    /// </summary>
    internal void Rebuild()
    {
        if (_disposed) return;
        _rebuilding = true;
        try
        {
            var inventory = _router.Devices;
            var rules = _settings.Load().RoutingRules ?? new List<RoutingRule>();

            var previousTargetKey = ControlTarget?.StableKey;

            Devices.Clear();
            foreach (var info in inventory)
                Devices.Add(new RoutedDeviceRow(info));

            Routes.Clear();
            foreach (var route in BuildRoutes(inventory, rules))
                Routes.Add(route);

            // Preserve the control target selection across the rebuild (rows are
            // recreated), else picking a device then triggering a rescan would
            // silently clear the picker.
            ControlTarget = previousTargetKey is null
                ? null
                : Devices.FirstOrDefault(d => d.StableKey == previousTargetKey);
        }
        finally
        {
            _rebuilding = false;
        }

        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasNoDevices));
        OnPropertyChanged(nameof(HasRoutes));
        OnPropertyChanged(nameof(HasNoRoutes));
        OnPropertyChanged(nameof(IsRoutingAvailable));
        OnPropertyChanged(nameof(HasCapableDevice));
        OnPropertyChanged(nameof(HasNoCapableDevice));
        OnPropertyChanged(nameof(RoutingStatus));
    }

    private IEnumerable<RouteRow> BuildRoutes(IReadOnlyList<RoutedDeviceInfo> inventory, IReadOnlyList<RoutingRule> rules)
    {
        // capabilityId → handler devices that advertise it.
        var handlersByCapability = new Dictionary<string, List<RoutedDeviceInfo>>(StringComparer.Ordinal);
        foreach (var device in inventory)
        {
            if (device.Manifest is null) continue;
            foreach (var cap in device.Manifest.Capabilities)
            {
                if (cap.Role != CapabilityRole.Handles) continue;
                if (!handlersByCapability.TryGetValue(cap.Id, out var list))
                    handlersByCapability[cap.Id] = list = new();
                list.Add(device);
            }
        }

        foreach (var source in inventory)
        {
            if (source.Manifest is null) continue;
            foreach (var cap in source.Manifest.Capabilities)
            {
                if (cap.Role != CapabilityRole.Triggers) continue;
                if (!handlersByCapability.TryGetValue(cap.Id, out var candidates)) continue;

                var handlerRows = candidates
                    .Where(h => h.StableKey != source.StableKey)
                    .Select(h => new RouteHandlerRow(
                        h.StableKey,
                        h.DisplayName,
                        IsRoutedByRules(rules, cap.Id, source.StableKey, h.StableKey),
                        OnRouteToggled))
                    .ToList();

                if (handlerRows.Count > 0)
                    yield return new RouteRow(cap.Id, source.StableKey, source.DisplayName, handlerRows);
            }
        }
    }

    /// <summary>Mirrors <c>CapabilityRouter.ResolveRules</c>: a target is routed
    /// unless blacklisted, and (if any whitelist rule exists for this
    /// capability+source) only when explicitly whitelisted.</summary>
    private static bool IsRoutedByRules(IReadOnlyList<RoutingRule> rules, string capabilityId, string sourceKey, string targetKey)
    {
        HashSet<string>? whitelist = null, blacklist = null;
        foreach (var rule in rules)
        {
            if (rule.CapabilityId != capabilityId || rule.SourceDeviceKey != sourceKey) continue;
            if (rule.Enabled) (whitelist ??= new()).Add(rule.TargetDeviceKey);
            else (blacklist ??= new()).Add(rule.TargetDeviceKey);
        }
        if (blacklist?.Contains(targetKey) == true) return false;
        if (whitelist is not null && !whitelist.Contains(targetKey)) return false;
        return true;
    }

    private void OnRouteToggled()
    {
        if (_rebuilding) return;
        PersistRoutes();
    }

    /// <summary>
    /// Translates the checkbox state of every displayed route group into the
    /// persisted <see cref="RoutingRule"/> set, normalising to the smallest
    /// expression: all handlers on → no rules (auto-broadcast, picks up future
    /// handlers); none on → a disable rule per handler; a strict subset on →
    /// an enable (whitelist) rule per checked handler. Rules for capability/
    /// source pairs not currently displayed (e.g. an unplugged device) are
    /// preserved untouched so they reapply on replug.
    /// </summary>
    private void PersistRoutes()
    {
        var displayedKeys = new HashSet<(string, string)>(
            Routes.Select(r => (r.CapabilityId, r.SourceKey)));

        var existing = _settings.Load().RoutingRules ?? new List<RoutingRule>();
        var newRules = existing
            .Where(r => !displayedKeys.Contains((r.CapabilityId, r.SourceDeviceKey)))
            .ToList();

        foreach (var route in Routes)
        {
            var routed = route.Handlers.Where(h => h.Routed).ToList();
            if (routed.Count == route.Handlers.Count)
                continue; // all on → no rule needed
            if (routed.Count == 0)
            {
                // route to none → disable every candidate
                foreach (var h in route.Handlers)
                    newRules.Add(new RoutingRule(route.CapabilityId, route.SourceKey, h.TargetKey, Enabled: false));
            }
            else
            {
                // strict subset → whitelist the chosen handlers
                foreach (var h in routed)
                    newRules.Add(new RoutingRule(route.CapabilityId, route.SourceKey, h.TargetKey, Enabled: true));
            }
        }

        _settings.Update(s => s with { RoutingRules = newRules }, "DeviceRouting");
        _router.RefreshConfig();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.RoutesChanged -= OnRoutesChanged;
    }
}
