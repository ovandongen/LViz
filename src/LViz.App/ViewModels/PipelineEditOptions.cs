using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>A selectable target device for a device-addressed pipeline step
/// (RGB / pointing / device-targeted layer). <see cref="StableKey"/> is what's
/// persisted on the step (empty = the "visualized keyboard" sentinel);
/// <see cref="DisplayName"/> is the combo label. <see cref="HandledCapabilities"/>
/// (the device manifest's handled ids) lets a step filter which actions the device
/// can take — e.g. which of the <c>core.layer.*</c> actions to offer.</summary>
public sealed record PipelineTargetOption(
    string StableKey, string DisplayName, bool IsKeyboard, IReadOnlySet<string> HandledCapabilities);

/// <summary>A selectable layer action for a device-targeted layer step:
/// <see cref="Action"/> is stored on the step, <see cref="Label"/> the combo
/// label. Only the actions the target device advertises are offered.</summary>
public sealed record PipelineLayerActionOption(PipelineLayerAction Action, string Label);

/// <summary>A selectable routable pointing action for a pointing step:
/// <see cref="ActionByte"/> is the wire byte stored on the step,
/// <see cref="Label"/> the combo label.</summary>
public sealed record PointingActionOption(byte ActionByte, string Label);

/// <summary>A selectable <see cref="PipelineStepKind"/> with a localized label,
/// for the per-step kind picker and the "add step" selector.</summary>
public sealed record PipelineStepKindOption(PipelineStepKind Kind, string Label);
