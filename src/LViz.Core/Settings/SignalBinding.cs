namespace LViz.Core.Settings;

/// <summary>
/// Binds an inbound <c>signal.fire</c> id (the opaque uint8 a device fires; see
/// <c>signal-capability-spec.md</c>) <em>from a specific source device</em> to the
/// <see cref="ActionPipeline"/> the host runs in response. The signal id space is
/// per-device: the same id from two devices is two distinct bindings, so
/// <see cref="SourceDeviceKey"/> (the router's stable device key) is part of the
/// match — without it the Bean's signal 0 would collide with the keyboard's.
/// An empty key (e.g. a pre-source persisted binding) matches no live device and
/// is inert until re-pointed. <see cref="PipelineName"/> references an
/// <see cref="ActionPipeline.Name"/> in <see cref="UserSettings.ActionPipelines"/>;
/// a dangling name (pipeline deleted/renamed) resolves to nothing and the signal
/// is ignored.
/// </summary>
public sealed record SignalBinding(int SignalId, string SourceDeviceKey, string PipelineName);
