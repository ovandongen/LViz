namespace LViz.Core.Settings;

/// <summary>
/// One capability-routing override. Routing is auto by default — when
/// <see cref="UserSettings.DeviceRoutingEnabled"/> is on, a trigger with a
/// single handler routes with zero config. Rules exist only to <em>disambiguate</em>
/// (when one capability has multiple handlers, pin which device receives it) or
/// to <em>disable</em> a specific route (<see cref="Enabled"/> = false).
///
/// <para>Devices are identified by their <em>stable device key</em> (configId
/// GUID if the manifest carries one, else <c>vid:pid:name</c>) — not the
/// transient transport path — so a rule survives reconnects and replug. The
/// rules live globally on <see cref="UserSettings"/>, not per keyboard profile:
/// routing is about physical devices, not the rendered board.</para>
/// </summary>
/// <param name="CapabilityId">The routed capability, e.g. <c>"pointing.dpi.set"</c>.</param>
/// <param name="SourceDeviceKey">Stable key of the trigger (emitting) device.</param>
/// <param name="TargetDeviceKey">Stable key of the chosen handler device.</param>
/// <param name="Enabled">False disables this source→target route for the capability.</param>
public sealed record RoutingRule(
    string CapabilityId,
    string SourceDeviceKey,
    string TargetDeviceKey,
    bool Enabled = true);
