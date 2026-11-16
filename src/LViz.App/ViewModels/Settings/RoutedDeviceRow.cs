using LViz.App.Services;
using ZmkHidProtocol.Capabilities;

namespace LViz.App.ViewModels;

/// <summary>One device in the inventory list / control-target picker.</summary>
public sealed class RoutedDeviceRow
{
    public RoutedDeviceRow(RoutedDeviceInfo info)
    {
        StableKey = info.StableKey;
        DisplayName = info.DisplayName;
        IsKeyboard = info.IsKeyboard;
        HasManifest = info.Manifest is not null;

        Triggers = JoinCapabilities(info, CapabilityRole.Triggers);
        Handles = JoinCapabilities(info, CapabilityRole.Handles);
        HandledCapabilities = HandledCapabilityIds(info.Manifest);
    }

    /// <summary>The set of capability ids a device's manifest handles — the
    /// discovery surface that drives which action widgets/pickers it gets.
    /// Shared by the routing tester and the pipeline step editor.</summary>
    public static IReadOnlySet<string> HandledCapabilityIds(DeviceManifest? manifest) =>
        manifest is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : manifest.Capabilities
                .Where(c => c.Role == CapabilityRole.Handles)
                .Select(c => c.Id)
                .ToHashSet(StringComparer.Ordinal);

    public string StableKey { get; }
    public string DisplayName { get; }
    public bool IsKeyboard { get; }
    public bool HasManifest { get; }

    /// <summary>Capability ids this device handles — drives which control
    /// widgets the Send-action section shows for it.</summary>
    public IReadOnlySet<string> HandledCapabilities { get; }

    /// <summary>Comma-joined trigger capability ids, or "—" when none.</summary>
    public string Triggers { get; }

    /// <summary>Comma-joined handled capability ids, or "—" when none.</summary>
    public string Handles { get; }

    private static string JoinCapabilities(RoutedDeviceInfo info, CapabilityRole role)
    {
        if (info.Manifest is null) return "—";
        var ids = info.Manifest.Capabilities
            .Where(c => c.Role == role)
            .Select(c => c.Id)
            .ToList();
        return ids.Count == 0 ? "—" : string.Join(", ", ids);
    }
}
