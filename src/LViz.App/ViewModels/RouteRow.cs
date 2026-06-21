using LViz.App.Localization;

namespace LViz.App.ViewModels;

/// <summary>A (trigger capability, source device) route group with its
/// candidate handler devices.</summary>
public sealed class RouteRow
{
    public RouteRow(string capabilityId, string sourceKey, string sourceName, IReadOnlyList<RouteHandlerRow> handlers)
    {
        CapabilityId = capabilityId;
        SourceKey = sourceKey;
        SourceName = sourceName;
        Handlers = handlers;
        Title = Loc.Instance.Format("Settings_DeviceRouting_RouteTitleFormat", sourceName, capabilityId);
    }

    public string CapabilityId { get; }
    public string SourceKey { get; }
    public string SourceName { get; }

    /// <summary>"{source} · {capability}" header for the route group.</summary>
    public string Title { get; }

    public IReadOnlyList<RouteHandlerRow> Handlers { get; }
}
