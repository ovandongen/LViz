using System.Text.Json.Serialization;
using ZmkHidProtocol.Capabilities;

namespace LViz.Core.Settings;

/// <summary>
/// The action kind of one <see cref="PipelineStep"/>. Discriminates which of the
/// step's optional fields carry meaning. Serialized as the case-insensitive enum
/// name (like <see cref="AutoSwitchFallbackMode"/>) so settings files stay
/// human-readable and survive field reordering.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PipelineStepKind>))]
public enum PipelineStepKind
{
    /// <summary>Change a layer: on the visualized keyboard when
    /// <see cref="PipelineStep.TargetDeviceKey"/> is empty (the overlay-aware
    /// <c>SetLayerState</c> push), or on a bus device that advertises a
    /// <c>core.layer.*</c> handle via <see cref="PipelineStep.LayerAction"/>.</summary>
    KeyboardLayer,

    /// <summary>Send <c>core.rgb.set</c> to a bus device (<see cref="PipelineStep.TargetDeviceKey"/> + <see cref="PipelineStep.Rgb"/>).</summary>
    Rgb,

    /// <summary>Send a routable pointing action to a bus device
    /// (<see cref="PipelineStep.TargetDeviceKey"/> + <see cref="PipelineStep.PointingActionByte"/> + <see cref="PipelineStep.PointingValue"/>).</summary>
    Pointing,

    /// <summary>Launch an app / file / URL on the host (<see cref="PipelineStep.LaunchTarget"/>).</summary>
    Launch,

    /// <summary>Run a shell command on the host (<see cref="PipelineStep.ShellCommand"/>).</summary>
    Shell,

    /// <summary>Pause before the next step (<see cref="PipelineStep.DelayMs"/>).</summary>
    Delay,

    /// <summary>Run another pipeline by name (<see cref="PipelineStep.PipelineRef"/>)
    /// inline at this point, for reuse. Cycles are broken at run time and rejected at
    /// edit time (see <see cref="PipelineGraph"/>).</summary>
    Pipeline,
}

/// <summary>
/// The layer action a device-targeted <see cref="PipelineStepKind.KeyboardLayer"/>
/// step sends, when it advertises the matching <c>core.layer.*</c> handle. The
/// visualized-keyboard layer step (empty <see cref="PipelineStep.TargetDeviceKey"/>)
/// ignores this and uses the overlay-aware push. String-serialized like
/// <see cref="PipelineStepKind"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PipelineLayerAction>))]
public enum PipelineLayerAction
{
    /// <summary><c>core.layer.setBase</c> (0xF4): set the base/default layer. Absolute, fire-and-forget.</summary>
    SetBase,

    /// <summary><c>core.layer.activate</c> (0xF3): activate a layer. Confirm-flagged.</summary>
    Activate,

    /// <summary><c>core.layer.deactivate</c> (0xF2): deactivate a layer. Confirm-flagged.</summary>
    Deactivate,
}

/// <summary>
/// One step in an <see cref="ActionPipeline"/>. A flat record (mirroring
/// <see cref="MouseLayerSettings"/>' style) rather than a polymorphic union, so
/// JSON serialization stays trivial under the reflection-based STJ used by
/// <see cref="SettingsService"/>: only the fields relevant to <see cref="Kind"/>
/// are set, the rest are null. Device steps (<see cref="PipelineStepKind.Rgb"/> /
/// <see cref="PipelineStepKind.Pointing"/>, and a <see cref="PipelineStepKind.KeyboardLayer"/>
/// with a non-empty <see cref="TargetDeviceKey"/>) address their target by the
/// router's stable device key and are skipped at run time if the device is absent.
/// </summary>
public sealed record PipelineStep(
    PipelineStepKind Kind,
    string? TargetDeviceKey = null,
    int? LayerIndex = null,
    PipelineLayerAction? LayerAction = null,
    RgbSet? Rgb = null,
    byte? PointingActionByte = null,
    uint? PointingValue = null,
    string? LaunchTarget = null,
    string? ShellCommand = null,
    int? DelayMs = null,
    string? PipelineRef = null);
